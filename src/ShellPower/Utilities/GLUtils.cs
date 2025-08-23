using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;          // GL (core bindings)
using OpenTK.Mathematics;              // Matrix4
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced; // Image<TPixel>
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing; // <= add this

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

        // Safe everywhere: assumes the texture is already bound to 'target'
        public static void FastTexSettingsBound(TextureTarget target = TextureTarget.Texture2D)
        {
            GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureWrapS,     (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(target, TextureParameterName.TextureWrapT,     (int)TextureWrapMode.ClampToEdge);
        }

        // Backward-compatible shim: bind the texture, set params, restore previous binding.
        // You can replace old callsites that pass an ID with this method and it will still work on macOS.
        public static void FastTexSettings(int textureId, TextureTarget target = TextureTarget.Texture2D)
        {
            GL.GetInteger(GetPName.TextureBinding2D, out int prev); // assumes Texture2D; adjust if you use other targets
            GL.BindTexture(target, textureId);
            FastTexSettingsBound(target);
            GL.BindTexture(target, prev);
        }
        
        private static Image<Rgba32> LoadTexture(string filename)
        {
            // ImageSharp auto-detects the format and decodes into RGBA8
            return Image.Load<Rgba32>(filename);
        }

        /// <summary>
        /// Upload an ImageSharp RGBA texture to the given texture object.
        /// (No unsafe; uses managed array overload.)
        /// </summary>
        public static void LoadTexture(Image<Rgba32> img, TextureUnit slot, int textureId)
        {
            if (img is null) throw new ArgumentNullException(nameof(img));

            // Flatten pixel buffer
            var pixels = img.GetPixelMemoryGroup()[0].ToArray();

            GL.ActiveTexture(slot);
            GL.BindTexture(TextureTarget.Texture2D, textureId);

            // Important on macOS: no row padding assumptions
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

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

            // Set filtering/wrap once while bound
            FastTexSettingsBound(TextureTarget.Texture2D);
        }
        
        // Hardened upload: handles large images, row alignment, and "first texture" on macOS.
        public static void UploadImageToTexture(Image<Rgba32> img, int textureId, TextureTarget target = TextureTarget.Texture2D)
        {
            if (img is null) throw new ArgumentNullException(nameof(img));
            if (textureId == 0) throw new InvalidOperationException("Texture not created/bound.");

            // 1) Query max texture size and downscale if needed
            GL.GetInteger(GetPName.MaxTextureSize, out int maxTexSize);
            int srcW = img.Width, srcH = img.Height;
            if (srcW > maxTexSize || srcH > maxTexSize)
            {
                double scale = Math.Min((double)maxTexSize / srcW, (double)maxTexSize / srcH);
                int dstW = Math.Max(1, (int)Math.Floor(srcW * scale));
                int dstH = Math.Max(1, (int)Math.Floor(srcH * scale));
                img = img.Clone(ctx => ctx.Resize(dstW, dstH));
                srcW = img.Width; srcH = img.Height;
                Debug.WriteLine($"[GLUpload] Resized to {srcW}x{srcH} (max {maxTexSize}).");
            }

            // 2) Bind + set safe pixel store
            GL.BindTexture(target, textureId);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        #if !WINDOWS
            // be explicit on non-Windows; 0 = tightly packed
            GL.PixelStore((PixelStoreParameter)0x0CF2 /*GL_UNPACK_ROW_LENGTH*/, 0);
        #endif

            // 3) Allocate storage first with NULL (some mac drivers prefer this)
            GL.TexImage2D(target, level: 0,
                internalformat: PixelInternalFormat.Rgba8, // 8-bit RGBA
                width: srcW, height: srcH, border: 0,
                format: PixelFormat.Rgba, type: PixelType.UnsignedByte,
                pixels: IntPtr.Zero);

            // 4) Upload real pixels via SubImage
            var pixels = img.GetPixelMemoryGroup()[0].ToArray();
            GL.TexSubImage2D(target, level: 0, xoffset: 0, yoffset: 0, width: srcW, height: srcH,
                format: PixelFormat.Rgba, type: PixelType.UnsignedByte, pixels: pixels);

            // 5) Set parameters while bound (no DSA)
            GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // 6) Validate + log
            var err = GL.GetError();
            if (err != ErrorCode.NoError)
                Debug.WriteLine($"[GLUpload] GL error after upload: {err}");

            GL.GetTexLevelParameter(target, 0, GetTextureParameter.TextureWidth, out int wid);
            GL.GetTexLevelParameter(target, 0, GetTextureParameter.TextureHeight, out int hei);
            Debug.WriteLine($"[GLUpload] Uploaded {wid}x{hei} into tex {textureId}");
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
