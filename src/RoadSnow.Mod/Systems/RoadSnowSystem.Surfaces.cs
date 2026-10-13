using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using RoadSnow.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private readonly Dictionary<Entity, SurfacePlan> _surfacePlans = new();
    private readonly HashSet<Entity> _seenSurfacePlans = new();
    private readonly List<Vector3> _coverageReplacements = new();
    private readonly HashSet<Vector3> _coverageCenters = new();
    private long _surfacePlanBuilds,
        _surfacePlanReuses;

    private sealed class SurfacePlan
    {
        public SurfacePlanInput Input;
        public float CheckAfter;
        public readonly List<PatchRequest> Requests = new();
        public readonly HashSet<PatchKey> Keys = new();
    }

    private SurfacePlanInput CaptureSurfaceInput(
        Entity entity,
        EdgeGeometry geometry,
        Composition composition,
        NetCompositionData data
    )
    {
        var input = new SurfacePlanInput
        {
            Geometry = geometry,
            Edge = EntityManager.GetComponentData<Edge>(entity),
            Composition = composition,
            EdgeData = data,
        };
        if (EntityManager.HasComponent<StartNodeGeometry>(entity))
        {
            input.Start = EntityManager.GetComponentData<StartNodeGeometry>(entity);
            input.Flags |= 1;
        }
        if (EntityManager.HasComponent<EndNodeGeometry>(entity))
        {
            input.End = EntityManager.GetComponentData<EndNodeGeometry>(entity);
            input.Flags |= 2;
        }
        if (SupportsSnowComposition(composition.m_StartNode))
        {
            input.StartData = EntityManager.GetComponentData<NetCompositionData>(
                composition.m_StartNode
            );
            input.Flags |= 4;
            if (_nativeJunctions.IsReady(EntityManager, composition.m_StartNode))
                input.Flags |= 16;
        }
        if (SupportsSnowComposition(composition.m_EndNode))
        {
            input.EndData = EntityManager.GetComponentData<NetCompositionData>(
                composition.m_EndNode
            );
            input.Flags |= 8;
            if (_nativeJunctions.IsReady(EntityManager, composition.m_EndNode))
                input.Flags |= 32;
        }
        return input;
    }

    private void AcceptSurfacePlan(Entity edge, SurfacePlan plan, float now)
    {
        _visibleNetworks++;
        _desiredByEdge[edge] = plan.Keys;
        foreach (var request in plan.Requests)
        {
            var section = request.Section;
            float middle = Mathf.Clamp(
                section.Data.m_MiddleOffset / section.Data.m_Width + .5f,
                .01f,
                .99f
            );
            float along = Mathf.Lerp(
                request.Chunk / (float)request.Chunks,
                (request.Chunk + 1f) / request.Chunks,
                .5f
            );
            Vector3 center = SurfaceGeometry.Position(
                section.Segment,
                section.Node,
                middle,
                .5f,
                along
            );
            center.y += Mathf.Clamp(Mod.Settings.SurfaceOffsetCm, 1, 12) / 100f + .03f;
            foreach (var way in section.Surfaces)
                if (way.Position.x - way.Width / 2 <= 0 && way.Position.x + way.Width / 2 >= 0)
                {
                    center.y += way.Position.y;
                    break;
                }
            _coverageCenters.Add(CachedCoverage.Key(center));
            _desired.Add(request.Key);
            if (_patches.TryGetValue(request.Key, out var patch))
            {
                patch.Active = true;
                patch.LastSeen = now;
            }
            else if (_queued.Add(request.Key))
                _pending.Add(request);
            if (request.Detailed)
                _detailedParts++;
            if (request.Section.Node.HasValue)
                _nodeParts++;
        }
        ReconcileEdge(edge, now);
    }

    // network compositions provide the surface shape. discovery and mesh creation have
    //separate budgets so rebuilding roads does not force all patches into one frame
    private IEnumerable<bool> RefreshPatches(Camera camera, float now)
    {
        var candidates = new List<(Entity Edge, EdgeGeometry Geometry, float Distance)>();
        Vector3 position = camera.transform.position;
        using (var entities = _roadQuery.ToEntityArray(Allocator.Temp))
        {
            using (var geometries = _roadQuery.ToComponentDataArray<EdgeGeometry>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var bounds = geometries[i].m_Bounds;
                    float distance = CoveragePolicy.HorizontalDistanceSquared(
                        position.x,
                        position.z,
                        bounds.min.x,
                        bounds.min.z,
                        bounds.max.x,
                        bounds.max.z
                    );
                    candidates.Add((entities[i], geometries[i], distance));
                }
            }
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        _desired.Clear();
        _coverageCenters.Clear();
        _desiredByEdge.Clear();
        _pending.Clear();
        _queued.Clear();
        _pendingHead = 0;
        _visibleNetworks = _detailedParts = _nodeParts = 0;
        _nativeJunctions.BeginRefresh();
        _seenSurfacePlans.Clear();
        foreach (var candidate in candidates)
        {
            yield return true;
            if (
                !EntityManager.Exists(candidate.Edge)
                || !EntityManager.HasComponent<Composition>(candidate.Edge)
            )
            {
                continue;
            }

            var composition = EntityManager.GetComponentData<Composition>(candidate.Edge);
            if (!EntityManager.HasComponent<NetCompositionData>(composition.m_Edge))
            {
                continue;
            }

            var data = EntityManager.GetComponentData<NetCompositionData>(composition.m_Edge);
            if (!SupportsSnowComposition(composition.m_Edge))
            {
                continue;
            }

            _seenSurfacePlans.Add(candidate.Edge);
            var input = CaptureSurfaceInput(candidate.Edge, candidate.Geometry, composition, data);
            if (
                _surfacePlans.TryGetValue(candidate.Edge, out var plan)
                && now < plan.CheckAfter
                && plan.Input.SameAs(input)
            )
            {
                AcceptSurfacePlan(candidate.Edge, plan, now);
                _surfacePlanReuses++;
                continue;
            }
            plan = new SurfacePlan
            {
                Input = input,
                CheckAfter = now + 30 + (candidate.Edge.Index & 31),
            };
            _surfacePlanBuilds++;

            var sections = new List<SurfaceSection>
            {
                new(
                    candidate.Geometry.m_Start,
                    null,
                    data,
                    CollectSurfaces(composition.m_Edge),
                    0,
                    candidate.Edge
                ),
                new(
                    candidate.Geometry.m_End,
                    null,
                    data,
                    CollectSurfaces(composition.m_Edge),
                    1,
                    candidate.Edge
                ),
            };
            var edge = input.Edge;
            AddNodeSection(
                sections,
                candidate.Edge,
                composition.m_StartNode,
                edge.m_Start,
                false,
                composition.m_Edge
            );
            AddNodeSection(
                sections,
                candidate.Edge,
                composition.m_EndNode,
                edge.m_End,
                true,
                composition.m_Edge
            );
            int detailCost = 0;
            foreach (var section in sections)
            {
                if (section.Surfaces.Count > 0 && section.Length >= 0.1f)
                {
                    detailCost += Math.Max(
                        1,
                        (int)Math.Ceiling(section.Length / (section.Pedestrian ? 24 : 48))
                    );
                }
            }

            if (detailCost == 0)
            {
                _surfacePlans.Remove(candidate.Edge);
                continue;
            }

            bool detailed = false;
            foreach (var section in sections)
            {
                if (section.Surfaces.Count == 0)
                {
                    continue;
                }

                float length = section.Length;
                if (length < 0.1f || length > 4096)
                {
                    continue;
                }

                int chunks = detailed
                    ? Math.Max(1, (int)Math.Ceiling(length / (section.Pedestrian ? 24 : 48)))
                    : 1;
                for (int chunk = 0; chunk < chunks; chunk++)
                {
                    int signature = GeometrySignature(
                        section.Segment,
                        new float3(section.Data.m_MiddleOffset, 0, 0),
                        section.Data.m_Width
                    );
                    foreach (var way in section.Surfaces)
                    {
                        signature = unchecked(
                            signature * 31
                            + (int)math.hash(way.Position)
                            + (int)math.asuint(way.Width)
                        );
                    }

                    if (section.Node.HasValue)
                    {
                        var node = section.Node.Value;
                        signature = unchecked(
                            signature * 31
                            + GeometrySignature(
                                node.m_Right,
                                new float3(node.m_MiddleRadius),
                                section.Data.m_Width
                            )
                        );
                    }

                    if (section.Node.HasValue)
                    {
                        signature = unchecked(
                            signature * 31
                            + (int)StablePositionHash(section.Node.Value.m_Middle.a)
                            + (int)StablePositionHash(section.Node.Value.m_Middle.b)
                            + (int)StablePositionHash(section.Node.Value.m_Middle.c)
                            + (int)StablePositionHash(section.Node.Value.m_Middle.d)
                            + (int)math.hash(section.Data.m_SyncVertexOffsetsLeft)
                            + (int)math.hash(section.Data.m_SyncVertexOffsetsRight)
                            + (int)math.hash(section.Node.Value.m_SyncVertexTargetsLeft)
                            + (int)math.hash(section.Node.Value.m_SyncVertexTargetsRight)
                        );
                        // Keep the fallback while streaming; replace it once the
                        // native pieces arrive, even if the road itself did not change.
                        signature = unchecked(
                            signature * 31
                            + (_nativeJunctions.IsReady(EntityManager, section.Composition) ? 1 : 0)
                        );
                    }

                    var key = new PatchKey(
                        candidate.Edge,
                        0,
                        section.Id,
                        chunk,
                        unchecked(signature * 31 + (detailed ? 1 : 0))
                    );
                    plan.Keys.Add(key);
                    plan.Requests.Add(
                        new PatchRequest(key, candidate.Edge, section, chunk, chunks, detailed)
                    );
                }
            }

            _surfacePlans[candidate.Edge] = plan;
            AcceptSurfacePlan(candidate.Edge, plan, now);
        }

        foreach (var entity in _surfacePlans.Keys.ToArray())
            if (!_seenSurfacePlans.Contains(entity))
                _surfacePlans.Remove(entity);

        foreach (var entity in _keysByEdge.Keys.ToArray())
        {
            if (!_desiredByEdge.ContainsKey(entity))
            {
                ReconcileEdge(entity, now);
            }
        }

        _cachedCoverage.RetireUnseen(_coverageCenters);
        _activePatches.RemoveAll(p => !p.Active);
        foreach (var owner in _patchesByRoad.Keys.ToArray())
        {
            _patchesByRoad[owner].RemoveAll(p => !p.Active);
            if (_patchesByRoad[owner].Count == 0)
            {
                _patchesByRoad.Remove(owner);
            }
        }
    }

    private readonly NativeJunctionSurface _nativeJunctions = new();

    private void CreatePending(float now, bool loading = false)
    {
        long started = Stopwatch.GetTimestamp();
        int count = 0;
        var terrain = _pendingHead < _pending.Count ? _terrainSystem.GetHeightData() : default;
        while (
            _pendingHead < _pending.Count
            && count < (loading ? 128 : 1)
            && (loading || Milliseconds(started) < .6)
        )
        {
            var request = _pending[_pendingHead++];
            _queued.Remove(request.Key);
            if (!_desired.Contains(request.Key) || _patches.ContainsKey(request.Key))
            {
                continue;
            }

            CreatePatch(request, now, terrain);
            count++;
        }

        _lastCreateMs = Milliseconds(started);
    }

    private void CreatePatch(PatchRequest request, float now, TerrainHeightData terrain)
    {
        var section = request.Section;
        var profile = section
            .Surfaces.Select(w => new SurfaceBand(
                w.Position.x - w.Width / 2,
                w.Position.x + w.Width / 2,
                w.Position.y,
                w.Pedestrian
            ))
            .ToArray();
        float surfaceLift = Mathf.Clamp(Mod.Settings.SurfaceOffsetCm, 1, 12) / 100f + .03f;
        var nativeJunction = section.Node.HasValue
            ? _nativeJunctions.Build(
                EntityManager,
                section.Composition,
                section.Data,
                section.Node.Value,
                surfaceLift,
                terrain
            )
            : null;
        var patch = new RoadPatch(
            section.Segment,
            request.Chunk / (float)request.Chunks,
            (request.Chunk + 1f) / request.Chunks,
            0,
            1,
            section.Data.m_Width,
            section.Length / request.Chunks,
            surfaceLift,
            _atlas,
            unchecked(1984 + request.Edge.Index * 37),
            request.Chunk * section.Length / request.Chunks,
            section.Pedestrian,
            request.Detailed,
            section.Node,
            section.Data.m_MiddleOffset / section.Data.m_Width + 0.5f,
            profile,
            section.Data.m_SyncVertexOffsetsLeft,
            section.Data.m_SyncVertexOffsetsRight,
            nativeJunction
        );
        patch.LastSeen = now;
        patch.LastSnowTime = now;
        patch.LastSimulationFrame = _simulationSystem.frameIndex;
        _laneWearMask.Register(EntityManager, section.Owner, _simulationSystem.frameIndex);
        if (_isPreviewActive || _snowSnapshot != null)
        {
            patch.FollowSnow(
                _isPreviewActive
                    ? 15
                    : patch.Observation.Observe(SampleSnow(patch.Center), _snowSnapshotVersion),
                0
            );
        }

        if (_residualHistory.TryGetValue(patch.Center, out var retained))
            patch.RestoreResidual(retained);
        if (
            _completedSectionOwners.TryGetValue(section.Owner, out double plowedAt)
            || _sectionPlowHistory.TryGetValue(CachedCoverage.Key(patch.Center), out plowedAt)
            || _cachedCoverage.TrySectionPlow(patch.Center, out plowedAt)
        )
        {
            patch.RestoreSectionPlow(plowedAt, _snowfall.TotalMm);
            _sectionPlowHistory[CachedCoverage.Key(patch.Center)] = plowedAt;
        }
        patch.Upload(true);
        _coverageReplacements.Add(patch.Center);
        _patches.Add(request.Key, patch);
        _activePatches.Add(patch);
        if (!_keysByEdge.TryGetValue(request.Edge, out var keys))
        {
            _keysByEdge.Add(request.Edge, keys = new List<PatchKey>());
        }

        keys.Add(request.Key);
        AddTrafficPatch(request.Edge, patch);
        if (section.Owner != request.Edge)
        {
            AddTrafficPatch(section.Owner, patch);
        }

        ReconcileEdge(request.Edge, now);
    }

    private void ReconcileEdge(Entity edge, float now)
    {
        if (!_keysByEdge.TryGetValue(edge, out var keys))
        {
            return;
        }

        bool replacing = _desiredByEdge.TryGetValue(edge, out var desired) && desired.Count > 0;
        bool complete = replacing && desired.All(k => _patches.ContainsKey(k));
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            var key = keys[i];
            var patch = _patches[key];
            if (_desired.Contains(key))
            {
                continue;
            }

            // Streamed native meshes may take longer than the retirement grace
            // period. Keep coverage until their queued replacement is complete.
            if (!complete && (replacing || now - patch.LastSeen <= 10))
            {
                continue;
            }

            _snowBanks?.Retire(patch);
            patch.Active = false;
            _residualHistory.Remove(patch.Center);
            patch.Dispose();
            _patches.Remove(key);
            keys.RemoveAt(i);
        }

        if (keys.Count == 0)
        {
            _keysByEdge.Remove(edge);
        }
    }

    private void UpdateSnowBudget(float now)
    {
        _snowBudgetMs = 0;
        if (_activePatches.Count == 0)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        int count = 0;
        while (count < _activePatches.Count && count < 256 && Milliseconds(started) < .4)
        {
            if (_snowCursor >= _activePatches.Count)
            {
                _snowCursor = 0;
            }

            var patch = _activePatches[_snowCursor++];
            count++;
            if (!patch.Active || now - patch.LastSnowTime < 0.5f)
            {
                continue;
            }

            uint frame = _simulationSystem.frameIndex;
            uint elapsed = unchecked(frame - patch.LastSimulationFrame);
            patch.LastSimulationFrame = frame;
            if (_isPreviewActive || _snowSnapshot != null)
            {
                float target = _isPreviewActive
                    ? 15
                    : patch.Observation.Observe(SampleSnow(patch.Center), _snowSnapshotVersion);
                patch.FollowSnow(
                    target,
                    Mathf.Clamp(now - patch.LastSnowTime, 0, 10),
                    _isPreviewActive || _climateSystem.isSnowing,
                    elapsed <= TimeSystem.kTicksPerDay
                        ? elapsed * (24f / TimeSystem.kTicksPerDay)
                        : 0,
                    Mod.Settings.ResidualMeltHours,
                    _climateSystem.temperature
                );
            }

            _residualHistory[patch.Center] = patch.ResidualDepth;
            patch.ObservePlowSnowfall(_snowfall.TotalMm);
            patch.LastSnowTime = now;
            if (patch.Upload(true))
            {
                _uploadedParts++;
            }
        }

        _snowBudgetMs = Milliseconds(started);
    }

    private void AddTrafficPatch(Entity owner, RoadPatch patch)
    {
        if (!_patchesByRoad.TryGetValue(owner, out var list))
        {
            _patchesByRoad.Add(owner, list = new List<RoadPatch>());
        }

        list.Add(patch);
    }

    private void AddNodeSection(
        List<SurfaceSection> sections,
        Entity edge,
        Entity composition,
        Entity node,
        bool end,
        Entity edgeComposition
    )
    {
        if (!EntityManager.HasComponent<NetCompositionData>(composition))
        {
            return;
        }

        if (
            end
                ? !EntityManager.HasComponent<EndNodeGeometry>(edge)
                : !EntityManager.HasComponent<StartNodeGeometry>(edge)
        )
        {
            return;
        }

        var data = EntityManager.GetComponentData<NetCompositionData>(composition);
        if (!SupportsSnowComposition(composition))
        {
            return;
        }

        var geometry = end
            ? EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry
            : EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry;
        if (Math.Max(geometry.m_Left.middleLength, geometry.m_Right.middleLength) < 0.1f)
        {
            return;
        }

        sections.Add(
            new SurfaceSection(
                geometry.m_Left,
                geometry,
                data,
                CollectSurfaces(composition, edgeComposition),
                end ? 3 : 2,
                node,
                composition
            )
        );
    }

    private bool SupportsSnowComposition(Entity composition)
    {
        if (
            !EntityManager.HasComponent<NetCompositionData>(composition)
            || EntityManager.HasComponent<WaterwayComposition>(composition)
        )
        {
            return false;
        }

        var flags = EntityManager.HasComponent<TaxiwayComposition>(composition)
            ? EntityManager.GetComponentData<TaxiwayComposition>(composition).m_Flags
            : 0;
        return SurfaceGeometry.SupportsSnow(
            EntityManager.GetComponentData<NetCompositionData>(composition),
            flags
        );
    }

    private List<SurfaceStrip> CollectSurfaces(Entity composition, Entity edgeFallback = default)
    {
        if (!SupportsSnowComposition(composition))
        {
            return new List<SurfaceStrip>();
        }

        var cacheKey = (composition, edgeFallback);
        if (_surfaceCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var lanesForSurface = new List<SurfaceBand>();
        var physicalPieces = new List<SurfaceBand>();
        var data = EntityManager.GetComponentData<NetCompositionData>(composition);
        if (EntityManager.HasBuffer<NetCompositionCarriageway>(composition))
        {
            var ways = EntityManager.GetBuffer<NetCompositionCarriageway>(composition, true);
            for (int i = 0; i < ways.Length; i++)
            {
                var way = ways[i];
                if (
                    (
                        way.m_Flags
                        & (
                            LaneFlags.Road
                            | LaneFlags.Parking
                            | LaneFlags.BicyclesOnly
                            | LaneFlags.Track
                        )
                    ) == 0
                    || (
                        way.m_Flags
                        & (LaneFlags.Underground | LaneFlags.Virtual | LaneFlags.OnWater)
                    ) != 0
                    || way.m_Width < 1
                    || way.m_Width > 40
                )
                {
                    continue;
                }

                lanesForSurface.Add(
                    new SurfaceBand(
                        way.m_Position.x - way.m_Width / 2,
                        way.m_Position.x + way.m_Width / 2,
                        way.m_Position.y,
                        false
                    )
                );
            }
        }

        if (EntityManager.HasBuffer<NetCompositionLane>(composition))
        {
            var lanes = EntityManager.GetBuffer<NetCompositionLane>(composition, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                var lane = lanes[i];
                if (
                    (
                        lane.m_Flags
                        & (
                            LaneFlags.Pedestrian
                            | LaneFlags.Road
                            | LaneFlags.Parking
                            | LaneFlags.BicyclesOnly
                            | LaneFlags.Track
                        )
                    ) == 0
                    || (
                        lane.m_Flags
                        & (LaneFlags.Underground | LaneFlags.Virtual | LaneFlags.OnWater)
                    ) != 0
                    || !EntityManager.HasComponent<NetLaneData>(lane.m_Lane)
                )
                {
                    continue;
                }

                float width = EntityManager.GetComponentData<NetLaneData>(lane.m_Lane).m_Width;
                if (width < 0.4f || width > 12)
                {
                    continue;
                }

                lanesForSurface.Add(
                    new SurfaceBand(
                        lane.m_Position.x - width / 2,
                        lane.m_Position.x + width / 2,
                        lane.m_Position.y,
                        (lane.m_Flags & LaneFlags.Pedestrian) != 0
                    )
                );
            }
        }

        if (EntityManager.HasBuffer<NetCompositionPiece>(composition))
        {
            foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition, true))
            {
                if (
                    (
                        piece.m_SectionFlags
                        & (
                            NetSectionFlags.Underground
                            | NetSectionFlags.Overhead
                            | NetSectionFlags.HiddenSurface
                        )
                    ) != 0
                    || (piece.m_PieceFlags & NetPieceFlags.Surface) == 0
                    || !EntityManager.HasComponent<NetPieceData>(piece.m_Piece)
                )
                {
                    continue;
                }

                var pieceData = EntityManager.GetComponentData<NetPieceData>(piece.m_Piece);
                float width = pieceData.m_Width;
                float height = piece.m_Offset.y + math.cmax(pieceData.m_SurfaceHeights);
                if (width < 0.01f || !math.isfinite(height))
                {
                    continue;
                }

                physicalPieces.Add(
                    new SurfaceBand(
                        piece.m_Offset.x - width / 2,
                        piece.m_Offset.x + width / 2,
                        height,
                        false
                    )
                );
            }
        }

        if (lanesForSurface.Count == 0 && edgeFallback != Entity.Null)
        {
            var fallback = CollectSurfaces(edgeFallback);
            float width = EntityManager.GetComponentData<NetCompositionData>(edgeFallback).m_Width;
            float scale = data.m_Width / width;
            foreach (var strip in fallback)
            {
                lanesForSurface.Add(
                    new SurfaceBand(
                        (strip.Position.x - strip.Width / 2) * scale,
                        (strip.Position.x + strip.Width / 2) * scale,
                        strip.Position.y,
                        strip.Pedestrian
                    )
                );
            }
        }

        if (lanesForSurface.Count == 0 && physicalPieces.Count == 0)
        {
            return new List<SurfaceStrip>();
        }

        float fallbackHeight =
            math.isfinite(data.m_SurfaceHeight.max) && Mathf.Abs(data.m_SurfaceHeight.max) < 10
                ? data.m_SurfaceHeight.max
                : 0;
        var bands = SurfaceLayout.Build(
            data.m_Width,
            lanesForSurface,
            physicalPieces,
            fallbackHeight
        );
        var result = bands
            .Select(
                (b, i) =>
                    new SurfaceStrip(
                        i,
                        new float3((b.Left + b.Right) / 2, b.Height, 0),
                        b.Right - b.Left,
                        b.Pedestrian
                    )
            )
            .ToList();
        _surfaceCache.Set(cacheKey, result);
        return result;
    }

    private static int GeometrySignature(Segment segment, float3 position, float width)
    {
        unchecked
        {
            uint hash = StablePositionHash(segment.m_Left.a);
            hash = hash * 31 + StablePositionHash(segment.m_Left.b);
            hash = hash * 31 + StablePositionHash(segment.m_Left.c);
            hash = hash * 31 + StablePositionHash(segment.m_Left.d);
            hash = hash * 31 + StablePositionHash(segment.m_Right.a);
            hash = hash * 31 + StablePositionHash(segment.m_Right.b);
            hash = hash * 31 + StablePositionHash(segment.m_Right.c);
            hash = hash * 31 + StablePositionHash(segment.m_Right.d);
            hash = hash * 31 + StablePositionHash(position);
            return (int)(hash * 31 + math.asuint(width));
        }
    }

    private static uint StablePositionHash(float3 position) =>
        math.hash(math.round(position * 100f) * 0.01f);

    private readonly struct PatchKey : IEquatable<PatchKey>
    {
        private readonly Entity _edge;
        private readonly int _way;
        private readonly int _segment;
        private readonly int _chunk;
        private readonly int _signature;

        public PatchKey(Entity edge, int way, int segment, int chunk, int signature)
        {
            _edge = edge;
            _way = way;
            _segment = segment;
            _chunk = chunk;
            _signature = signature;
        }

        public bool Equals(PatchKey other) =>
            _edge == other._edge
            && _way == other._way
            && _segment == other._segment
            && _chunk == other._chunk
            && _signature == other._signature;

        public override bool Equals(object obj) => obj is PatchKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (((_edge.GetHashCode() * 31 + _way) * 31 + _segment) * 31 + _chunk) * 31
                    + _signature;
            }
        }
    }

    private readonly struct SurfaceStrip
    {
        public readonly int Id;
        public readonly float3 Position;
        public readonly float Width;
        public readonly bool Pedestrian;

        public SurfaceStrip(int id, float3 position, float width, bool pedestrian)
        {
            Id = id;
            Position = position;
            Width = width;
            Pedestrian = pedestrian;
        }
    }

    private readonly struct SurfaceSection
    {
        public readonly Segment Segment;
        public readonly EdgeNodeGeometry? Node;
        public readonly NetCompositionData Data;
        public readonly List<SurfaceStrip> Surfaces;
        public readonly int Id;
        public readonly Entity Owner;
        public readonly Entity Composition;
        public bool Pedestrian => Surfaces.Count > 0 && Surfaces.All(s => s.Pedestrian);
        public float Length =>
            Node.HasValue
                ? (
                    Node.Value.m_MiddleRadius > 0
                        ? Node.Value.m_Left.middleLength + Node.Value.m_Right.middleLength
                        : Math.Max(Node.Value.m_Left.middleLength, Node.Value.m_Right.middleLength)
                )
                : Segment.middleLength;

        public SurfaceSection(
            Segment segment,
            EdgeNodeGeometry? node,
            NetCompositionData data,
            List<SurfaceStrip> surfaces,
            int id,
            Entity owner,
            Entity composition = default
        )
        {
            Segment = segment;
            Node = node;
            Data = data;
            Surfaces = surfaces;
            Id = id;
            Owner = owner;
            Composition = composition;
        }
    }

    private readonly struct PatchRequest
    {
        public readonly PatchKey Key;
        public readonly Entity Edge;
        public readonly SurfaceSection Section;
        public readonly int Chunk;
        public readonly int Chunks;
        public readonly bool Detailed;

        public PatchRequest(
            PatchKey key,
            Entity edge,
            SurfaceSection section,
            int chunk,
            int chunks,
            bool detailed
        )
        {
            Key = key;
            Edge = edge;
            Section = section;
            Chunk = chunk;
            Chunks = chunks;
            Detailed = detailed;
        }
    }
}
