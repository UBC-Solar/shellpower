using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace SSCP.ShellPower
{
    public sealed class ShadowMeshSprite : Sprite
    {
        // Public knobs
        public Vector3 Position { get; set; } = Vector3.Zero;
        public bool ShowShadowVolume { get; set; } = true;
        public bool ShowShadowOutline { get; set; } = true;

        // Inputs
        public Mesh Mesh { get; }
        public Shadow Shadow { get; }

        public Quad3 BoundingBox => Mesh.BoundingBox;
        
// fields (add these to ShadowMeshSprite)
        private int _vaoMesh, _vboMeshInterleaved, _eboMesh;
        private int _vertexCount, _meshIndexCount;
        // private DrawElementsType _indexType;
        private bool _glReady;

// expose for debugging
        public int Vao            => _vaoMesh;
        public int VboInterleaved => _vboMeshInterleaved;
        public int Ebo            => _eboMesh;
        public int VertexCount    => _vertexCount;
        public int IndexCount     => _meshIndexCount;
        public DrawElementsType IndexType => _indexType;


        // GL objects (created lazily)
        // int _vaoMesh, _vboMeshInterleaved, _eboMesh, _meshIndexCount;
        int _vaoOutline, _vboOutline, _outlineVertCount;
        int _vaoVolume, _vboVolume, _volumeVertCount;

        // bool _glReady; // set true after first EnsureGlResourcesCreated()

        public bool HasOutline => _outlineVertCount > 0;
        public bool HasVolume  => _volumeVertCount > 0;

        public ShadowMeshSprite(Shadow shadow)
        {
            Shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
            Mesh   = shadow.Mesh ?? throw new ArgumentNullException(nameof(shadow.Mesh));
            // IMPORTANT: no GL calls here (no current context yet)
        }

        /// <summary>Create VAOs/VBOs once a GL context is current.</summary>
        public void EnsureGlResourcesCreated()
        {
            if (_glReady) return;

            BuildMeshBuffers();    // pos+normal interleaved + indices
            BuildOutlineBuffer();  // line list (pos only)
            // Volume VBO/VAO will be created on first UpdateShadowVolumeVertices()

            _glReady = true;
        }

        public override void PushTransform() { /* no-op (core pipeline; matrices set by control) */ }
        public override void PopTransform()  { /* no-op */ }

        // public override void Render()
        // {
        //     RenderMesh();
        //     RenderShadowVolume();
        //     RenderShadowOutline();
        // }

        public override void RenderMesh()
        {
            if (!_glReady || _vaoMesh == 0 || _meshIndexCount <= 0)
            {
                Debug.WriteLine($"[RenderMesh] skip: glReady={_glReady} vao={_vaoMesh} indexCount={_meshIndexCount}");
                return;
            }

            GL.BindVertexArray(_vaoMesh);

            GL.GetInteger(GetPName.ElementArrayBufferBinding, out int ibo);
            GL.GetInteger(GetPName.ArrayBufferBinding, out int vbo);
            // Debug.WriteLine($"[RenderMesh] bound VAO={_vaoMesh} VBO={vbo} IBO={ibo} idxType={_indexType}");
            //
            // Debug.WriteLine($"[RenderMesh] count={_meshIndexCount} type={_indexType} vao={_vaoMesh}");

            GL.DrawElements(PrimitiveType.Triangles, _meshIndexCount, _indexType, IntPtr.Zero);

            var err = GL.GetError();
            if (err != ErrorCode.NoError)
                Debug.WriteLine($"GL error in RenderMesh: {err}");

            GL.BindVertexArray(0);
        }

        public void RebuildOutlineBuffer()
        {
            // dispose old buffers (safe if zero)
            if (_vboOutline != 0) { GL.DeleteBuffer(_vboOutline); _vboOutline = 0; }
            if (_vaoOutline != 0) { GL.DeleteVertexArray(_vaoOutline); _vaoOutline = 0; }
            _outlineVertCount = 0;

            // rebuild from current SilhouetteEdges
            var lines = new List<float>(Shadow.SilhouetteEdges.Count * 2 * 3);
            foreach (var e in Shadow.SilhouetteEdges)
            {
                var a = Mesh.points[e.First];
                var b = Mesh.points[e.Second];
                lines.Add(a.X); lines.Add(a.Y); lines.Add(a.Z);
                lines.Add(b.X); lines.Add(b.Y); lines.Add(b.Z);
            }
            _outlineVertCount = lines.Count / 3;
            if (_outlineVertCount == 0) return;

            _vaoOutline = GL.GenVertexArray();
            _vboOutline = GL.GenBuffer();

            GL.BindVertexArray(_vaoOutline);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboOutline);
            GL.BufferData(BufferTarget.ArrayBuffer, lines.Count * sizeof(float), lines.ToArray(), BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

            GL.BindVertexArray(0);
        }

        public void RenderShadowOutline()
        {
            if (!_glReady || _vaoOutline == 0 || _outlineVertCount == 0) return;
            GL.BindVertexArray(_vaoOutline);
            GL.DrawArrays(PrimitiveType.Lines, 0, _outlineVertCount);
            GL.BindVertexArray(0);
        }

        // public void RenderShadowVolume()
        // {
        //     if (!_glReady || _vaoVolume == 0 || _volumeVertCount == 0) return;
        //     GL.BindVertexArray(_vaoVolume);
        //     GL.DrawArrays(_volumePrim, 0, _volumeVertCount);
        //     GL.BindVertexArray(0);
        // }
        
// ShadowMeshSprite

        private PrimitiveType _volumePrim = PrimitiveType.Triangles;

        public void UpdateShadowVolumeVertices()
        {
            if (!_glReady) return;

            var L = Shadow.Light;                 // directional light in object/world space
            if (L.Y <= 0f) { _volumeVertCount = 0; return; }

            float Ly = MathF.Max(MathF.Abs(L.Y), 1e-8f);

            // OBJECT-SPACE ground plane
            float minY = Mesh.points.Min(p => p.Y);

            // build independent quads (two triangles per edge)
            var verts = new List<float>(Shadow.SilhouetteEdges.Count * 6 * 3);

            foreach (var e in Shadow.SilhouetteEdges)
            {
                var p0 = Mesh.points[e.First];   // object space
                var p1 = Mesh.points[e.Second];

                float k0 = (p0.Y - minY) / Ly;
                float k1 = (p1.Y - minY) / Ly;

                var p0b = new System.Numerics.Vector3(p0.X - L.X * k0, p0.Y - L.Y * k0, p0.Z - L.Z * k0);
                var p1b = new System.Numerics.Vector3(p1.X - L.X * k1, p1.Y - L.Y * k1, p1.Z - L.Z * k1);

                void add(System.Numerics.Vector3 v)
                {
                    verts.Add(v.X); verts.Add(v.Y); verts.Add(v.Z);
                }

                // two triangles per silhouette edge
                add(p0);  add(p1);  add(p0b);
                add(p1);  add(p1b); add(p0b);
            }

            _volumeVertCount = verts.Count / 3;
            if (_volumeVertCount == 0) return;

            if (_vaoVolume == 0)
            {
                _vaoVolume = GL.GenVertexArray();
                _vboVolume = GL.GenBuffer();

                GL.BindVertexArray(_vaoVolume);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vboVolume);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.DynamicDraw);

                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

                GL.BindVertexArray(0);
            }
            else
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vboVolume);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.DynamicDraw);
            }

            _volumePrim = PrimitiveType.Triangles;
        }

        public void RenderShadowVolume()
        {
            if (!_glReady || _vaoVolume == 0 || _volumeVertCount == 0) return;
            GL.BindVertexArray(_vaoVolume);
            GL.DrawArrays(_volumePrim, 0, _volumeVertCount);
            GL.BindVertexArray(0);
        }
        private DrawElementsType _indexType; // store what type we uploaded

