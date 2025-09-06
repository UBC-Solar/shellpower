using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace SSCP.ShellPower
{
    class MeshSprite : IDisposable
    {
        private int _vao, _vbo, _ebo;
        private int _indexCount;

        // Optional internal debug shader (not used by simulator path)
        private int _shaderProgram = 0;
        private int _uViewProjLoc = -1;
        private int _uModelLoc    = -1;

        public int Vao        => _vao;
        public int IndexCount => _indexCount;

        public MeshSprite(Mesh mesh)
        {
            // 1) Flatten interleaved vertex data: [pos(3), normal(3), color(4)]
            var vertices = new List<float>(mesh.points.Length * 10);
            for (int i = 0; i < mesh.points.Length; i++)
            {
                var p = mesh.points[i];
                var n = mesh.normals[i];

                // position
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z);
                // normal
                vertices.Add(n.X); vertices.Add(n.Y); vertices.Add(n.Z);
                // color (unused by sim, kept for debug shader)
                vertices.Add(1f); vertices.Add(1f); vertices.Add(1f); vertices.Add(1f);
            }

            // 2) Indices
            var indices = new List<uint>(mesh.triangles.Length * 3);
            foreach (var tri in mesh.triangles)
            {
                indices.Add((uint)tri.vertexA);
                indices.Add((uint)tri.vertexB);
                indices.Add((uint)tri.vertexC);
            }
            _indexCount = indices.Count;

            // 3) VAO / VBO / EBO
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();

            GL.BindVertexArray(_vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                          vertices.Count * sizeof(float),
                          vertices.ToArray(),
                          BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                          indices.Count * sizeof(uint),
                          indices.ToArray(),
                          BufferUsageHint.StaticDraw);

            int stride = (3 + 3 + 4) * sizeof(float);

            // aPos -> location 0
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(index: 0, size: 3, VertexAttribPointerType.Float, normalized: false,
                                   stride: stride, offset: 0);

            // aNormal -> location 1
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(index: 1, size: 3, VertexAttribPointerType.Float, normalized: false,
                                   stride: stride, offset: 3 * sizeof(float));

            // aColor -> location 2 (not used by sim shader, but harmless)
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(index: 2, size: 4, VertexAttribPointerType.Float, normalized: false,
                                   stride: stride, offset: 6 * sizeof(float));

            GL.BindVertexArray(0);

            // Optional: build a tiny internal shader for debugging
            _shaderProgram = CompileShaderProgram();
        }

        /// <summary>
        /// Draw using whatever program is currently bound (does NOT call UseProgram).
        /// </summary>
        public void RenderVAO()
        {
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
            GL.BindVertexArray(0);
        }

        // /// <summary>
        // /// Draw using an external program that has a single mat4 'uMvp' uniform.
        // /// Does NOT change vertex attrib locations; assumes aPos=0, aNormal=1.
        // /// </summary>
        // public void RenderWithExternalProgram(int program, int uMvpLoc, in Matrix4 mvp)
        // {
        //     GL.UseProgram(program);
        //     GL.UniformMatrix4(uMvpLoc, false, ref mvp);
        //     RenderVAO();
        //     // leave program bound for caller; they usually keep it during the pass
        // }

        /// <summary>
        /// Optional debug path with its own minimalist shader (uViewProj * uModel).
        /// </summary>
        public void RenderDebug(Matrix4 viewProj, Matrix4 model)
        {
            if (_shaderProgram == 0) return;

            GL.UseProgram(_shaderProgram);
            // GLSL mat4 is column-major; OpenTK handles the transpose param
            GL.UniformMatrix4(_uViewProjLoc, false, ref viewProj);
            GL.UniformMatrix4(_uModelLoc,    false, ref model);

            RenderVAO();

            GL.UseProgram(0);
        }

        private int CompileShaderProgram()
        {
            const string vsSource = @"
                #version 330 core
                layout (location = 0) in vec3 aPos;
                layout (location = 1) in vec3 aNormal;
                layout (location = 2) in vec4 aColor;

                uniform mat4 uViewProj;
                uniform mat4 uModel;

                out vec4 vColor;

                void main() {
                    gl_Position = uViewProj * uModel * vec4(aPos, 1.0);
                    vColor = aColor;
                }";

            const string fsSource = @"
                #version 330 core
                in vec4 vColor;
                out vec4 FragColor;
                void main() {
                    FragColor = vColor;
                }";

            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vsSource);
            GL.CompileShader(vs);
            GL.GetShader(vs, ShaderParameter.CompileStatus, out int okVS);
            if (okVS == 0) throw new InvalidOperationException("MeshSprite VS: " + GL.GetShaderInfoLog(vs));

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fsSource);
            GL.CompileShader(fs);
            GL.GetShader(fs, ShaderParameter.CompileStatus, out int okFS);
            if (okFS == 0) throw new InvalidOperationException("MeshSprite FS: " + GL.GetShaderInfoLog(fs));

            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int okLink);
            if (okLink == 0) throw new InvalidOperationException("MeshSprite Link: " + GL.GetProgramInfoLog(prog));

            // Now that we have 'prog', set fields and query uniforms against it
            _shaderProgram = prog;
            _uViewProjLoc = GL.GetUniformLocation(prog, "uViewProj");
            _uModelLoc    = GL.GetUniformLocation(prog, "uModel");

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            return prog;
        }

        public void Dispose()
        {
            if (_shaderProgram != 0) GL.DeleteProgram(_shaderProgram);
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_ebo != 0) GL.DeleteBuffer(_ebo);
            _shaderProgram = _vao = _vbo = _ebo = 0;
        }
    }
}