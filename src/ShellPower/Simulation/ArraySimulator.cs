// Avalonia + OpenTK 4.x conversion (v2) — uses OpenTK.Mathematics for GL-bound matrices
// Packages: Avalonia, Avalonia.Desktop, Avalonia.OpenGL, OpenTK (>=4), SixLabors.ImageSharp
// Removes System.Drawing; uses ImageSharp Rgba32. Uses core OpenTK OpenGL bindings.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics; // Vector3 for model-space math
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL;   // GL API (core, cross-version)
using OpenTK.Mathematics;       // Matrix4, Vector3 for GL
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower
{
    public sealed class ArraySimulator
    {
        private const int COMPUTE_TEX_SIZE = 2048;

        // GL resources
        private int _vs, _fs, _prog;
        private int _uMvp, _uX0, _uX1, _uZ0, _uZ1, _uPixelWattsIn, _uPixelArea, _uSolarCells;
        private bool _mrtEnabled = false; // set to true when you actually attach CA1/CA2
        
        // Input texture (layout)
        private int _texArray;
        private Image<Rgba32>? _cacheSolarCells;

        // Output MRTs
        private int _texCells, _texWatts, _texArea, _texDepth, _fbo;
        private int _w = COMPUTE_TEX_SIZE, _h = COMPUTE_TEX_SIZE;

        private bool _glInit;
        
        private bool _preview = true; // set to false to hide (but keep computing)
        public bool Preview
        {
            get => _preview;
            set => _preview = value;
        }

        public ArraySimulator() { }

        // ---- Helpers for logs & type bridging ----
        private static string ShaderLog(int shader) { GL.GetShaderInfoLog(shader, out string log); return log; }
        private static string ProgramLog(int program) { GL.GetProgramInfoLog(program, out string log); return log; }
        private static OpenTK.Mathematics.Vector3 TkVec(System.Numerics.Vector3 v) => new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z);

        public void EnsureGlResources()
        {
            if (_glInit) return;
            InitProgram();
            InitOutputBuffers();
            InitInputArrayTexture();
            _glInit = true;
        }

        private void InitProgram()
        {
            _vs = GL.CreateShader(ShaderType.VertexShader);
            _fs = GL.CreateShader(ShaderType.FragmentShader);

// VS: screen-space UVs from clip coords (no bounds)
            var vsSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMvp;
out vec2 vLayoutUV;
void main(){
    vec4 clip = uMvp * vec4(aPos, 1.0);
    gl_Position = clip;
    // NDC [-1,1] -> UV [0,1]
    float iw = 1.0 / max(clip.w, 1e-6);
    vLayoutUV = clip.xy * iw * 0.5 + 0.5;
}";

            var fsSrc = @"#version 330 core
in vec2 vLayoutUV;
uniform sampler2D solarCells;
layout(location=0) out vec4 oCells;
void main(){
    oCells = texture(solarCells, vLayoutUV);
}";

            GL.ShaderSource(_vs, vsSrc);
            GL.CompileShader(_vs);
            var vsLog = ShaderLog(_vs);
            if (!string.IsNullOrWhiteSpace(vsLog))
                throw new InvalidOperationException("Vertex shader compile failed:\n" + vsLog);

            GL.ShaderSource(_fs, fsSrc);
            GL.CompileShader(_fs);
            var fsLog = ShaderLog(_fs);
            if (!string.IsNullOrWhiteSpace(fsLog))
                throw new InvalidOperationException("Fragment shader compile failed:\n" + fsLog);

            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, _vs);
            GL.AttachShader(_prog, _fs);
            GL.LinkProgram(_prog);
            var linkLog = ProgramLog(_prog);
            if (!string.IsNullOrWhiteSpace(linkLog))
                throw new InvalidOperationException("Program link failed:\n" + linkLog);
            
            _uMvp        = GL.GetUniformLocation(_prog, "uMvp");
            _uSolarCells = GL.GetUniformLocation(_prog, "solarCells");
        }

        private int _rbColor; // add this field next to _fbo/_texCells

        private void InitOutputBuffers()
        {
            _w = _h = COMPUTE_TEX_SIZE;

            // Cleanup if reinit
            if (_fbo != 0) GL.DeleteFramebuffer(_fbo);
            if (_texCells != 0) GL.DeleteTexture(_texCells);

            // Color texture
            _texCells = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texCells);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // FBO
            _fbo = GL.GenFramebuffer();

            // IMPORTANT: bind as FRAMEBUFFER (affects draw+read), then attach and set both draw & read buffers
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texCells, 0);

            // Select only COLOR_ATTACHMENT0 for both DRAW and READ
            GL.DrawBuffers(1, new[] { DrawBuffersEnum.ColorAttachment0 });
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            // Verify attachment on the SAME target you bound
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                FramebufferParameterName.FramebufferAttachmentObjectType, out int objType);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                FramebufferParameterName.FramebufferAttachmentObjectName, out int objName);
            Debug.WriteLine($"FBO attach0 type={(FramebufferAttachmentObjectType)objType} name={objName} (expect Texture, nonzero)");

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            Debug.WriteLine($"FBO status after build: {status}");
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException($"FBO incomplete at build: {status}");

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            
            _mrtEnabled = false; // CA0 only in the current debug phase
        }

        private static void CheckShader(int sh, string stage)
        {
            GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{stage} compile failed:\n{GL.GetShaderInfoLog(sh)}");
        }

        private static void CheckProgram(int prog)
        {
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"Program link failed:\n{GL.GetProgramInfoLog(prog)}");
        }

        // Call this ONLY when the texture is already bound to `target`
        private static void SetTexParamsBound(TextureTarget target = TextureTarget.Texture2D)
        {
            GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureWrapS,     (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(target, TextureParameterName.TextureWrapT,     (int)TextureWrapMode.ClampToEdge);
        }

        // Backward-compatible shim: bind -> set -> restore previous binding
        private static void SetTexParams(int tex, TextureTarget target = TextureTarget.Texture2D)
        {
            // Save current binding for this target
            int prev = 0;
            switch (target)
            {
                case TextureTarget.Texture2D:
                    GL.GetInteger(GetPName.TextureBinding2D, out prev);
                    break;
                // add other targets here if you use them
                default:
                    GL.GetInteger(GetPName.TextureBinding2D, out prev);
                    break;
            }

            GL.BindTexture(target, tex);
            SetTexParamsBound(target);
            GL.BindTexture(target, prev);
        }


        private void InitInputArrayTexture()
        {
            _texArray = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);
            SetTexParams(_texArray);
        }

        public ArraySimulationStepOutput Simulate(ArraySimulationStepInput simInput)
        {
            if (simInput == null) throw new InvalidOperationException("No input specified.");
            var sunDir = GetSunDir(simInput);
            return Simulate(simInput.Array!, sunDir, simInput.Irradiance, simInput.IndirectIrradiance, simInput.Temperature);
        }

        public ArraySimulationStepOutput Simulate(ArraySpec array, System.Numerics.Vector3 sunDir, double wPerM2Insolation, double wPerM2Indirect, double cTemp)
        {
            if (array is null) throw new ArgumentException("No array specified.");
            if (array.Mesh is null) throw new ArgumentException("No array shape (mesh) loaded.");
            if (array.LayoutTexture is null) throw new ArgumentException("No array layout (texture) loaded.");
            if (wPerM2Insolation < 0) throw new ArgumentException("Invalid insolation.");
            if (Math.Abs(sunDir.Length() - 1.0f) > 1e-3) throw new ArgumentException("Sun dir must be unit length.");
            
            EnsureGlResources();

            var t1 = DateTime.Now;
            SetUniforms(array, wPerM2Insolation);
            ComputeRender(array, sunDir);
            var output = AnalyzeComputeTex(array, wPerM2Insolation, wPerM2Indirect, cTemp);
            var t2 = DateTime.Now;
            Debug.WriteLine($"finished sim step! {(t2 - t1).TotalSeconds:0.000}s {output.WattsInsolation:0.0}/{output.WattsOutput:0.0}W");
            return output;
        }
        
        private (int vao, int vbo) _clipTri;
        private void DrawClipspaceTriangle()
        {
            if (_clipTri.vao == 0)
            {
                // 3 vertices in clip/NDC space (x,y,z), normals unused
                float[] verts = {
                    // pos only; VS will ignore normal
                    -0.5f, -0.5f, 0.0f,
                    0.5f, -0.5f, 0.0f,
                    0.0f,  0.5f, 0.0f
                };
                int vao = GL.GenVertexArray();
                int vbo = GL.GenBuffer();
                GL.BindVertexArray(vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

                GL.EnableVertexAttribArray(0); // position @ location 0
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
                // we won’t supply attrib 1 (normal) for this test; VS will synthesize one

                _clipTri = (vao, vbo);
            }

            GL.BindVertexArray(_clipTri.vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
        }
        
        public void ComputeRender(ArraySpec array, System.Numerics.Vector3 sunDir)
        {
            // Bind offscreen FBO (single CA0)
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.DrawBuffers(1, new[] { DrawBuffersEnum.ColorAttachment0 });
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            // Sanity
            var fboStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            Debug.WriteLine($"FBO status at draw: {fboStatus}");
            if (fboStatus != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException($"FBO incomplete at draw: {fboStatus}");

            GL.Viewport(0, 0, _w, _h);
            GL.ColorMask(true, true, true, true);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            // Program
            GL.UseProgram(_prog);

            // ---- A) Draw a clip-space triangle (guard) with identity MVP ----
            var I = Matrix4.Identity;
            if (_uMvp >= 0) GL.UniformMatrix4(_uMvp, false, ref I);

            // Bind layout texture to unit 0 (do this once before both draws)
            if (_uSolarCells >= 0)
            {
                if (!ReferenceEquals(_cacheSolarCells, array.LayoutTexture))
                {
                    _cacheSolarCells = array.LayoutTexture;
                    UploadLayoutTexture(_cacheSolarCells!);
                }
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _texArray);
                GL.Uniform1(_uSolarCells, 0);
            }

            DrawClipspaceTriangle();

            // ---- B) Draw your mesh with your real MVP (still screen-space UV) ----
            var center = ComputeArrayCenter(array);
            double maxDim = ComputeArrayMaxDimension(array);
            var eye  = center + sunDir * 50f;
            var view = Matrix4.LookAt(TkVec(eye), TkVec(center), new OpenTK.Mathematics.Vector3(0, 1, 0));
            float half = (float)(maxDim * 0.5f);
            var proj = Matrix4.CreateOrthographic(2 * half, 2 * half, 0.1f, 200f);
            Matrix4 model = Matrix4.Identity;
            Matrix4 mvp = proj * view * model;
            if (_uMvp >= 0) GL.UniformMatrix4(_uMvp, false, ref mvp);

            // Mesh draw
            using var sprite = new MeshSprite(array.Mesh);
            GL.BindVertexArray(sprite.Vao);
            GL.DrawElements(PrimitiveType.Triangles, sprite.IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
            GL.BindVertexArray(0);

            GL.Finish();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

        private static Matrix4 BuildSunPOVMvp(System.Numerics.Vector3 sunDir, System.Numerics.Vector3 modelCenter, double modelMaxDim)
        {
            var eye = modelCenter + sunDir * 50f;
            var view = Matrix4.LookAt(TkVec(eye), TkVec(modelCenter), new OpenTK.Mathematics.Vector3(0, 1, 0));
            float half = (float)(modelMaxDim * 0.5);
            var proj = Matrix4.CreateOrthographic(2 * half, 2 * half, 0.1f, 200f);
            return view * proj;
        }

        private void SetUniforms(ArraySpec array, double insolation)
        {
            // Only set if our program is already current
            GL.GetInteger(GetPName.CurrentProgram, out int curProg);
            if (curProg != _prog) return;

            // (These bounds are unused in this step; safe to leave as-is)
            if (_uX0 >= 0) GL.Uniform1(_uX0, (float)array.LayoutBounds.MinX);
            if (_uX1 >= 0) GL.Uniform1(_uX1, (float)array.LayoutBounds.MaxX);
            if (_uZ0 >= 0) GL.Uniform1(_uZ0, (float)array.LayoutBounds.MinZ);
            if (_uZ1 >= 0) GL.Uniform1(_uZ1, (float)array.LayoutBounds.MaxZ);

            // Only if present
            if (_uSolarCells >= 0)
            {
                if (!ReferenceEquals(_cacheSolarCells, array.LayoutTexture))
                {
                    _cacheSolarCells = array.LayoutTexture;
                    UploadLayoutTexture(_cacheSolarCells!);
                }
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _texArray);
                GL.Uniform1(_uSolarCells, 0);
            }

            if (_uPixelWattsIn >= 0 || _uPixelArea >= 0)
            {
                double arrayDimM = ComputeArrayMaxDimension(array);
                double m2PerPixel = arrayDimM * arrayDimM / (double)(COMPUTE_TEX_SIZE * COMPUTE_TEX_SIZE);
                double wattsPerPixel = m2PerPixel * insolation;

                if (_uPixelWattsIn >= 0) GL.Uniform1(_uPixelWattsIn, (float)wattsPerPixel);
                if (_uPixelArea    >= 0) GL.Uniform1(_uPixelArea,    (float)m2PerPixel);
            }
        }
        
        private static double ComputeArrayMaxDimension(ArraySpec array)
        {
            Quad3 bb = array.Mesh.BoundingBox;
            return (bb.Max - bb.Min).Length();
        }

        private static System.Numerics.Vector3 ComputeArrayCenter(ArraySpec array)
        {
            Quad3 bb = array.Mesh.BoundingBox;
            return System.Numerics.Vector3.Multiply((bb.Max + bb.Min), 0.5f);
        }

        public void UploadLayoutTexture(Image<Rgba32> img)
        {
            // Ensure we have a single, tightly-packed RGBA buffer
            var raw = new byte[img.Width * img.Height * 4];
            img.CopyPixelDataTo(raw); // ImageSharp guarantees RGBA order for Rgba32
            
            // Sanity: count non-black pixels in the CPU buffer
            int nz = 0;
            for (int i = 0; i < raw.Length; i += 4)
            {
                byte r = raw[i+0], g = raw[i+1], b = raw[i+2]; // RGBA order
                if (r != 0 || g != 0 || b != 0) { nz++; break; } // early exit; even 1 is enough
            }
            
            Debug.WriteLine($"layout CPU buffer hasNonBlack={(nz > 0)} size={img.Width}x{img.Height}");
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            // Allocate storage with a sized internal format
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            // Upload with alignment 1 so no row padding is assumed
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, img.Width, img.Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, raw);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4); // restore default

            // No mipmaps expected (NEAREST), clamp & filters
            SetTexParamsBound(TextureTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        }

        private float[] ReadFloatTexture(FramebufferAttachment attachment, double scale)
        {
            // Bind READ framebuffer (explicit) and select the requested color attachment
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);

            // Is the requested attachment actually present?
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.ReadFramebuffer,
                attachment,
                FramebufferParameterName.FramebufferAttachmentObjectName, out int objName);

            if (objName == 0)
            {
                // Nothing attached here -> return zeros (safe default during single-CA debug)
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                return new float[_w * _h];
            }

            // Select that attachment for reads
            var rbm = (ReadBufferMode)attachment; // ColorAttachmentN -> same enum value layout
            GL.ReadBuffer(rbm);

            // Read RGBA8 bytes and decode
            var buf = ArrayPool<byte>.Shared.Rent(_w * _h * 4);
            try
            {
                GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
                GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, buf);
                GL.PixelStore(PixelStoreParameter.PackAlignment, 4);

                float[] decoded = new float[_w * _h];
                for (int i = 0; i < decoded.Length; i++)
                {
                    byte r = buf[i * 4 + 0];
                    byte g = buf[i * 4 + 1];
                    byte b = buf[i * 4 + 2];
                    // byte a = buf[i * 4 + 3];  // alpha is not guaranteed to be 255 on all drivers

                    // Don’t assert strict channel values; just decode what we used to encode.
                    // Our encode packs: value = (r/2) + (g/255) when r stores even steps.
                    // If b != 0 or r is odd, we’ll still produce a stable best-effort decode.
                    decoded[i] = (float)(scale * ((r / 2.0) + (g / 255.0)));
                }
                return decoded;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            }
        }
        
        private static Matrix4 BuildSunPOVMvp(System.Numerics.Vector3 sunDir,
            System.Numerics.Vector3 modelCenter,
            double modelMaxDim,
            Matrix4 model)
        {
            var eye  = modelCenter + sunDir * 50f;
            var view = Matrix4.LookAt(TkVec(eye), TkVec(modelCenter), new OpenTK.Mathematics.Vector3(0, 1, 0));
            float half = (float)(modelMaxDim * 0.5);
            var proj = Matrix4.CreateOrthographic(2 * half, 2 * half, 0.1f, 200f);

            // GLSL multiplies column-major with vectors on the right ⇒ proj * view * model
            return proj * view * model;
        }
        
        [Conditional("DEBUG")]
        private static void CheckGLErr(string where)
        {
            var err = GL.GetError();
            if (err != ErrorCode.NoError) Debug.WriteLine($"{where}: GL ERROR = {err}");
        }
        
        // Preview: blit CA0 from our offscreen FBO to a target framebuffer (e.g., the control's 'fb')
        public void BlitCellsTo(int drawFb, int dstW, int dstH)
        {
            // Read from our offscreen FBO's COLOR_ATTACHMENT0
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            // Draw to the provided framebuffer (Avalonia passes it into OnOpenGlRender)
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);

            // Be conservative: no scissor
            GL.Disable(EnableCap.ScissorTest);

            // Blit color (nearest so it's crisp)
            GL.BlitFramebuffer(
                0, 0, _w, _h,
                0, 0, dstW, dstH,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest
            );

            // Cleanup
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        }

        private Rgba32[] ReadColorTexture(FramebufferAttachment attachment)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            var raw = new byte[_w * _h * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, raw);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);

            var colors = new Rgba32[_w * _h];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = new Rgba32(raw[i*4+0], raw[i*4+1], raw[i*4+2], raw[i*4+3]);
            return colors;
        }
        
        static bool IsBackground(Rgba32 c) => c.R == 0 && c.G == 0 && c.B == 0;
        
        private ArraySimulationStepOutput AnalyzeComputeTex(ArraySpec array, double wPerM2Insolation, double wPerM2Indirect, double cTemp)
        {
            var texColors = ReadColorTexture(FramebufferAttachment.ColorAttachment0);

            float[] texWattsIn, texArea;
            if (_mrtEnabled)
            {
                texWattsIn = ReadFloatTexture(FramebufferAttachment.ColorAttachment1, 0.0001);
                double arrayDimM = ComputeArrayMaxDimension(array);
                double m2PerPixel = arrayDimM * arrayDimM / (double)(COMPUTE_TEX_SIZE * COMPUTE_TEX_SIZE);
                texArea = ReadFloatTexture(FramebufferAttachment.ColorAttachment2, m2PerPixel / 4.0);
            }
            else
            {
                // No CA1/CA2 yet -> zeros (expected for single-attachment debug)
                texWattsIn = new float[_w * _h];
                texArea    = new float[_w * _h];
            }
            
            int ncells = 0;
            var cells = new List<ArraySpec.Cell>();
            var colorToId = new Dictionary<Rgba32, int>();
            foreach (var cellStr in array.Strings)
            {
                foreach (var cell in cellStr.Cells)
                {
                    cells.Add(cell);
                    colorToId[cell.Color] = ncells++;
                }
            }

            double[] wattsIn = new double[ncells];
            double[] areas = new double[ncells];
            double wattsInUnlinked = 0, areaUnlinked = 0;

            for (int i = 0; i < _w * _h; i++)
            {
                var color = texColors[i];
                
                if (IsBackground(color)) continue;  // ONLY skip background now

                if (colorToId.TryGetValue(color, out int id))
                {
                    wattsIn[id] += texWattsIn[i];
                    areas[id] += texArea[i];
                }
                else
                {
                    wattsInUnlinked += texWattsIn[i];
                    areaUnlinked += texArea[i];
                }
            }
            if (areaUnlinked > 0 || wattsInUnlinked > 0)
                Logger.warn("Found texels not linked to any cell. Area={0}m^2, Watts={1}W", areaUnlinked, wattsInUnlinked);

            for (int i = 0; i < ncells; i++)
            {
                wattsIn[i] += array.CellSpec.Area * wPerM2Indirect;
                wattsIn[i] *= (1.0 - array.EncapsulationLoss);
            }

            double totalArea = 0, totalWattsIn = 0;
            for (int i = 0; i < ncells; i++) { totalWattsIn += wattsIn[i]; totalArea += areas[i]; }

            var cellSpec = array.CellSpec;
            int nstrings = array.Strings.Count;
            double totalWattsOutByCell = 0, totalWattsOutByString = 0;
            var strings = new ArraySimStringOutput[nstrings];

            int cellIx = 0;
            for (int s = 0; s < nstrings; s++)
            {
                var cellStr = array.Strings[s];
                double stringWattsIn = 0, stringWattsOutByCell = 0, stringLitArea = 0;
                var cellSweeps = new IVTrace[cellStr.Cells.Count];

                for (int j = 0; j < cellStr.Cells.Count; j++)
                {
                    double cellWattsIn = wattsIn[cellIx];
                    double cellLitArea = areas[cellIx];     // <-- use the matching cell index
                    cellIx++;                                // advance after using it

                    double cellInsolation = cellWattsIn / cellSpec.Area;
                    var cellSweep = CellSimulator.CalcSweep(cellSpec, cellInsolation, cTemp);

                    cellSweeps[j] = cellSweep;
                    stringWattsIn += cellWattsIn;
                    stringWattsOutByCell += cellSweep.Pmp;
                    totalWattsOutByCell += cellSweep.Pmp;
                    stringLitArea += cellLitArea;           // <-- accumulate correctly
                }

                strings[s] = new ArraySimStringOutput
                {
                    WattsIn = stringWattsIn,
                    WattsOutputByCell = stringWattsOutByCell,
                    IVTrace = StringSimulator.CalcStringIV(cellStr, cellSweeps, array.BypassDiodeSpec),
                    String = cellStr,
                    Area = cellStr.Cells.Count * cellSpec.Area,
                    AreaShaded = 0, // set below
                    WattsOutputIdeal = CellSimulator.CalcSweep(cellSpec, wPerM2Insolation, cTemp).Pmp * cellStr.Cells.Count,
                };
                strings[s].WattsOutput = strings[s].IVTrace.Pmp;
                strings[s].AreaShaded = Math.Max(0.0, strings[s].Area - stringLitArea);
            }

            return new ArraySimulationStepOutput
            {
                ArrayArea = ncells * cellSpec.Area,
                ArrayLitArea = totalArea,
                WattsInsolation = totalWattsIn,
                WattsOutputByCell = totalWattsOutByCell,
                WattsOutput = totalWattsOutByString,
                Strings = strings,
            };
        }

        public static System.Numerics.Vector3 GetSunDir(ArraySimulationStepInput simInput)
        {
            var utc = simInput.Utc;
            var sidereal = Astro.sidereal_time(utc, simInput.Longitude);
            var solarAzimuth = Astro.solar_azimuth((int)sidereal.TimeOfDay.TotalSeconds, sidereal.DayOfYear, simInput.Latitude);
            var solarElevation = Astro.solar_elevation((int)sidereal.TimeOfDay.TotalSeconds, sidereal.DayOfYear, simInput.Latitude);
            var phi = solarAzimuth - simInput.Heading;
            var x = Math.Cos(solarElevation) * Math.Cos(phi);
            var y = Math.Cos(solarElevation) * Math.Sin(phi);
            var z = Math.Sin(solarElevation);
            z = Math.Cos(simInput.Tilt) * z + Math.Sin(simInput.Tilt) * y;
            y = Math.Cos(simInput.Tilt) * y - Math.Sin(simInput.Tilt) * z;
            return new System.Numerics.Vector3((float)x, (float)z, (float)y);
        }
    }

    public sealed class ArraySimComputeSurface : OpenGlControlBase
    {
        private readonly ArraySimulator _sim = new();
        private bool _glLoaded;

        // one job at a time
        // private ArraySimulationStepInput? _pending;
        // private TaskCompletionSource<ArraySimulationStepOutput>? _tcs;

        // Adapter: let OpenTK resolve procs from Avalonia’s GlInterface
        private sealed class OpenTKBindingsContext : OpenTK.IBindingsContext
        {
            private readonly GlInterface _gl;
            public OpenTKBindingsContext(GlInterface gl) => _gl = gl;
            public IntPtr GetProcAddress(string procName) => _gl.GetProcAddress(procName);
        }
        
        public Task<ArraySimulationStepOutput> RunOnceAsync(ArraySimulationStepInput input)
        {
            if (_tcs != null) throw new InvalidOperationException("A simulation is already running.");
            _pending = input;
            _explicit = null;
            _tcs = new TaskCompletionSource<ArraySimulationStepOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(InvalidateVisual);
            return _tcs.Task;
        }

        public Task<ArraySimulationStepOutput> RunOnceExplicitAsync(
            ArraySpec array, System.Numerics.Vector3 sunDir, double irr, double indirect, double temp)
        {
            if (_tcs != null) throw new InvalidOperationException("A simulation is already running.");
            _pending = null;
            _explicit = (array, sunDir, irr, indirect, temp);
            _tcs = new TaskCompletionSource<ArraySimulationStepOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(InvalidateVisual);
            return _tcs.Task;
        }

// add these fields
        private ArraySimulationStepInput? _pending;
        private (ArraySpec arr, System.Numerics.Vector3 dir, double irr, double indir, double temp)? _explicit;
        private TaskCompletionSource<ArraySimulationStepOutput>? _tcs;


        protected override void OnOpenGlInit(GlInterface gl)
        {
            if (_glLoaded) return;
            // Wire OpenTK’s GL loader to Avalonia’s context
            OpenTK.Graphics.OpenGL.GL.LoadBindings(new OpenTKBindingsContext(gl));
            
            string glVer = GL.GetString(StringName.Version);
            string glslVer = GL.GetString(StringName.ShadingLanguageVersion);
            GL.GetInteger(GetPName.MaxColorAttachments, out int maxCA);
            GL.GetInteger(GetPName.MaxDrawBuffers, out int maxDB);
            Debug.WriteLine($"GL={glVer} GLSL={glslVer} MaxColorAttachments={maxCA} MaxDrawBuffers={maxDB}");

            // Basic state you want once
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Create GL resources owned by the simulator while context is current
            _sim.EnsureGlResources();

            _glLoaded = true;
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            // If you add explicit GL deletion in ArraySimulator, you can call it here
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                if (_pending != null)
                {
                    var out1 = _sim.Simulate(_pending);
                    _tcs?.TrySetResult(out1);
                }
                else if (_explicit != null)
                {
                    var (arr, dir, irr, indir, temp) = _explicit.Value;
                    var out2 = _sim.Simulate(arr, dir, irr, indir, temp);
                    _tcs?.TrySetResult(out2);
                }
                else
                {
                    // nothing queued; clear default FB for Avalonia’s sake
                    
                    // GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    // GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
                    //
                    // GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                    //     FramebufferAttachment.ColorAttachment0,
                    //     FramebufferParameterName.FramebufferAttachmentObjectType, out int t0);
                    // GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                    //     FramebufferAttachment.ColorAttachment0,
                    //     FramebufferParameterName.FramebufferAttachmentObjectName, out int n0);
                    // Debug.WriteLine($"At draw: CA0 type={(FramebufferAttachmentObjectType)t0} name={n0}");
                    //
                    // GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                }
            }
            catch (Exception ex)
            {
                _tcs?.TrySetException(ex);
            }
            finally
            {
                _pending = null;
                _explicit = null;
                _tcs = null;
                
                if (_sim.Preview)
                {
                    var scale = (VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1.0;
                    int w = Math.Max(1, (int)Math.Round(Bounds.Width  * scale));
                    int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

// (optional, helps some drivers)
                    OpenTK.Graphics.OpenGL.GL.BindFramebuffer(OpenTK.Graphics.OpenGL.FramebufferTarget.DrawFramebuffer, fb);
                    OpenTK.Graphics.OpenGL.GL.Viewport(0, 0, w, h);
                    OpenTK.Graphics.OpenGL.GL.BindFramebuffer(OpenTK.Graphics.OpenGL.FramebufferTarget.DrawFramebuffer, 0);

                    _sim.BlitCellsTo(fb, w, h);                }
                else
                {
                    // leave the default FB in a clean state
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    
                }
            }
        }
    }
}
