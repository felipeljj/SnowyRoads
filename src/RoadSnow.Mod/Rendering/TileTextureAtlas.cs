using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

// Keep the destination on the GPU. Batch changed pages into a small staging
// texture, upload once, then copy exact regions without a GPU-to-CPU readback.
internal sealed class TileTextureAtlas : IDisposable
{
    public readonly Texture2D Texture;
    private readonly int _size,
        _tileSize,
        _slotsPerSide;
    private readonly bool _partial;
    private readonly Texture2D[] _staging = new Texture2D[3];
    private readonly List<Upload> _pending = new();
    public long UploadedBytes { get; private set; }
    public int UploadBatches { get; private set; }
    public int RegionCopies { get; private set; }
    public long CopyTicks { get; private set; }
    public long TransferTicks { get; private set; }
    public bool PartialUploads => _partial;

    public TileTextureAtlas(int size, int tileSize)
    {
        _size = size;
        _tileSize = tileSize;
        _slotsPerSide = size / tileSize;
        _partial = (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;
        Texture = Create(size, size, "RoadSnow individual impressions");
        Texture.filterMode = FilterMode.Bilinear;
        if (_partial)
            Texture.Apply(false, true);
    }

    private static Texture2D Create(int width, int height, string name) =>
        new(width, height, TextureFormat.RGBA32, false, true)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

    public void Queue(byte[] pixels, int slot)
    {
        if (
            pixels.Length != _tileSize * _tileSize * 4
            || (uint)slot >= _slotsPerSide * _slotsPerSide
        )
            throw new ArgumentException("Invalid atlas page.");
        _pending.Add(new Upload(pixels, slot));
    }

    public void Flush()
    {
        if (_pending.Count == 0)
            return;
        long started = Stopwatch.GetTimestamp();
        if (!_partial)
        {
            foreach (var upload in _pending)
                Copy(
                    upload.Pixels,
                    Texture,
                    upload.Slot % _slotsPerSide * _tileSize,
                    upload.Slot / _slotsPerSide * _tileSize
                );
            CopyTicks += Stopwatch.GetTimestamp() - started;
            started = Stopwatch.GetTimestamp();
            Texture.Apply(false, false);
            UploadedBytes += (long)_size * _size * 4;
        }
        else
        {
            int bucket =
                _pending.Count <= 4 ? 0
                : _pending.Count <= 32 ? 1
                : 2;
            int capacity =
                bucket == 0 ? 4
                : bucket == 1 ? 32
                : _slotsPerSide * _slotsPerSide;
            int columns = Math.Min(16, capacity);
            var staging = _staging[bucket];
            if (staging == null)
                _staging[bucket] = staging = Create(
                    columns * _tileSize,
                    ((capacity + columns - 1) / columns) * _tileSize,
                    "RoadSnow changed pages"
                );
            for (int i = 0; i < _pending.Count; i++)
                Copy(_pending[i].Pixels, staging, i % columns * _tileSize, i / columns * _tileSize);
            CopyTicks += Stopwatch.GetTimestamp() - started;
            started = Stopwatch.GetTimestamp();
            staging.Apply(false, false);
            UploadedBytes += (long)staging.width * staging.height * 4;
            for (int i = 0; i < _pending.Count; i++)
            {
                int slot = _pending[i].Slot;
                Graphics.CopyTexture(
                    staging,
                    0,
                    0,
                    i % columns * _tileSize,
                    i / columns * _tileSize,
                    _tileSize,
                    _tileSize,
                    Texture,
                    0,
                    0,
                    slot % _slotsPerSide * _tileSize,
                    slot / _slotsPerSide * _tileSize
                );
                RegionCopies++;
            }
        }
        TransferTicks += Stopwatch.GetTimestamp() - started;
        UploadBatches++;
        _pending.Clear();
    }

    private unsafe void Copy(byte[] pixels, Texture2D texture, int x, int y)
    {
        int rowBytes = _tileSize * 4;
        var data = texture.GetRawTextureData<byte>();
        if (
            x < 0
            || y < 0
            || x + _tileSize > texture.width
            || y + _tileSize > texture.height
            || pixels.Length != rowBytes * _tileSize
            || data.Length != texture.width * texture.height * 4
        )
            throw new ArgumentException("Atlas copy outside texture.");
        fixed (byte* source = pixels)
        {
            byte* target = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(data);
            UnsafeUtility.MemCpyStride(
                target + (y * texture.width + x) * 4,
                texture.width * 4,
                source,
                rowBytes,
                rowBytes,
                _tileSize
            );
        }
    }

    public void Dispose()
    {
        _pending.Clear();
        foreach (var texture in _staging)
            if (texture != null)
                Release(texture);
        Release(Texture);
    }

    private static void Release(Texture2D texture)
    {
        if (Application.isPlaying)
            Object.Destroy(texture);
        else
            Object.DestroyImmediate(texture);
    }

    private readonly struct Upload
    {
        public readonly byte[] Pixels;
        public readonly int Slot;

        public Upload(byte[] pixels, int slot)
        {
            Pixels = pixels;
            Slot = slot;
        }
    }
}
