using System;
using System.Collections.Generic;
using RoadSnow.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class LaneRaster : IDisposable
{
    public const int Size = 1024;
    public const int MaximumQuads = 65536;
    private readonly ComputeShader _shader;
    private readonly ExactSequence<Quad> _quads = new();
    private bool _hasRaster;
    private ComputeBuffer _buffer;
    private readonly int _clear;
    private readonly int _raster;
    private Vector4 _area;
    private Vector3 _probePoint;
    private float _probePressure;
    private bool _probePending;
    private bool _disposed;
    private readonly string _binding;
    public RenderTexture Texture { get; }
    public int Count => _quads.Count;
    public int Rasterizations { get; private set; }
    public int ReusedRasterizations { get; private set; }

    public LaneRaster(ComputeShader shader, bool distant = false, bool detail = false)
    {
        _binding =
            detail ? "_RoadSnowDetailLane"
            : distant ? "_RoadSnowDistantLane"
            : "_RoadSnowLane";
        if (shader == null || !SystemInfo.supportsComputeShaders)
        {
            throw new InvalidOperationException("RoadSnow lane compute shader unavailable.");
        }

        _shader = Object.Instantiate(shader);
        _clear = _shader.FindKernel("Clear");
        _raster = _shader.FindKernel("Raster");
        Texture = new RenderTexture(
            new RenderTextureDescriptor(Size, Size)
            {
                graphicsFormat = GraphicsFormat.R32_UInt,
                depthBufferBits = 0,
                enableRandomWrite = true,
                msaaSamples = 1,
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = 4,
            }
        )
        {
            name = "RoadSnow packed lane pressure and height",
            filterMode = FilterMode.Point,
        };
        Texture.Create();
        _shader.SetTexture(_clear, "_Output", Texture);
        _shader.SetTexture(_raster, "_Output", Texture);
        Shader.SetGlobalTexture(_binding + "Packed", Texture);
        Shader.SetGlobalVector(_binding + "Area", Vector4.zero);
    }

    public void Begin()
    {
        _quads.Begin();
    }

    public void Retain(int count) => _quads.Retain(Math.Min(count, MaximumQuads - _quads.Count));

    public void Add(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        float start,
        float end,
        float width,
        bool rail = false
    )
    {
        if (_quads.Count >= MaximumQuads)
        {
            return;
        }

        _quads.Add(
            new Quad
            {
                A = new Vector4(a.x, a.y, a.z, start),
                B = new Vector4(b.x, b.y, b.z, start),
                C = new Vector4(c.x, c.y, c.z, end),
                D = new Vector4(d.x, d.y, d.z, end),
                Shape = new Vector4(width, rail ? 1 : 0, 0, 0),
            }
        );
    }

    public void Draw(Vector4 area)
    {
        Render(area, _quads.End());
    }

    // Caller has compared all lane inputs exactly, before expanding the ribbons.
    // Render still restores a lost GPU texture from the retained CPU quads.
    public void Reuse(Vector4 area) => Render(area, false);

    private void Render(Vector4 area, bool changed)
    {
        if (!Texture.IsCreated())
        {
            Texture.Create();
            _shader.SetTexture(_clear, "_Output", Texture);
            _shader.SetTexture(_raster, "_Output", Texture);
            _hasRaster = false;
        }
        // LaneRaster.compute reads only the ordered quads and the area. Exact
        // equality preserves tiny pressure/height changes and stacked road order.
        if (_hasRaster && !changed && _area.Equals(area))
        {
            ReusedRasterizations++;
            Bind(area);
            return;
        }
        _hasRaster = false;
        _area = area;
        _shader.SetVector("_Area", area);
        _shader.Dispatch(_clear, Size / 8, Size / 8, 4);
        if (_quads.Count > 0)
        {
            if (_buffer == null || _buffer.count < _quads.Count)
            {
                _buffer?.Dispose();
                _buffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Math.Max(64, _quads.Count)), 80);
            }

            _buffer.SetData(_quads.Items);
            _shader.SetBuffer(_raster, "_Quads", _buffer);
            _shader.Dispatch(_raster, _quads.Count, 1, 1);
        }

        _hasRaster = true;
        Rasterizations++;
        Bind(area);
    }

    private void Bind(Vector4 area)
    {
        Shader.SetGlobalTexture(_binding + "Packed", Texture);
        Shader.SetGlobalVector(_binding + "Area", area);
    }

    public void ProbeGpu(Action<string> report)
    {
        if (_probePending || !SystemInfo.supportsAsyncGPUReadback)
        {
            return;
        }

        // Diagnostics run once per reporting window, not per generated vertex.
        _probePressure = 0;
        foreach (var quad in _quads.Items)
        {
            float pressureAtQuad = (quad.A.w + quad.C.w) * .5f;
            if (pressureAtQuad > _probePressure)
            {
                _probePressure = pressureAtQuad;
                _probePoint = (quad.A + quad.B + quad.C + quad.D) * .25f;
            }
        }
        if (_probePressure < .01f)
            return;

        Vector3 point = _probePoint;
        float pressure = _probePressure;
        int x = Mathf.FloorToInt((point.x - _area.x) * _area.z * Size);
        int y = Mathf.FloorToInt((point.z - _area.y) * _area.z * Size);
        if (x < 0 || y < 0 || x >= Size || y >= Size)
        {
            return;
        }

        _probePending = true;
        AsyncGPUReadback.Request(
            Texture,
            0,
            x,
            1,
            y,
            1,
            0,
            1,
            request =>
            {
                if (_disposed)
                {
                    return;
                }

                _probePending = false;
                if (request.hasError)
                {
                    report("lane readback failed");
                    return;
                }

                uint packed = request.GetData<uint>()[0];
                report(
                    $"lane compute GPU={(packed & 255) / 255f:F3}, CPU pressure={pressure:F3}, GPU height={(packed >> 8) * .05f - 1024:F2}, world={point}, quads={Count}"
                );
            }
        );
    }

    public void Dispose()
    {
        _disposed = true;
        Shader.SetGlobalVector(_binding + "Area", Vector4.zero);
        _buffer?.Dispose();
        Texture.Release();
        Object.Destroy(Texture);
        Object.Destroy(_shader);
    }

    private struct Quad : IEquatable<Quad>
    {
        public Vector4 A;
        public Vector4 B;
        public Vector4 C;
        public Vector4 D;
        public Vector4 Shape;

        public bool Equals(Quad other) =>
            A.Equals(other.A)
            && B.Equals(other.B)
            && C.Equals(other.C)
            && D.Equals(other.D)
            && Shape.Equals(other.Shape);
    }
}
