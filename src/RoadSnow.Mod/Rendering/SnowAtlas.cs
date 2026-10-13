using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class SnowAtlas : IDisposable
{
    private readonly Material _material;
    private readonly List<Page> _pages = new();
    public int BatchCount => _pages.Count;
    public int LastTextureUploads { get; private set; }

    public SnowAtlas(Material material) => _material = material;

    public void SetVisible(bool visible)
    {
        foreach (var page in _pages)
            page.SetVisible(visible);
    }

    public Slot Allocate(bool detailed, Vector3[] vertices, Vector2[] uv, int[] indices)
    {
        foreach (var page in _pages)
        {
            if (page.Detailed == detailed && page.HasSpace)
            {
                return page.Add(vertices, uv, indices);
            }
        }

        var created = new Page(_material, detailed);
        _pages.Add(created);
        return created.Add(vertices, uv, indices);
    }

    public void Flush(bool uploadTextures = true)
    {
        LastTextureUploads = 0;
        foreach (var page in _pages)
        {
            if (page.Flush(uploadTextures))
            {
                LastTextureUploads++;
            }
        }
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        _pages.Clear();
    }

    internal sealed class Slot : IDisposable
    {
        private readonly Page _page;
        public readonly int Index;
        public readonly Vector3[] Vertices;
        public readonly Vector2[] UV;
        public readonly int[] Indices;
        private Color32[] _pixels;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int Width => _page.TileWidth;
        public int Height => _page.TileHeight;
        public Vector2 Offset =>
            new((Index % _page.TilesAcross) * Width, (Index / _page.TilesAcross) * Height);
        public Texture2D Texture => _page.Texture;

        public Vector2 MapUV(Vector2 uv) =>
            new(
                (Offset.x + 1.5f + uv.x * (Columns - 1)) / _page.Size,
                (Offset.y + 1.5f + uv.y * (Rows - 1)) / _page.Size
            );

        public Slot(Page page, int index, Vector3[] vertices, Vector2[] uv, int[] indices)
        {
            _page = page;
            Index = index;
            Vertices = vertices;
            UV = uv;
            Indices = indices;
        }

        public void Write(Color32[] pixels, int columns, int rows)
        {
            if (columns > Width - 2 || rows > Height - 2)
            {
                throw new ArgumentException("Atlas tile overflow.");
            }

            if (_pixels == null)
            {
                Columns = columns;
                Rows = rows;
                _pixels = new Color32[(columns + 2) * (rows + 2)];
            }

            if (Columns != columns || Rows != rows)
            {
                throw new ArgumentException("Atlas slot resolution changed.");
            }

            int usedWidth = columns + 2;
            int usedHeight = rows + 2;
            for (int y = 0; y < usedHeight; y++)
            {
                int sourceY = Mathf.Clamp(y - 1, 0, rows - 1);
                for (int x = 0; x < usedWidth; x++)
                {
                    int sourceX = Mathf.Clamp(x - 1, 0, columns - 1);
                    _pixels[y * usedWidth + x] = pixels[sourceY * columns + sourceX];
                }
            }

            _page.Write(this, _pixels, usedWidth, usedHeight);
        }

        public void Dispose() => _page.Remove(this);
    }

    internal sealed class Page : IDisposable
    {
        public int Size => Detailed ? 1024 : 256;
        public Texture2D Texture => _texture;

        private readonly Dictionary<int, Slot> _slots = new();
        private readonly List<int> _free = new();
        private readonly Texture2D _texture;
        private readonly Mesh _mesh;
        private readonly GameObject _object;
        private readonly Color32[] _empty;
        private bool _geometryDirty;
        private bool _textureDirty;
        public readonly bool Detailed;
        public int TileWidth => Detailed ? 256 : 16;
        public int TileHeight => Detailed ? 256 : 16;
        public int TilesAcross => Size / TileWidth;
        public bool HasSpace => _free.Count > 0;

        public void SetVisible(bool visible) => _object.SetActive(visible);

        public Page(Material material, bool detailed)
        {
            Detailed = detailed;
            int capacity = TilesAcross * (Size / TileHeight);
            for (int i = capacity - 1; i >= 0; i--)
            {
                _free.Add(i);
            }

            _empty = new Color32[TileWidth * TileHeight];
            _texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true)
            {
                name = "RoadSnow shared atlas",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            _texture.SetPixels32(new Color32[Size * Size]);
            _texture.Apply(false, false);
            _mesh = new Mesh
            {
                name = "RoadSnow combined network mesh",
                hideFlags = HideFlags.DontSave,
                indexFormat = IndexFormat.UInt32,
            };
            _object = new GameObject("RoadSnow atlas batch") { hideFlags = HideFlags.DontSave };
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            var properties = new MaterialPropertyBlock();
            properties.SetTexture("_BaseColorMap", _texture);
            renderer.SetPropertyBlock(properties);
        }

        public Slot Add(Vector3[] vertices, Vector2[] uv, int[] indices)
        {
            int index = _free[_free.Count - 1];
            _free.RemoveAt(_free.Count - 1);
            var slot = new Slot(this, index, vertices, uv, indices);
            _slots.Add(slot.Index, slot);
            _geometryDirty = true;
            return slot;
        }

        public void Write(Slot slot, Color32[] pixels, int width, int height)
        {
            Vector2 offset = slot.Offset;
            _texture.SetPixels32((int)offset.x, (int)offset.y, width, height, pixels);
            _textureDirty = true;
        }

        public void Remove(Slot slot)
        {
            if (!_slots.Remove(slot.Index))
            {
                return;
            }

            Write(slot, _empty, TileWidth, TileHeight);
            _free.Add(slot.Index);
            _geometryDirty = true;
        }

        public bool Flush(bool uploadTextures)
        {
            if (_geometryDirty)
            {
                uploadTextures = true;
                int vertexCount = 0;
                int indexCount = 0;
                foreach (var slot in _slots.Values)
                {
                    vertexCount += slot.Vertices.Length;
                    indexCount += slot.Indices.Length;
                }

                var vertices = new Vector3[vertexCount];
                var uv = new Vector2[vertexCount];
                var indices = new int[indexCount];
                int vertex = 0;
                int index = 0;
                foreach (var slot in _slots.Values)
                {
                    Array.Copy(slot.Vertices, 0, vertices, vertex, slot.Vertices.Length);
                    Vector2 offset = slot.Offset;
                    for (int i = 0; i < slot.UV.Length; i++)
                    {
                        uv[vertex + i] = new Vector2(
                            (offset.x + 1.5f + slot.UV[i].x * (slot.Columns - 1)) / Size,
                            (offset.y + 1.5f + slot.UV[i].y * (slot.Rows - 1)) / Size
                        );
                    }

                    foreach (int local in slot.Indices)
                    {
                        indices[index++] = vertex + local;
                    }

                    vertex += slot.Vertices.Length;
                }

                _mesh.Clear();
                _mesh.vertices = vertices;
                _mesh.uv = uv;
                _mesh.triangles = indices;
                _mesh.RecalculateNormals();
                _mesh.RecalculateBounds();
                _geometryDirty = false;
            }

            if (!_textureDirty || !uploadTextures)
            {
                return false;
            }

            _texture.Apply(false, false);
            _textureDirty = false;
            return true;
        }

        public void Dispose()
        {
            _object.SetActive(false);
            Object.Destroy(_object);
            Object.Destroy(_mesh);
            Object.Destroy(_texture);
        }
    }
}
