using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;          // GL (OpenTK 4 core bindings ok)
using OpenTK.Mathematics;              // Matrix4, Vector2, Vector3, Vector4
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace TexLib
{
    public static class TexUtil
    {
        /// <summary>Set basic state for alpha-blended textured quads.</summary>
        public static void InitTexturing()
        {
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        }

        public static int CreateRGBTexture(int width, int height, byte[] rgb) =>
            CreateTexture(width, height, alpha: false, rgb);

        public static int CreateRGBATexture(int width, int height, byte[] rgba) =>
            CreateTexture(width, height, alpha: true, rgba);

        /// <summary>Load an ImageSharp image into a GL texture.</summary>
        public static int CreateTextureFromImage(Image<Rgba32> img)
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);

            var pixels = img.GetPixelMemoryGroup()[0].ToArray(); // Rgba32[]
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          img.Width, img.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            // basic params
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            return tex;
        }

        /// <summary>Load from file via ImageSharp, then upload.</summary>
        public static int CreateTextureFromFile(string path)
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            return CreateTextureFromImage(img);
        }

        // --- internals ---

        private static int CreateTexture(int width, int height, bool alpha, byte[] bytes)
        {
            int expected = width * height * (alpha ? 4 : 3);
            Debug.Assert(expected == bytes.Length);

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);

            var internalFormat = alpha ? PixelInternalFormat.Rgba8 : PixelInternalFormat.Rgb8;
            var format         = alpha ? PixelFormat.Rgba       : PixelFormat.Rgb;

            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                          width, height, 0, format, PixelType.UnsignedByte, bytes);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            return tex;
        }
    }

    /// <summary>
    /// Shader-based bitmap font renderer for a 16x16 ASCII atlas.
    /// </summary>
    public sealed class TextureFont : IDisposable
    {
        private readonly int _texId;

        // GL objects
        private int _prog;
        private int _vao;
        private int _vbo; // interleaved position (xy) + uv
        private int _uMvp;
        private int _uTint;

        // batched vertices: 6 verts per glyph (two triangles)
        private float[] _vertexScratch = Array.Empty<float>();

        public TextureFont(int textureId)
        {
            _texId = textureId;
            CompileShader();
            CreateBuffers();
        }

        /// <summary>
        /// Distance between glyph centers in model space.
        /// Height of glyphs is 1.0 by convention; default advance 0.75.
        /// </summary>
        public double AdvanceWidth { get; set; } = 0.75;

        /// <summary>
        /// Fraction of the atlas cell to sample (avoid bleeding due to filtering).
        /// </summary>
        public double CharacterBoundingBoxWidth  { get; set; } = 0.8;
        public double CharacterBoundingBoxHeight { get; set; } = 0.8;

        /// <summary>
        /// Draw string with a provided MVP (use this for in-scene or overlay).
        /// </summary>
        public void WriteString(string text, in Matrix4 mvp, Vector4? tint = null)
        {
            if (string.IsNullOrEmpty(text))
                return;

            EnsureScratchCapacity(text.Length);

            // Build quad geometry in model space: each glyph 1×1, origin top-left of glyph box
            int cursor = 0;
            double x = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                // atlas coords
                byte ascii = unchecked((byte)ch);
                int row = ascii >> 4;
                int col = ascii & 0x0F;

                double cx = (col + 0.5) * Sixteenth;
                double cy = (row + 0.5) * Sixteenth;
                double halfH = CharacterBoundingBoxHeight * Sixteenth / 2.0;
                double halfW = CharacterBoundingBoxWidth  * Sixteenth / 2.0;

                double uL = cx - halfW, uR = cx + halfW;
                double vT = cy - halfH, vB = cy + halfH;

                // quad in model space: (x,0) to (x+1,1)
                // two triangles: (x,1)-(x+1,1)-(x+1,0) and (x,1)-(x+1,0)-(x,0)
                PushVertex(ref cursor,  (float)x, 1f,  (float)uL, (float)vT);
                PushVertex(ref cursor,  (float)(x+1), 1f,  (float)uR, (float)vT);
                PushVertex(ref cursor,  (float)(x+1), 0f,  (float)uR, (float)vB);

                PushVertex(ref cursor,  (float)x, 1f,  (float)uL, (float)vT);
                PushVertex(ref cursor,  (float)(x+1), 0f,  (float)uR, (float)vB);
                PushVertex(ref cursor,  (float)x, 0f,  (float)uL, (float)vB);

                x += AdvanceWidth;
            }

            int vertCount = cursor / 4; // (x,y,u,v) per vertex

            // Upload & draw
            GL.UseProgram(_prog);

            // Set MVP (using 4×vec4 uploads)
            SetMat4(_prog, _uMvp, mvp);

            var color = tint ?? new Vector4(1, 1, 1, 1);
            GL.Uniform4(_uTint, color.X, color.Y, color.Z, color.W);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texId);

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * cursor, _vertexScratch, BufferUsageHint.DynamicDraw);

            GL.DrawArrays(PrimitiveType.Triangles, 0, vertCount);

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        /// <summary>
        /// Convenience overlay draw in a 0..100 x 0..100 virtual screen,
        /// centered at (xPercent,yPercent) with given heightPercent.
        /// </summary>
        public void WriteStringAt(string text, double heightPercent, double xPercent, double yPercent, double degreesCCW)
        {
            // Build a simple 2D MVP without using MatrixMode
            // Projection: ortho 0..100 (x), 0..100 (y)
            var proj = Matrix4.CreateOrthographicOffCenter(0, 100, 0, 100, -1, 1);

            // Model: translate to requested center, scale to requested height, rotate
            // Keep glyph height = 1 → scale by heightPercent in Y and by aspect-corrected value in X
            float aspect = GetViewportAspect(); // h/w (to mirror your legacy ComputeAspectRatio)
            float sY = (float)heightPercent;
            float sX = aspect * sY;

            var model =
                Matrix4.CreateRotationZ(MathHelper.DegreesToRadians((float)degreesCCW)) *
                Matrix4.CreateScale(sX, sY, 1f) *
                Matrix4.CreateTranslation((float)xPercent, (float)yPercent, 0f);

            var mvp = model * proj; // no view in 2D overlay

            WriteString(text, mvp);
        }

        public double ComputeWidth(string text) => text?.Length * AdvanceWidth ?? 0.0;

        // ---- internals ----

        private void CompileShader()
        {
            const string vs = @"#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUV;
uniform mat4 uMvp;
out vec2 vUV;
void main() {
    gl_Position = uMvp * vec4(aPos, 0.0, 1.0);
    vUV = aUV;
}";
            const string fs = @"#version 330 core
in vec2 vUV;
uniform sampler2D uTex0;
uniform vec4 uTint;
out vec4 FragColor;
void main() {
    vec4 s = texture(uTex0, vUV);
    FragColor = s * uTint;
}";

            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vs); GL.CompileShader(v);
            int f = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(f, fs); GL.CompileShader(f);

            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, v);
            GL.AttachShader(_prog, f);
            GL.LinkProgram(_prog);
            GL.DeleteShader(v);
            GL.DeleteShader(f);

            _uMvp  = GL.GetUniformLocation(_prog, "uMvp");
            _uTint = GL.GetUniformLocation(_prog, "uTint");
            // sampler defaults to 0 (TextureUnit0); set if needed:
            int uTex0 = GL.GetUniformLocation(_prog, "uTex0");
            GL.UseProgram(_prog);
            if (uTex0 >= 0) GL.Uniform1(uTex0, 0);
            GL.UseProgram(0);
        }

        private void CreateBuffers()
        {
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

            // Interleaved: aPos.xy (2 floats), aUV.xy (2 floats)
            int stride = 4 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));

            GL.BindVertexArray(0);
        }

        private void EnsureScratchCapacity(int glyphCount)
        {
            int neededFloats = glyphCount * 6 /*tri verts*/ * 4 /*xyuv*/;
            if (_vertexScratch.Length < neededFloats)
                _vertexScratch = new float[Math.Max(neededFloats, _vertexScratch.Length * 2 + 256)];
        }

        private static void SetMat4(int programId, int baseLocation, in Matrix4 m)
        {
            // column-major upload via 4 vec4s (works with OpenTK.Graphics.OpenGL)
            GL.UseProgram(programId);
            GL.Uniform4(baseLocation + 0,  m.M11, m.M21, m.M31, m.M41);
            GL.Uniform4(baseLocation + 1,  m.M12, m.M22, m.M32, m.M42);
            GL.Uniform4(baseLocation + 2,  m.M13, m.M23, m.M33, m.M43);
            GL.Uniform4(baseLocation + 3,  m.M14, m.M24, m.M34, m.M44);
        }

        // JIT inliner can’t see _vertexScratch above in a static method; using instance method:
        private void PushVertex(ref int cursor, float x, float y, float u, float v)
        {
            _vertexScratch[cursor++] = x;
            _vertexScratch[cursor++] = y;
            _vertexScratch[cursor++] = u;
            _vertexScratch[cursor++] = v;
        }

        private static float GetViewportAspect()
        {
            int[] vp = new int[4];
            GL.GetInteger(GetPName.Viewport, vp);
            int w = Math.Max(1, vp[2]);
            int h = Math.Max(1, vp[3]);
            return (float)h / w; // matches your legacy ComputeAspectRatio()
        }

        public void Dispose()
        {
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_prog != 0) GL.DeleteProgram(_prog);
            _vbo = _vao = _prog = 0;
        }

        private const double Sixteenth = 1.0 / 16.0;
    }
}
