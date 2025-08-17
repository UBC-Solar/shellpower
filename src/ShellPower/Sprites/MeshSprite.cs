using System;
using System.Collections.Generic;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System.Diagnostics;
using OpenTK.Mathematics;
using Vector3 = System.Numerics.Vector3;


namespace SSCP.ShellPower {
class MeshSprite : IDisposable
{
    private int _vao, _vbo, _ebo;
    private int _shaderProgram;
    private int _indexCount;

    public MeshSprite(Mesh mesh)
    {
        // 1. Flatten vertex data
        var vertices = new List<float>();
        for (int i = 0; i < mesh.points.Length; i++)
        {
            var p = mesh.points[i];
            var n = mesh.normals[i];
            // var c = mesh.VertexColors != null ? mesh.VertexColors[i] : new OpenTK.Mathematics.Vector4(1,1,1,1);

            // position
            vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z);
            // normal
            vertices.Add(n.X); vertices.Add(n.Y); vertices.Add(n.Z);
            // color
            vertices.Add(1f); vertices.Add(1f); vertices.Add(1f); vertices.Add(1f);
        }

        // 2. Flatten indices
        var indices = new List<uint>();
        foreach (var tri in mesh.triangles)
        {
            indices.Add((uint)tri.vertexA);
            indices.Add((uint)tri.vertexB);
            indices.Add((uint)tri.vertexC);
        }
        _indexCount = indices.Count;

        // 3. Generate VAO/VBO/EBO
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);

        int stride = (3 + 3 + 4) * sizeof(float);

        // position attribute (location=0)
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);

        // normal attribute (location=1)
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        // color attribute (location=2)
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);

        // 4. Compile simple shader
        _shaderProgram = CompileShaderProgram();
    }
    
    private int _uViewProjLoc;
    private int _uModelLoc;

    private static void SetMat4(int programId, int baseLocation, in Matrix4 m)
    {
        GL.UseProgram(programId); // bind program (required in OpenGL core bindings)

        // GLSL mat4 is column-major: send 4 column vec4s
        GL.Uniform4(baseLocation + 0,  m.M11, m.M21, m.M31, m.M41);
        GL.Uniform4(baseLocation + 1,  m.M12, m.M22, m.M32, m.M42);
        GL.Uniform4(baseLocation + 2,  m.M13, m.M23, m.M33, m.M43);
        GL.Uniform4(baseLocation + 3,  m.M14, m.M24, m.M34, m.M44);
    }

    public void Render(Matrix4 viewProj, Matrix4 model)
    {
        GL.UseProgram(_shaderProgram);

        // Pass MVP matrices
        SetMat4(_shaderProgram, _uViewProjLoc, viewProj);
        SetMat4(_shaderProgram, _uModelLoc,    model);

        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);

        GL.UseProgram(0);
    }

    private int CompileShaderProgram()
    {
        string vsSource = @"
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

        string fsSource = @"
            #version 330 core
            in vec4 vColor;
            out vec4 FragColor;

            void main() {
                FragColor = vColor;
            }";

        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vsSource);
        GL.CompileShader(vs);

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fsSource);
        GL.CompileShader(fs);

        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vs);
        GL.AttachShader(prog, fs);
        GL.LinkProgram(prog);
        
        _uViewProjLoc = GL.GetUniformLocation(_shaderProgram, "uViewProj");
        _uModelLoc    = GL.GetUniformLocation(_shaderProgram, "uModel");

        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return prog;
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteProgram(_shaderProgram);
    }

    // /// <summary>
    // /// Returns a depth t such that position + direction*t = a point in triangle. Note that this may be a positive or negative number.
    // /// Returns NaN if the ray does not intersect the triangle or if direction is the zero vector.
    // /// </summary>
    // public float Intersect(Mesh.Triangle triangle, Vector3 position, Vector3 direction) {
    //     //transform coords so that position=0, triangle = (v1, v2, v3)
    //     // find a b c such that
    //     // a*v1x + b*v1y + c*v1z = 1
    //     // a*v2x + b*v2y + c*v2z = 1
    //     // a*v3x + b*v3y + c*v3z = 1
    //
    //     // (v1 v2 v3)T(a b c)T = (1 1 1)
    //     // (a b c) = (v1 v2 v3)T^-1 (1 1 1)
    //     var m = new Matrix3(
    //         Mesh.points[triangle.vertexA],
    //         Mesh.points[triangle.vertexB],
    //         Mesh.points[triangle.vertexC]);
    //     m.Transpose();
    //     var inv = m.Inverse;
    //     var abc = inv * new Vector3(1, 1, 1);
    //
    //     //(p + t*d) dot (a b c) = 1
    //     //p dot (a b c) + t*d dot (a b c) = 1
    //     //t = (1 - p dot (a b c)) / (d dot (a b c))
    //     var t = (1f - Vector3.Dot(position, abc)) / Vector3.Dot(direction, abc);
    //     var intersection = position + direction * t;
    //
    //     //next, find the intersection
    //     //i = p + t*d
    //     //ap*v1 + bp*v2 + cp*v3 = i
    //     //(ap bp cp) = (v1 v2 v3)T^-1 (1 1 1)
    //     var abcPrime = inv * intersection;
    //
    //     //if any of the components (ap bp cp) is outside of [0, 1], 
    //     //then the ray does not intersect the triangle
    //     if (abcPrime.X < 0 || abcPrime.X > 1)
    //         return float.NaN;
    //     if (abcPrime.Y < 0 || abcPrime.Y > 1)
    //         return float.NaN;
    //     if (abcPrime.Z < 0 || abcPrime.Z > 1)
    //         return float.NaN;
    //     return t;
    // }
    }
}
