using System;
using OpenTK.Graphics.OpenGL;          // GL (core bindings)
using OpenTK.Mathematics;              // Matrix4
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced; // Image<TPixel>
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower
{
    public static class GLUtils
    {
        private static Matrix4 projection;
        private static Matrix4 view;

        // --- PROJECTION HELPERS (return matrices; upload as uniforms in your code) ---

        public static Matrix4 CreatePerspective(int w, int h, float fovRadians = (float)Math.PI / 6f,
                                                float zNear = 0.1f, float zFar = 1000f)
        {
            float aspect = w <= 0 || h <= 0 ? 1f : (float)w / h;
            return Matrix4.CreatePerspectiveFieldOfView(fovRadians, aspect, zNear, zFar);
        }

        /// <summary>
        /// Orthographic matrix where the smaller of (viewport width,height) spans minDim meters.
        /// </summary>
        public static Matrix4 CreateOrthoFittingMinDim(int viewportWidth, int viewportHeight,
                                                       double minDimMeters,
                                                       float zNear = 0.1f, float zFar = 100f)
        {
            if (viewportWidth <= 0 || viewportHeight <= 0)
                return Matrix4.Identity;

            double scale = Math.Max(minDimMeters / viewportWidth, minDimMeters / viewportHeight);
            float volW = (float)(scale * viewportWidth);
            float volH = (float)(scale * viewportHeight);
            return Matrix4.CreateOrthographic(volW, volH, zNear, zFar);
        }

        // If you need to set a mat4 uniform without GL4/unsafe:
        public static void SetMat4(int program, int baseLocation, in Matrix4 m)
        {
            // A GLSL mat4 occupies 4 consecutive vec4 uniforms (column-major).
            GL.UseProgram(program);
            GL.Uniform4(baseLocation + 0, m.M11, m.M21, m.M31, m.M41);
            GL.Uniform4(baseLocation + 1, m.M12, m.M22, m.M32, m.M42);
            GL.Uniform4(baseLocation + 2,  m.M13, m.M23, m.M33, m.M43);
            GL.Uniform4(baseLocation + 3, m.M14, m.M24, m.M34, m.M44);
        }

        // --- TEXTURE HELPERS (ImageSharp → GL) ---

        /// <summary>
        /// Set common sampling/wrap params (DSA; no bind required).
        /// </summary>
        public static void FastTexSettings(int textureId)
        {
            GL.TextureParameter(textureId, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(textureId, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TextureParameter(textureId, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TextureParameter(textureId, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        /// <summary>
        /// Upload an ImageSharp RGBA texture to the given texture object.
        /// (No unsafe; uses managed array overload.)
        /// </summary>
        public static void LoadTexture(Image<Rgba32> img, TextureUnit slot, int textureId)
        {
            if (img is null) throw new ArgumentNullException(nameof(img));

            // Flatten pixels to Rgba32[] (one contiguous memory group)
            var pixels = img.GetPixelMemoryGroup()[0].ToArray();

            GL.ActiveTexture(slot);
            GL.BindTexture(TextureTarget.Texture2D, textureId);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                level: 0,
                internalformat: PixelInternalFormat.Rgba8,
                width: img.Width,
                height: img.Height,
                border: 0,
                format: PixelFormat.Rgba,
                type: PixelType.UnsignedByte,
                pixels: pixels);

            FastTexSettings(textureId);
        }
        
        public static void SetCameraProjectionPerspective(int width, int height,
            float fovDeg = 60f,
            float near = 0.1f,
            float far = 1000f)
        {
            float aspect = (float)width / height;
            projection = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(fovDeg), aspect, near, far);

            // simple look-at: camera at (0,0,5) looking at (0,0,0)
            view = Matrix4.LookAt(new Vector3(0, 0, 5),
                Vector3.Zero,
                Vector3.UnitY);
        }
        
        public static void UploadCameraUniforms(int shaderProg, string projName="uProj", string viewName="uView")
        {
            int uProj = GL.GetUniformLocation(shaderProg, projName);
            int uView = GL.GetUniformLocation(shaderProg, viewName);

            if (uProj >= 0) GL.UniformMatrix4(uProj, false, ref projection);
            if (uView >= 0) GL.UniformMatrix4(uView, false, ref view);
        }

        public static Matrix4 Projection => projection;
        public static Matrix4 View => view;
    }
}
