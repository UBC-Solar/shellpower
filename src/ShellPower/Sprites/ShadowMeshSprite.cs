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

        // GL objects (created lazily)
        int _vaoMesh, _vboMeshInterleaved, _eboMesh, _meshIndexCount;
        int _vaoOutline, _vboOutline, _outlineVertCount;
        int _vaoVolume, _vboVolume, _volumeVertCount;

        bool _glReady; // set true after first EnsureGlResourcesCreated()

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
            Debug.WriteLine($"[RenderMesh] bound VAO={_vaoMesh} VBO={vbo} IBO={ibo} idxType={_indexType}");
            
            Debug.WriteLine($"[RenderMesh] count={_meshIndexCount} type={_indexType} vao={_vaoMesh}");
            
            // === DEBUG: draw with DrawArrays from positions only ===
            // 1) bind no VAO
            GL.BindVertexArray(0);

            // 2) bind positions VBO and point to shader location for aPos
            int posLoc = GL.GetAttribLocation(_shaderProg, "aPos");   // don't assume 0
            if (posLoc >= 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vboVerts);
                GL.EnableVertexAttribArray(posLoc);
                GL.VertexAttribPointer(posLoc, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            }

            // 3) disable normal usage in shader path by forcing solid color
            // (uMode is already set to 1 higher up in your Step 1 test)
            SetMode(1);
            SetColor(1, 0, 0, 1); // red

            // 4) draw N vertices directly (no indices, just triangles)
            int vertexCount = _vertexCount; // positions.Length / 3 (store this when you upload)
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.DrawArrays(PrimitiveType.Triangles, 0, vertexCount);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);

            // === end DEBUG ===
            
            GL.DrawElements(PrimitiveType.Triangles, _meshIndexCount, _indexType, IntPtr.Zero);

            var err = GL.GetError();
            if (err != ErrorCode.NoError)
                Debug.WriteLine($"GL error in RenderMesh: {err}");

            GL.BindVertexArray(0);
        }


        public void RenderShadowOutline()
        {
            if (!_glReady || _vaoOutline == 0 || _outlineVertCount == 0) return;
            GL.BindVertexArray(_vaoOutline);
            GL.DrawArrays(PrimitiveType.Lines, 0, _outlineVertCount);
            GL.BindVertexArray(0);
        }

        public void RenderShadowVolume()
        {
            if (!_glReady || _vaoVolume == 0 || _volumeVertCount == 0) return;
            GL.BindVertexArray(_vaoVolume);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, _volumeVertCount);
            GL.BindVertexArray(0);
        }

        public void UpdateShadowVolumeVertices()
        {
            if (!_glReady) return;

            if (Shadow.Light.Y <= 0f)
            {
                _volumeVertCount = 0;
                return;
            }

            float minY = Mesh.points.Min(p => p.Y) + Position.Y;

            var L = Shadow.Light; // Vector4
            float Ly = MathF.Abs(L.Y) < 1e-8f ? 1e-8f : L.Y;

            var verts = new List<float>(Shadow.SilhouetteEdges.Count * 4 * 3);
            foreach (var e in Shadow.SilhouetteEdges)
            {
                var p0 = Mesh.points[e.First];
                var p1 = Mesh.points[e.Second];
                var w0 = new Vector3(p0.X + Position.X, p0.Y + Position.Y, p0.Z + Position.Z);
                var w1 = new Vector3(p1.X + Position.X, p1.Y + Position.Y, p1.Z + Position.Z);

                float k0 = (w0.Y - minY) / Ly;
                float k1 = (w1.Y - minY) / Ly;
                var w0b = new Vector3(w0.X - L.X * k0, w0.Y - L.Y * k0, w0.Z - L.Z * k0);
                var w1b = new Vector3(w1.X - L.X * k1, w1.Y - L.Y * k1, w1.Z - L.Z * k1);

                // strip order: p0, p1, p0', p1'
                verts.Add(w0.X); verts.Add(w0.Y); verts.Add(w0.Z);
                verts.Add(w1.X); verts.Add(w1.Y); verts.Add(w1.Z);
                verts.Add(w0b.X); verts.Add(w0b.Y); verts.Add(w0b.Z);
                verts.Add(w1b.X); verts.Add(w1b.Y); verts.Add(w1b.Z);
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
        }

        private DrawElementsType _indexType; // store what type we uploaded

        private void BuildMeshBuffers()
        {
            int vcount = Mesh.points.Length;

            if (Mesh.points.Length != Mesh.normals.Length)
                throw new InvalidOperationException("Points and normals count mismatch.");

            // Interleaved data: pos(3) + normal(3)
            var data = new float[vcount * 6];
            for (int i = 0; i < vcount; i++)
            {
                var p = Mesh.points[i];
                var n = Mesh.normals[i];
                int o = i * 6;
                data[o + 0] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
                data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
            }

            // Build index buffer
            var triCount = Mesh.triangles.Length;
            _meshIndexCount = triCount * 3;

            // If possible, use ushort indices (safe on macOS)
            bool useShort = vcount <= ushort.MaxValue;

            _vaoMesh = GL.GenVertexArray();
            _vboMeshInterleaved = GL.GenBuffer();
            _eboMesh = GL.GenBuffer();

            GL.BindVertexArray(_vaoMesh);

            // Upload vertex data
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboMeshInterleaved);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);

            // Upload index data
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _eboMesh);

            if (useShort)
            {
                var indices = new ushort[_meshIndexCount];
                int k = 0;
                foreach (var t in Mesh.triangles)
                {
                    if (t.vertexA >= vcount || t.vertexB >= vcount || t.vertexC >= vcount)
                        throw new InvalidOperationException("Triangle references invalid point index.");

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
                    if (t.vertexA >= vcount || t.vertexB >= vcount || t.vertexC >= vcount)
                        throw new InvalidOperationException("Triangle references invalid point index.");

                    indices[k++] = (uint)t.vertexA;
                    indices[k++] = (uint)t.vertexB;
                    indices[k++] = (uint)t.vertexC;
                }

                GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
                _indexType = DrawElementsType.UnsignedInt;
            }

            // layout 0: position
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);

            // layout 1: normal
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

            GL.BindVertexArray(0);

            Debug.WriteLine($"Mesh buffers built: vcount={vcount}, tris={triCount}, indexType={_indexType}");
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
