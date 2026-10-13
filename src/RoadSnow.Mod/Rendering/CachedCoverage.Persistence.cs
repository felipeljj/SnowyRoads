using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace RoadSnow.Mod;

internal sealed partial class CachedCoverage
{
    private const int MaximumSurfaceVertices = 16384;
    private const int MaximumSurfaceIndices = 98304;
    private const int MaximumCacheBytes = 160 * 1024 * 1024;
    public int LastWritten { get; private set; }
    public int LastOmitted { get; private set; }

    public void Write(
        BinaryWriter writer,
        IEnumerable<Surface> current,
        int maximumBytes = MaximumCacheBytes,
        IEnumerable<Vector3> required = null,
        bool plowState = false
    )
    {
        maximumBytes = Math.Max(4, Math.Min(MaximumCacheBytes, maximumBytes));
        var surfaces = Merge(current);
        using var raw = new MemoryStream();
        using var data = new BinaryWriter(raw, System.Text.Encoding.UTF8, true);
        data.Write(0);
        LastWritten = LastOmitted = 0;
        if (required != null)
            foreach (var center in required)
                if (!Find(surfaces, center, out _))
                    LastOmitted++;
        foreach (var surface in surfaces.Values)
        {
            if (
                surface.Vertices.Length > MaximumSurfaceVertices
                || surface.Indices.Length > MaximumSurfaceIndices
            )
            {
                LastOmitted++;
                continue;
            }
            // Junction triangulation repeats vertices. Deduplicate exact position
            // AND UV pairs, preserving every triangle and its coordinates.
            var map = new Dictionary<(Vector3, Vector2, byte), ushort>();
            var points = new List<(Vector3, Vector2, byte)>();
            var indices = new ushort[surface.Indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                int original = surface.Indices[i];
                var vertex = (
                    surface.Vertices[original],
                    surface.UV[original],
                    plowState && surface.RoadVertexMask != null
                        ? surface.RoadVertexMask[original]
                        : (byte)0
                );
                if (!map.TryGetValue(vertex, out ushort index))
                {
                    index = checked((ushort)points.Count);
                    map.Add(vertex, index);
                    points.Add(vertex);
                }
                indices[i] = index;
            }
            long bytes =
                33L
                + (plowState ? 8 : 0)
                + points.Count * (plowState ? 21L : 20L)
                + indices.Length * 2L
                + surface.Pixels.Length * 4L;
            if (LastWritten >= 100000 || raw.Length + bytes > maximumBytes)
            {
                LastOmitted++;
                continue;
            }
            WritePoint(data, surface.Center);
            data.Write(surface.Detailed);
            data.Write(surface.Columns);
            data.Write(surface.Rows);
            data.Write(points.Count);
            foreach (var point in points)
            {
                WritePoint(data, point.Item1);
                data.Write(point.Item2.x);
                data.Write(point.Item2.y);
                if (plowState)
                    data.Write(point.Item3);
            }
            data.Write(indices.Length);
            foreach (ushort index in indices)
                data.Write(index);
            foreach (var pixel in surface.Pixels)
            {
                data.Write(pixel.r);
                data.Write(pixel.g);
                data.Write(pixel.b);
                data.Write(pixel.a);
            }
            if (plowState)
                data.Write(surface.PlowedAt);
            LastWritten++;
        }
        raw.Position = 0;
        data.Write(LastWritten);
        raw.Position = 0;
        using var packed = new MemoryStream();
        using (
            var zip = new DeflateStream(
                packed,
                System.IO.Compression.CompressionLevel.Fastest,
                true
            )
        )
            raw.CopyTo(zip);
        writer.Write(checked((int)raw.Length));
        writer.Write(checked((int)packed.Length));
        writer.Write(packed.ToArray());
    }

