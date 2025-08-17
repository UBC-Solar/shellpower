using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL;
// using OpenTK.Mathematics; // Matrix4, Vector3
using System.Numerics;
using Matrix4 = OpenTK.Mathematics.Matrix4;

namespace SSCP.ShellPower
{
    public sealed class ShadowMeshSprite
    {
        // Public knobs
        public Vector3 Position { get; set; } = Vector3.Zero;
        public bool ShowShadowVolume { get; set; } = true;
        public bool ShowShadowOutline { get; set; } = true;

        // Inputs
        public Mesh Mesh { get; }
        public Shadow Shadow { get; }

        // GL objects
        int _vao, _vbo, _ebo;                 // mesh
        int _vaoOutline, _vboOutline;         // outline (lines)
        int _vaoVolume, _vboVolume;           // shadow volume (tri strips)
        int _prog;                            // shader
        int _uViewProj, _uModel, _uColor;     // uniforms

        int _indexCount;
        int _outlineVertCount;
        int _volumeVertCount;

        public ShadowMeshSprite(Shadow shadow)
        {
            Shadow = shadow;
            Mesh = shadow.Mesh;

            CompileShader();
            BuildMeshBuffers();
            BuildOutlineBuffer();
            // volume is dynamic (depends on light & minY); build on demand per frame
        }

        // --------- public API you call each frame ----------
        public void Render(Matrix4 viewProj)
        {
            Matrix4 model = Matrix4.CreateTranslation(Position.X, Position.Y, Position.Z);

            // 1) Solid mesh (white or per-face color if you wire it in)
            GL.UseProgram(_prog);
            SetMat4(_prog, _uViewProj, viewProj);
            SetMat4(_prog, _uModel,    model);
            GL.Uniform4(_uColor, 1f, 1f, 1f, 1f);

            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);

            // 2) Shadow volume (translucent) — extrude silhouette to floor plane
            if (ShowShadowVolume && Shadow.Light.Y > 0f)
            {
                UpdateShadowVolumeVertices(); // rebuild CPU buffer to current light
                if (_volumeVertCount > 0)
                {
                    GL.Uniform4(_uColor, 0f, 0f, 1f, 0.4f); // translucent blue-ish
                    GL.BindVertexArray(_vaoVolume);
                    GL.DrawArrays(PrimitiveType.TriangleStrip, 0, _volumeVertCount);
                    GL.BindVertexArray(0);
                }
            }

            // 3) Shadow outline (red lines)
            if (ShowShadowOutline && Shadow.Light.Y > 0f && _outlineVertCount > 0)
            {
                GL.Uniform4(_uColor,  1f, 0f, 0f, 1f);
                GL.BindVertexArray(_vaoOutline);
                GL.DrawArrays(PrimitiveType.Lines, 0, _outlineVertCount);
                GL.BindVertexArray(0);
            }

            GL.UseProgram(0);
        }

        // --------- GL setup ----------
        void CompileShader()
        {
            string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uViewProj;
uniform mat4 uModel;
void main(){
    gl_Position = uViewProj * uModel * vec4(aPos,1.0);
}";
            string fs = @"#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main(){ FragColor = uColor; }";

            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vs); GL.CompileShader(v);
            int f = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(f, fs); GL.CompileShader(f);

            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, v);
            GL.AttachShader(_prog, f);
            GL.LinkProgram(_prog);
            GL.DeleteShader(v); GL.DeleteShader(f);

            _uViewProj = GL.GetUniformLocation(_prog, "uViewProj");
            _uModel    = GL.GetUniformLocation(_prog, "uModel");
            _uColor    = GL.GetUniformLocation(_prog, "uColor");
        }

        void BuildMeshBuffers()
        {
            // Interleave only positions; you can extend to normals/colors if your shader uses them.
            var verts = new List<float>(Mesh.points.Length * 3);
            foreach (var p in Mesh.points) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); }

            var indices = new List<uint>(Mesh.triangles.Length * 3);
            foreach (var t in Mesh.triangles)
            {
                indices.Add((uint)t.vertexA);
                indices.Add((uint)t.vertexB);
                indices.Add((uint)t.vertexC);
            }
            _indexCount = indices.Count;

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();

            GL.BindVertexArray(_vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

            GL.BindVertexArray(0);
        }

        void BuildOutlineBuffer()
        {
            // Build a line list from silhouette edges
            var lines = new List<float>(Shadow.SilhouetteEdges.Count * 2 * 3);
            foreach (var e in Shadow.SilhouetteEdges)
            {
                var a = Mesh.points[e.First];
                var b = Mesh.points[e.Second];
                lines.Add(a.X); lines.Add(a.Y); lines.Add(a.Z);
                lines.Add(b.X); lines.Add(b.Y); lines.Add(b.Z);
            }
            _outlineVertCount = lines.Count / 3;

            _vaoOutline = GL.GenVertexArray();
            _vboOutline = GL.GenBuffer();

            GL.BindVertexArray(_vaoOutline);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboOutline);
            GL.BufferData(BufferTarget.ArrayBuffer, lines.Count * sizeof(float), lines.ToArray(), BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.BindVertexArray(0);
        }

        void UpdateShadowVolumeVertices()
        {
            // Build triangle strips per-edge extruded down to minY plane
            float minY = Mesh.points.Min(p => p.Y) + Position.Y;

            // Light as Vector3 (drop W), guard against nearly-zero Y
            Vector4 L4 = Shadow.Light;                     // System.Numerics.Vector4
            Vector3 L  = new Vector3(L4.X, L4.Y, L4.Z);    // project to xyz
            float Ly   = (MathF.Abs(L.Y) < 1e-6f) ? 1e-6f : L.Y;

            // For each edge, output 4 vertices (v0, v1, v0', v1') as a strip
            var verts = new List<float>(Shadow.SilhouetteEdges.Count * 4 * 3);

            foreach (var e in Shadow.SilhouetteEdges)
            {
                Vector3 p0 = Mesh.points[e.First]  + Position;
                Vector3 p1 = Mesh.points[e.Second] + Position;

                // Project along light to the floor (Y = minY): p' = p - L * ((p.Y - minY) / L.Y)
                float k0 = (p0.Y - minY) / Ly;
                float k1 = (p1.Y - minY) / Ly;
                Vector3 p0b = p0 - L * k0;
                Vector3 p1b = p1 - L * k1;

                // strip order: p0, p1, p0b, p1b
                verts.Add(p0.X); verts.Add(p0.Y); verts.Add(p0.Z);
                verts.Add(p1.X); verts.Add(p1.Y); verts.Add(p1.Z);
                verts.Add(p0b.X); verts.Add(p0b.Y); verts.Add(p0b.Z);
                verts.Add(p1b.X); verts.Add(p1b.Y); verts.Add(p1b.Z);
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

        // Set a mat4 uniform using 4× vec4 writes (works in OpenTK.Graphics.OpenGL without unsafe)
        static void SetMat4(int programId, int baseLoc, in Matrix4 m)
        {
            GL.UseProgram(programId);
            GL.Uniform4(baseLoc + 0,  m.M11, m.M21, m.M31, m.M41);
            GL.Uniform4(baseLoc + 1,  m.M12, m.M22, m.M32, m.M42);
            GL.Uniform4(baseLoc + 2,  m.M13, m.M23, m.M33, m.M43);
            GL.Uniform4(baseLoc + 3,  m.M14, m.M24, m.M34, m.M44);
        }
    }
}