private void BuildMeshBuffers()
{
    int vcount = Mesh.points.Length;
    _vertexCount = vcount;

    if (Mesh.points.Length != Mesh.normals.Length)
        throw new InvalidOperationException("Points and normals count mismatch.");

    // Interleaved pos(3) + norm(3)
    var data = new float[vcount * 6];
    for (int i = 0; i < vcount; i++)
    {
        var p = Mesh.points[i];
        var n = Mesh.normals[i];
        int o = i * 6;
        data[o + 0] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
        data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
    }

    // Indices
    var triCount = Mesh.triangles.Length;
    _meshIndexCount = triCount * 3;
    bool useShort = vcount <= ushort.MaxValue;

    _vaoMesh = GL.GenVertexArray();
    _vboMeshInterleaved = GL.GenBuffer();
    _eboMesh = GL.GenBuffer();

    GL.BindVertexArray(_vaoMesh);

    // Upload interleaved vertex data
    GL.BindBuffer(BufferTarget.ArrayBuffer, _vboMeshInterleaved);
    GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);

    // Upload index data *while VAO is bound* (EBO is VAO state)
    GL.BindBuffer(BufferTarget.ElementArrayBuffer, _eboMesh);
    if (useShort)
    {
        var indices = new ushort[_meshIndexCount];
        int k = 0;
        foreach (var t in Mesh.triangles)
        {
            indices[k++] = (ushort)t.vertexA;
            indices[k++] = (ushort)t.vertexB;
            indices[k++] = (ushort)t.vertexC;
        }
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(ushort), indices, BufferUsageHint.StaticDraw);
        _indexType = DrawElementsType.UnsignedShort;
    }
    else
    {
        var indices = new uint[_meshIndexCount];
        int k = 0;
        foreach (var t in Mesh.triangles)
        {
            indices[k++] = (uint)t.vertexA;
            indices[k++] = (uint)t.vertexB;
            indices[k++] = (uint)t.vertexC;
        }
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
        _indexType = DrawElementsType.UnsignedInt;
    }

    // --- Attribute setup (MATCHES your shader layout) ---
    // layout(location=0) vec3 aPos;
    GL.EnableVertexAttribArray(0);
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                           6 * sizeof(float), (IntPtr)0);

    // layout(location=1) vec3 aNormal;
    GL.EnableVertexAttribArray(1);
    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false,
                           6 * sizeof(float), (IntPtr)(3 * sizeof(float)));

    // Be explicit: no instancing
    GL.VertexAttribDivisor(0, 0);
    GL.VertexAttribDivisor(1, 0);

    // (Optional) unbind array buffer; VAO has captured the binding
    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    GL.BindVertexArray(0);

    Debug.WriteLine($"[BuildMeshBuffers] vcount={vcount}, tris={triCount}, idxCount={_meshIndexCount}, type={_indexType}, vao={_vaoMesh}, vbo={_vboMeshInterleaved}, ebo={_eboMesh}");
}



        private void BuildOutlineBuffer()
        {
            var lines = new List<float>(Shadow.SilhouetteEdges.Count * 2 * 3);
            foreach (var e in Shadow.SilhouetteEdges)
            {
                var a = Mesh.points[e.First];
                var b = Mesh.points[e.Second];
                lines.Add(a.X); lines.Add(a.Y); lines.Add(a.Z);
                lines.Add(b.X); lines.Add(b.Y); lines.Add(b.Z);
            }
            _outlineVertCount = lines.Count / 3;
            if (_outlineVertCount == 0) return;

            _vaoOutline = GL.GenVertexArray();
            _vboOutline = GL.GenBuffer();

            GL.BindVertexArray(_vaoOutline);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboOutline);
            GL.BufferData(BufferTarget.ArrayBuffer, lines.Count * sizeof(float), lines.ToArray(), BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

            GL.BindVertexArray(0);
        }
    }
}