    public void Read(BinaryReader reader, bool exact = true, bool plowState = false)
    {
        Clear();
        if (!exact)
        {
            ReadLegacy(reader);
            return;
        }
        int length = CountValue(reader, MaximumCacheBytes);
        int packedLength = CountValue(reader, MaximumCacheBytes + 1024 * 1024);
        byte[] packed = reader.ReadBytes(packedLength);
        if (packed.Length != packedLength)
            throw new EndOfStreamException();
        using var input = new MemoryStream(packed, false);
        using var zip = new DeflateStream(input, CompressionMode.Decompress);
        using var bounded = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = zip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bounded.Length + read > length)
                throw new InvalidDataException("Snow coverage cache too large.");
            bounded.Write(buffer, 0, read);
        }
        if (bounded.Length != length)
            throw new InvalidDataException("Truncated snow coverage cache.");
        bounded.Position = 0;
        using var data = new BinaryReader(bounded);
        int count = CountValue(data, 100000);
        for (int n = 0; n < count; n++)
        {
            var surface = new Surface { Center = ReadPoint(data), Detailed = data.ReadBoolean() };
            int maximum = surface.Detailed ? 254 : 14;
            surface.Columns = CountValue(data, maximum);
            surface.Rows = CountValue(data, maximum);
            if (surface.Columns < 1 || surface.Rows < 1)
                throw new InvalidDataException("Invalid snow grid.");
            int vertices = CountValue(data, MaximumSurfaceVertices);
            surface.Vertices = new Vector3[vertices];
            surface.UV = new Vector2[vertices];
            if (plowState)
                surface.RoadVertexMask = new byte[vertices];
            for (int i = 0; i < vertices; i++)
            {
                surface.Vertices[i] = ReadPoint(data);
                var uv = new Vector2(data.ReadSingle(), data.ReadSingle());
                if (!float.IsFinite(uv.sqrMagnitude))
                    throw new InvalidDataException("Invalid snow UV.");
                surface.UV[i] = uv;
                if (plowState)
                    surface.RoadVertexMask[i] = data.ReadByte();
            }
            int indices = CountValue(data, MaximumSurfaceIndices);
            if (indices % 3 != 0)
                throw new InvalidDataException("Invalid snow triangles.");
            surface.Indices = new int[indices];
            for (int i = 0; i < indices; i++)
            {
                int index = data.ReadUInt16();
                if (index >= vertices)
                    throw new InvalidDataException("Invalid snow vertex.");
                surface.Indices[i] = index;
            }
            surface.Pixels = new Color32[surface.Columns * surface.Rows];
            for (int i = 0; i < surface.Pixels.Length; i++)
                surface.Pixels[i] = new Color32(
                    data.ReadByte(),
                    data.ReadByte(),
                    data.ReadByte(),
                    data.ReadByte()
                );
            if (plowState)
            {
                surface.PlowedAt = data.ReadDouble();
                if (
                    double.IsNaN(surface.PlowedAt)
                    || double.IsInfinity(surface.PlowedAt)
                    || surface.PlowedAt < -1
                )
                    throw new InvalidDataException("Invalid section plow state.");
            }
            _surfaces[Key(surface.Center)] = surface;
        }
        if (bounded.Position != bounded.Length)
            throw new InvalidDataException("Unexpected snow coverage bytes.");
        _remaining = _surfaces.Count;
        _exact = true;
    }

    private void ReadLegacy(BinaryReader reader)
    {
        int count = CountValue(reader, 100000),
            total = 0;
        for (int n = 0; n < count; n++)
        {
            var surface = new Surface
            {
                Center = ReadPoint(reader),
                Columns = 1,
                Rows = 1,
            };
            int vertices = CountValue(reader, MaximumSurfaceVertices);
            total += vertices;
            if (total > 2000000)
                throw new InvalidDataException("Legacy snow coverage cache too large.");
            surface.Vertices = new Vector3[vertices];
            surface.UV = new Vector2[vertices];
            for (int i = 0; i < vertices; i++)
                surface.Vertices[i] = ReadPoint(reader);
            int indices = CountValue(reader, MaximumSurfaceIndices);
            if (indices % 3 != 0)
                throw new InvalidDataException("Invalid snow triangles.");
            surface.Indices = new int[indices];
            for (int i = 0; i < indices; i++)
            {
                int index = reader.ReadInt32();
                if (index < 0 || index >= vertices)
                    throw new InvalidDataException("Invalid snow vertex.");
                surface.Indices[i] = index;
            }
            surface.Pixels = new[] { new Color32(0, 0, 0, 255) };
            _surfaces[Key(surface.Center)] = surface;
        }
        _remaining = _surfaces.Count;
    }

    private static int CountValue(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid snow coverage count.");
        return count;
    }

    private static void WritePoint(BinaryWriter writer, Vector3 p)
    {
        writer.Write(p.x);
        writer.Write(p.y);
        writer.Write(p.z);
    }

    private static Vector3 ReadPoint(BinaryReader reader)
    {
        var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (!float.IsFinite(p.sqrMagnitude))
            throw new InvalidDataException("Invalid snow coverage position.");
        return p;
    }
}
