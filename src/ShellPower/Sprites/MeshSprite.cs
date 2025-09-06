using OpenTK.Graphics.OpenGL;
using System;
using System.Linq;

namespace SSCP.ShellPower
{
    /// GPU wrapper for Mesh: creates VAO with:
    ///   attrib 0 = position (vec3)
    ///   attrib 1 = normal   (vec3)
public sealed class MeshSprite : IDisposable
{
    public int Vao { get; private set; }
    public int IndexCount { get; private set; }

    private int _vbo = 0;
    private int _ebo = 0;

    public MeshSprite(Mesh mesh)
    {
        if (mesh.points.Length != mesh.normals.Length)
            throw new ArgumentException("points/normals length mismatch");

        // Interleave pos(3) + nrm(3)
        var verts = new float[mesh.points.Length * 6];
        for (int i = 0; i < mesh.points.Length; i++)
        {
            var p = mesh.points[i];
            var n = mesh.normals[i];
            int o = i * 6;
            verts[o+0]=p.X; verts[o+1]=p.Y; verts[o+2]=p.Z;
            verts[o+3]=n.X; verts[o+4]=n.Y; verts[o+5]=n.Z;
        }

        // Indices (uint)
        var idx = new uint[mesh.triangles.Length * 3];
        for (int t = 0; t < mesh.triangles.Length; t++)
        {
            idx[t*3+0] = (uint)mesh.triangles[t].vertexA;
            idx[t*3+1] = (uint)mesh.triangles[t].vertexB;
            idx[t*3+2] = (uint)mesh.triangles[t].vertexC;
        }
        IndexCount = idx.Length;

        Vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(Vao);

        // VBO
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

        // EBO — bind while VAO is bound so it latches to this VAO
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);                 // (bind while VAO is bound!)
        GL.BufferData(BufferTarget.ElementArrayBuffer, idx.Length * sizeof(uint), idx, BufferUsageHint.StaticDraw);

        int stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        // Leave EBO bound; DO NOT unbind it, or the VAO will lose it.
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (Vao != 0) { GL.DeleteVertexArray(Vao); Vao = 0; }
        if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
        if (_ebo != 0) { GL.DeleteBuffer(_ebo); _ebo = 0; }
    }
}}