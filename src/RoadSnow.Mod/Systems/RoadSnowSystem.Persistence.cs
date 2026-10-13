using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using RoadSnow.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using BinaryReader = System.IO.BinaryReader;
using BinaryWriter = System.IO.BinaryWriter;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private const int SaveVersion = 5;
    private const int MaximumSaveBytes = 192 * 1024 * 1024;

    // World geometry keys survive regenerated lane entities. A rebuilt road gets a fresh state.
    private readonly Dictionary<Bezier4x3, LaneSnow> _laneHistory = new();
    private readonly Dictionary<Bezier4x3, (float Width, bool Rail)> _laneLayout = new();
    private readonly Dictionary<Vector3, float> _residualHistory = new();
    private readonly CachedCoverage _cachedCoverage = new();
    private Shader _cacheShader;

    public void Serialize<TWriter>(TWriter writer)
        where TWriter : IWriter
    {
        using var stream = new MemoryStream();
        using (var data = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            data.Write(_snowfall.TotalMm);
            _wearHistory.Write(data);
            data.Write(_laneHistory.Count);
            foreach (var lane in _laneHistory)
            {
                WritePoint(data, lane.Key.a);
                WritePoint(data, lane.Key.b);
                WritePoint(data, lane.Key.c);
                WritePoint(data, lane.Key.d);
                lane.Value.Write(data);
                var layout = _laneLayout.TryGetValue(lane.Key, out var saved) ? saved : (3f, false);
                data.Write(layout.Item1);
                data.Write(layout.Item2);
            }
            _plowedSnow.Write(data);
            data.Write(_residualHistory.Count);
            foreach (var residual in _residualHistory)
            {
                WritePoint(data, residual.Key);
                data.Write(residual.Value);
            }
            _cachedCoverage.Write(
                data,
                _activePatches.Where(p => p.Active).Select(p => p.CacheSurface())
            );
            data.Write(_snowActivity.Active);
        }
        writer.Write(SaveVersion);
        writer.Write(checked((int)stream.Length));
        using var bytes = new NativeArray<byte>(stream.ToArray(), Allocator.Temp);
        writer.Write(bytes);
    }

    public void Deserialize<TReader>(TReader reader)
        where TReader : IReader
    {
        // No EntityManager or rendering calls here: the loader owns the world transaction.
        SetDefaults(reader.context);
        reader.Read(out int version);
        reader.Read(out int length);
        if (length < 0 || length > MaximumSaveBytes)
            throw new InvalidDataException("Invalid snow save size.");
        if (version < 1 || version > SaveVersion)
        {
            reader.Skip(length);
            return;
        }
        using var bytes = new NativeArray<byte>(length, Allocator.Temp);
        reader.Read(bytes);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var data = new BinaryReader(stream);
            _snowfall.Restore(data.ReadDouble());
            _wearHistory.Read(data);
            int lanes = ReadCount(data, 500000);
            for (int i = 0; i < lanes; i++)
            {
                var curve = new Bezier4x3(
                    ReadPoint(data),
                    ReadPoint(data),
                    ReadPoint(data),
                    ReadPoint(data)
                );
                var snow = new LaneSnow();
                snow.Read(data);
                _laneHistory[curve] = snow;
                if (version >= 2)
                {
                    float width = data.ReadSingle();
                    bool rail = data.ReadBoolean();
                    if (!float.IsFinite(width) || width < .4f || width > 12)
                        throw new InvalidDataException("Invalid saved lane width.");
                    _laneLayout[curve] = (width, rail);
                }
            }
            _plowedSnow.Read(data);
            int residuals = ReadCount(data, 500000);
            for (int i = 0; i < residuals; i++)
                _residualHistory[ReadPoint(data)] = data.ReadSingle();
            if (version >= 2)
                _cachedCoverage.Read(data);
            if (version >= 3)
                _snowActivity.Restore(data.ReadBoolean());
            else
            {
                // Keep saved wear, but never replay the old distorted junction meshes.
                _cachedCoverage.Clear();
                _snowActivity.Restore(
                    _wearHistory.Tiles.Count > 0
                        || _laneHistory.Values.Any(lane => lane.Pressure > .001f)
                        || _residualHistory.Values.Any(depth => depth > .125f)
                        || _plowedSnow.Deposits.Count > 0
                );
            }
            if (version < 5) _cachedCoverage.Clear();
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Unexpected snow save payload.");
        }
        catch (Exception error)
        {
            SetDefaults(reader.context);
            Mod.log.Warn("Snow state could not be restored; rebuilding visuals. " + error.Message);
        }
    }

    public void SetDefaults(Context context)
    {
        _wearHistory.Clear();
        _laneHistory.Clear();
        _laneLayout.Clear();
        _residualHistory.Clear();
        _snowfall.Reset();
        _plowedSnow.Deposits.Clear();
        _cachedCoverage.Clear();
        _snowActivity.Restore(false);
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid snow record count.");
        return count;
    }

    private static void WritePoint(BinaryWriter writer, float3 point)
    {
        writer.Write(point.x);
        writer.Write(point.y);
        writer.Write(point.z);
    }

    private static float3 ReadPoint(BinaryReader reader)
    {
        var point = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (!math.all(math.isfinite(point)))
            throw new InvalidDataException("Invalid snow position.");
        return point;
    }
}
