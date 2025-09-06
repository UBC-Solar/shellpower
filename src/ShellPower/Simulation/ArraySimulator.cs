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

        // Input texture (layout)
        private int _texArray;
        private Image<Rgba32>? _cacheSolarCells;

        // Output MRTs
        private int _texCells, _texWatts, _texArea, _texDepth, _fbo;
        private int _w = COMPUTE_TEX_SIZE, _h = COMPUTE_TEX_SIZE;

        private bool _glInit;

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

            var vsSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uMvp;
uniform float x0, x1, z0, z1;
out float vCosRule;
out float vAreaMult;
out vec2 vLayoutUV;
void main(){
    gl_Position = uMvp * vec4(aPos,1.0);
    vec3 n = normalize(aNormal);
    vCosRule = max(n.z, 0.0);
    float lenN = length(n);
    vAreaMult = clamp(lenN / max(n.z, 1e-6), 0.0, 24.0);

    float dx = max(x1 - x0, 1e-6);
    float dz = max(z1 - z0, 1e-6);
    vec2 uv = vec2((aPos.x - x0) / dx, (aPos.z - z0) / dz);
    vLayoutUV = clamp(uv, 0.0, 1.0);

}";

            var fsSrc = @"#version 330 core
in float vCosRule;
in float vAreaMult;
in vec2 vLayoutUV;
uniform float pixelWattsIn;
uniform float pixelArea;
uniform sampler2D solarCells;
layout(location=0) out vec4 oCells;
layout(location=1) out vec4 oWatts;
layout(location=2) out vec4 oArea;
vec4 encodeFloat(float val){
    float mwRed = floor(val) * 2.0 / 255.0;
    float mwGreen = val - floor(val);
    return vec4(mwRed, mwGreen, 0.0, 1.0);
}
void main(){
    vec4 solarCell = texture(solarCells, vLayoutUV);
    float watts10k = pixelWattsIn * vCosRule * 10000.0;
    oCells = vec4(solarCell.rgb, 1.0);
    oWatts = encodeFloat(watts10k);
    oArea  = encodeFloat(vAreaMult * 4.0);

    //oCells = texture(solarCells, vec2(0.5, 0.5));
    //oWatts = vec4(0,0,0,1);
    //oArea  = vec4(0,0,0,1);
    oCells = texture(solarCells, vec2(0.5, 0.5));
    oWatts = vec4(0,0,0,1);
    oArea  = vec4(0,0,0,1);
}";

            GL.ShaderSource(_vs, vsSrc);
            GL.CompileShader(_vs);
            var vsLog = ShaderLog(_vs);
            if (!string.IsNullOrWhiteSpace(vsLog)) throw new InvalidOperationException("Vertex shader compile failed:\n" + vsLog);

            GL.ShaderSource(_fs, fsSrc);
            GL.CompileShader(_fs);
            var fsLog = ShaderLog(_fs);
            if (!string.IsNullOrWhiteSpace(fsLog)) throw new InvalidOperationException("Fragment shader compile failed:\n" + fsLog);

            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, _vs);
            GL.AttachShader(_prog, _fs);
            GL.LinkProgram(_prog);
            var linkLog = ProgramLog(_prog);
            if (!string.IsNullOrWhiteSpace(linkLog)) throw new InvalidOperationException("Program link failed:\n" + linkLog);

            _uMvp = GL.GetUniformLocation(_prog, "uMvp");
            _uX0 = GL.GetUniformLocation(_prog, "x0");
            _uX1 = GL.GetUniformLocation(_prog, "x1");
            _uZ0 = GL.GetUniformLocation(_prog, "z0");
            _uZ1 = GL.GetUniformLocation(_prog, "z1");
            _uPixelWattsIn = GL.GetUniformLocation(_prog, "pixelWattsIn");
            _uPixelArea = GL.GetUniformLocation(_prog, "pixelArea");
            _uSolarCells = GL.GetUniformLocation(_prog, "solarCells");
        }

        private void InitOutputBuffers()
        {
            _w = _h = COMPUTE_TEX_SIZE;

            // --- Cells texture (RGBA8) ---
            _texCells = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texCells);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texCells);

            // --- Watts texture (RGBA8; matches 8-bit channel encode) ---
            _texWatts = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texWatts);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texWatts);

            // --- Area texture (RGBA8) ---
            _texArea = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texArea);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texArea);
            // --- Depth texture (DEPTH24) ---
            _texDepth = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texDepth);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
                          _w, _h, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // --- Framebuffer setup ---
            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texCells, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _texWatts, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, _texArea, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _texDepth, 0);

            // Specify we will draw to all three color attachments
            DrawBuffersEnum[] bufs = {
                DrawBuffersEnum.ColorAttachment0,
                DrawBuffersEnum.ColorAttachment1,
                DrawBuffersEnum.ColorAttachment2
            };
            GL.DrawBuffers(bufs.Length, bufs);

            // Check status
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException($"FBO incomplete: {status}");

            // Unbind to avoid accidental rendering
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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
        
public void ComputeRender(ArraySpec array, System.Numerics.Vector3 sunDir)
{
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    var fboStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
    if (fboStatus != FramebufferErrorCode.FramebufferComplete)
        throw new InvalidOperationException($"FBO incomplete: {fboStatus}");

    GL.Viewport(0, 0, _w, _h);
    GL.DrawBuffers(3, new[]
    {
        DrawBuffersEnum.ColorAttachment0,
        DrawBuffersEnum.ColorAttachment1,
        DrawBuffersEnum.ColorAttachment2
    });

    GL.ClearColor(0f, 0f, 0f, 1f);
    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    // Disable rejects for now
    GL.Disable(EnableCap.CullFace);
    GL.Disable(EnableCap.DepthTest);

    // ---- BIND PROGRAM FIRST ----
    GL.UseProgram(_prog);

    // Now it’s safe to query/assert the current program
    GL.GetInteger(GetPName.CurrentProgram, out int curProg);
    Debug.Assert(curProg == _prog, $"Wrong program bound: {curProg} vs expected {_prog}");
    Debug.Assert(_uSolarCells >= 0, "uSolarCells location is -1");

    // Build MVP = proj * view * model
    var center = ComputeArrayCenter(array);
    double maxDim = ComputeArrayMaxDimension(array);
    var eye  = center + sunDir * 50f;
    var view = Matrix4.LookAt(TkVec(eye), TkVec(center), new OpenTK.Mathematics.Vector3(0, 1, 0));
    float half = (float)(maxDim * 0.5);
    var proj = Matrix4.CreateOrthographic(2 * half, 2 * half, 0.1f, 200f);
    Matrix4 model = Matrix4.Identity;
    Matrix4 mvp = proj * view * model;
    GL.UniformMatrix4(_uMvp, false, ref mvp);

    // Bind layout texture to unit 0 and set the sampler to 0 (after UseProgram!)
    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture2D, _texArray);
    GL.Uniform1(_uSolarCells, 0);

    // Optional: log sampler value (safe now the program is bound)
    // int[] samplerVal = new int[1];
    // GL.GetUniform(_prog, _uSolarCells, samplerVal);
    // Debug.WriteLine($"uSolarCells={samplerVal[0]} (expect 0)");

    // Draw with VAO-only path (no program changes inside)
    using var sprite = new MeshSprite(array.Mesh);
    GL.BindVertexArray(sprite.Vao);

    // Quick geometry sanity
    GL.GetInteger(GetPName.ElementArrayBufferBinding, out int ebo);
    Debug.Assert(ebo != 0, "No EBO bound in VAO");
    Debug.Assert(sprite.IndexCount > 0, "IndexCount == 0");

    GL.GetInteger(GetPName.CurrentProgram, out curProg);
    GL.GetInteger(GetPName.FramebufferBinding, out int curFbo);
    GL.GetInteger(GetPName.DrawFramebufferBinding, out int curDrawFbo);
    GL.GetInteger(GetPName.ReadFramebufferBinding, out int curReadFbo);
    GL.GetInteger(GetPName.ActiveTexture, out int activeTex); // should be Texture0 + 0
    int[] samplerVal = new int[1];
    GL.GetUniform(_prog, _uSolarCells, samplerVal);
    Debug.WriteLine($"prog={curProg} fbo={curFbo}/{curDrawFbo}/{curReadFbo} activeTex={activeTex} sampler={samplerVal[0]}");
    
    GL.DrawElements(PrimitiveType.Triangles, sprite.IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
    GL.BindVertexArray(0);

    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
}
        // public void ComputeRender(ArraySpec array, System.Numerics.Vector3 sunDir)
        // {
        //     // bind your MRT FBO & state
        //     GL.UseProgram(_prog);
        //     GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        //     GL.DrawBuffers(3, new[] {
        //         DrawBuffersEnum.ColorAttachment0,
        //         DrawBuffersEnum.ColorAttachment1,
        //         DrawBuffersEnum.ColorAttachment2
        //     });
        //     GL.Viewport(0, 0, _w, _h);
        //     GL.ClearColor(0f, 0f, 0f, 1f);
        //     GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        //
        //     // build and upload MVP
        //     var center = ComputeArrayCenter(array);
        //     double maxDim = ComputeArrayMaxDimension(array);
        //     Matrix4 mvp = BuildSunPOVMvp(sunDir, center, maxDim, Matrix4.Identity);
        //     GL.UniformMatrix4(_uMvp, false, ref mvp);
        //
        //     // textures/samplers
        //     GL.ActiveTexture(TextureUnit.Texture0);
        //     GL.BindTexture(TextureTarget.Texture2D, _texArray);
        //     GL.Uniform1(_uSolarCells, 0);
        //
        //     // draw with YOUR program (bind VAO directly)
        //     var sprite = new MeshSprite(array.Mesh);   // assumes this builds/owns a VAO
        //     GL.BindVertexArray(sprite.Vao);            // attrib 0 = pos, 1 = normal
        //     GL.DrawElements(PrimitiveType.Triangles, sprite.IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
        //     GL.BindVertexArray(0);
        //
        //     GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        // }
        //
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
            GL.UseProgram(_prog);
            GL.Uniform1(_uX0, (float)array.LayoutBounds.MinX);
            GL.Uniform1(_uX1, (float)array.LayoutBounds.MaxX);
            GL.Uniform1(_uZ0, (float)array.LayoutBounds.MinZ);
            GL.Uniform1(_uZ1, (float)array.LayoutBounds.MaxZ);
            
            Debug.WriteLine($"layout bounds x0={array.LayoutBounds.MinX}, x1={array.LayoutBounds.MaxX}, z0={array.LayoutBounds.MinZ}, z1={array.LayoutBounds.MaxZ}");
            
            if (!ReferenceEquals(_cacheSolarCells, array.LayoutTexture))
            {
                _cacheSolarCells = array.LayoutTexture;
                UploadLayoutTexture(_cacheSolarCells!);
            }
            GL.Uniform1(_uSolarCells, 0);

            double arrayDimM = ComputeArrayMaxDimension(array);
            double m2PerPixel = arrayDimM * arrayDimM / (double)(COMPUTE_TEX_SIZE * COMPUTE_TEX_SIZE);
            double wattsPerPixel = m2PerPixel * insolation;
            GL.Uniform1(_uPixelWattsIn, (float)wattsPerPixel);
            GL.Uniform1(_uPixelArea, (float)m2PerPixel);
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
            //
            // // Read back a small sample from GPU to confirm non-zero data arrived
            // var back = new byte[Math.Min(64, img.Width) * Math.Min(64, img.Height) * 4];
            // GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, back);
            //
            // bool gpuHasNonBlack = false;
            // for (int i = 0; i < back.Length; i += 4)
            // {
            //     if (back[i+0] != 0 || back[i+1] != 0 || back[i+2] != 0) { gpuHasNonBlack = true; break; }
            // }
            // Debug.WriteLine($"layout GPU sample hasNonBlack={gpuHasNonBlack}");
            //
            // GL.BindTexture(TextureTarget.Texture2D, 0);
            //
            // int tw=0, th=0;
            // GL.BindTexture(TextureTarget.Texture2D, _texArray);
            // GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out tw);
            // GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out th);
            // Debug.WriteLine($"layout tex size = {tw}x{th}");
        }

        private float[] ReadFloatTexture(FramebufferAttachment attachment, double scale)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.ReadBuffer((ReadBufferMode)attachment);

            // Rent managed buffer
            var buf = ArrayPool<byte>.Shared.Rent(_w * _h * 4);
            try
            {
                // Read directly into managed array
                GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
                GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, buf);
                GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                
                float[] decoded = new float[_w * _h];
                for (int i = 0; i < decoded.Length; i++)
                {
                    byte r = buf[i * 4 + 0], g = buf[i * 4 + 1], b = buf[i * 4 + 2], a = buf[i * 4 + 3];
                    if (r == 0 && g == 0 && b == 0) continue;
                    Debug.Assert(a == 255);
                    Debug.Assert(r % 2 == 0 && r < 200);
                    Debug.Assert(b == 0);
                    decoded[i] = (float)(scale * (r / 2.0 + g / 255.0));
                }
                return decoded;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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


        private Rgba32[] ReadColorTexture(FramebufferAttachment attachment)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.ReadBuffer((ReadBufferMode)attachment);

            var raw = new byte[_w * _h * 4];
            // Read directly into managed array
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, raw);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            var colors = new Rgba32[_w * _h];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Rgba32(
                    raw[i * 4 + 0],
                    raw[i * 4 + 1],
                    raw[i * 4 + 2],
                    raw[i * 4 + 3]);
            }
            return colors;
        }
        
        static bool IsBackground(Rgba32 c) => c.R == 0 && c.G == 0 && c.B == 0;
        
        private ArraySimulationStepOutput AnalyzeComputeTex(ArraySpec array, double wPerM2Insolation, double wPerM2Indirect, double cTemp)
        {
            var texColors = ReadColorTexture(FramebufferAttachment.ColorAttachment0);
            var texWattsIn = ReadFloatTexture(FramebufferAttachment.ColorAttachment1, 0.0001);
            double arrayDimM = ComputeArrayMaxDimension(array);
            double m2PerPixel = arrayDimM * arrayDimM / (double)(COMPUTE_TEX_SIZE * COMPUTE_TEX_SIZE);
            var texArea = ReadFloatTexture(FramebufferAttachment.ColorAttachment2, m2PerPixel / 4.0);

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
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
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

                // leave the default FB in a clean state
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            }
        }
    }
}
