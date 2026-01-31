// Avalonia + OpenTK 4.x conversion (v2) — uses OpenTK.Mathematics for GL-bound matrices
// Packages: Avalonia, Avalonia.Desktop, Avalonia.OpenGL, OpenTK (>=4), SixLabors.ImageSharp
// Removes System.Drawing; uses ImageSharp Rgba32. Uses core OpenTK OpenGL bindings.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using OpenTK.Graphics.OpenGL;   // GL API (core, cross-version)
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower
{
    public sealed class ArraySimulator
    {
        private const int COMPUTE_TEX_SIZE = 2048;

        // GL resources
        private int _vs, _fs, _prog;
        private int _uNormalMat;
        private int _uSunDir;
        private int _uMvp, _uX0, _uX1, _uZ0, _uZ1, _uPixelWattsIn, _uPixelArea, _uSolarCells, _uY0, _uY1, _uSolid;
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

// --- Vertex shader: UV from mesh X/Y bounds
    var vsSrc = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;

uniform mat4 uMvp;
uniform mat3 uNormalMat;

// layout UV mapping (object/world XZ bounds)
uniform float x0, x1;
uniform float z0, z1;
uniform vec3 uSunDir;

out vec2 vLayoutUV;
out float vCosRule;
out float vAreaMult;

void main()
{
    // Sun-POV render for shadowing (depth test)
    gl_Position = uMvp * vec4(aPos, 1.0);

    // Normal in view space (or whatever space uNormalMat represents)
    vec3 n = normalize(aNormal);
    vCosRule = dot(n, normalize(uSunDir));
    float nz = max(n.z, 1e-6);
    vAreaMult = clamp(1.0 / nz, 0.0, 24.0);

    // Layout UVs from XZ (independent of sun view)
    float dx = max(x1 - x0, 1e-6);
    float dz = max(z1 - z0, 1e-6);
    vec2 uv = vec2((aPos.x - x0) / dx, (aPos.z - z0) / dz);
    vLayoutUV = clamp(uv, 0.0, 1.0);
}";
    
// --- Fragment shader: sample the layout (or solid for debug)
    var fsSrc = @"
#version 330 core
in vec2 vLayoutUV;
in float vCosRule;
in float vAreaMult;

uniform sampler2D solarCells;
uniform float pixelWattsIn;
uniform float pixelArea;

layout(location=0) out vec4 oCells;
layout(location=1) out vec4 oWatts;
layout(location=2) out vec4 oArea;

// encodes float in [0..100] with 1/256 precision (same scheme as before)
vec4 encodeFloat(float val){
    float mwRed = floor(val) * 2.0 / 255.0;
    float mwGreen = val - floor(val);
    return vec4(mwRed, mwGreen, 0.0, 1.0);
}

void main()
{
    vec4 solarCell = texture(solarCells, vLayoutUV);

    float cosRule = max(vCosRule, 0.0);

    // keep your historical scaling:
    // watts10k is in 0.1 mW units after decode scale=0.0001 on CPU
    float watts10k = pixelWattsIn * cosRule * 10000.0;

    oCells = vec4(solarCell.rgb, 1.0);
    oWatts = encodeFloat(watts10k);
    oArea  = encodeFloat(vAreaMult * 4.0);
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

    _uMvp         = GL.GetUniformLocation(_prog, "uMvp");
    _uNormalMat = GL.GetUniformLocation(_prog, "uNormalMat");    
    _uPixelWattsIn = GL.GetUniformLocation(_prog, "pixelWattsIn");
    _uPixelArea    = GL.GetUniformLocation(_prog, "pixelArea");    
    _uSolarCells = GL.GetUniformLocation(_prog, "solarCells");
    _uX0         = GL.GetUniformLocation(_prog, "x0");
    _uX1         = GL.GetUniformLocation(_prog, "x1");
    _uY0         = GL.GetUniformLocation(_prog, "y0");
    _uY1         = GL.GetUniformLocation(_prog, "y1");
    _uZ0         = GL.GetUniformLocation(_prog, "z0");
    _uZ1         = GL.GetUniformLocation(_prog, "z1");
    _uSolid      = GL.GetUniformLocation(_prog, "uSolid");
    _uSunDir = GL.GetUniformLocation(_prog, "uSunDir");
    
    // (keep these disabled for now)
    // _uPixelWattsIn = -1;
    // _uPixelArea    = -1;
    
}

public void ComputeRender(ArraySpec array, System.Numerics.Vector3 sunDir, double wPerM2Insolation)
{
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    var bufs = new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 };
    GL.DrawBuffers(bufs.Length, bufs);
    GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

    var fboStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
    if (fboStatus != FramebufferErrorCode.FramebufferComplete)
        throw new InvalidOperationException($"FBO incomplete at draw: {fboStatus}");

    GL.Viewport(0, 0, _w, _h);

    GL.Disable(EnableCap.CullFace);
    GL.Enable(EnableCap.DepthTest);
    GL.DepthMask(true);
    GL.DepthFunc(DepthFunction.Lequal);

    GL.ClearColor(0f, 0f, 0f, 1f);
    GL.ClearDepth(1.0);
    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    GL.UseProgram(_prog);
    
    // Layout texture (unit 0)
    if (!ReferenceEquals(_cacheSolarCells, array.LayoutTexture))
    {
        _cacheSolarCells = array.LayoutTexture;
        UploadLayoutTexture(_cacheSolarCells!);
    }
    GL.ActiveTexture(TextureUnit.Texture0);
    GL.BindTexture(TextureTarget.Texture2D, _texArray);
    if (_uSolarCells >= 0) GL.Uniform1(_uSolarCells, 0);

    // Bounds for UV mapping
    var bb = array.Mesh.BoundingBox;
    if (_uX0 >= 0) GL.Uniform1(_uX0, (float)bb.Min.X);
    if (_uX1 >= 0) GL.Uniform1(_uX1, (float)bb.Max.X);
    if (_uZ0 >= 0) GL.Uniform1(_uZ0, (float)bb.Min.Z);
    if (_uZ1 >= 0) GL.Uniform1(_uZ1, (float)bb.Max.Z);
    if (_uSunDir >= 0) GL.Uniform3(_uSunDir, sunDir.X, sunDir.Y, sunDir.Z);
    // pixelWattsIn + pixelArea (same as legacy)
    // NOTE: your SetUniforms currently computes these; you can compute here or keep SetUniforms,
    // but *make sure* the shader actually has pixelWattsIn/pixelArea locations now.
    // If you keep SetUniforms(), ensure _uPixelWattsIn/_uPixelArea are the real locations.
    // (shown here inline for clarity)
    double arrayDimM = ComputeArrayMaxDimension(array);
    double m2PerPixel = arrayDimM * arrayDimM / (double)(COMPUTE_TEX_SIZE * COMPUTE_TEX_SIZE);
    double wattsPerPixel = m2PerPixel * wPerM2Insolation;

    if (_uPixelArea >= 0) GL.Uniform1(_uPixelArea, (float)m2PerPixel);
    if (_uPixelWattsIn >= 0) GL.Uniform1(_uPixelWattsIn, (float)wattsPerPixel);

    // You want this based on direct irradiance (wPerM2Insolation) — pass that in if needed.
    // If you rely on SetUniforms(), remove this block.
    // GL.Uniform1(_uPixelArea, (float)m2PerPixel);
    // GL.Uniform1(_uPixelWattsIn, (float)(m2PerPixel * wPerM2Insolation));

    // MVP + normal matrix
    var mvp = MakeSunMvp(array, sunDir);
    if (_uMvp >= 0)
        GL.UniformMatrix4(_uMvp, false, ref mvp);

    var viewOnly = mvp; // if you split proj/view, use view matrix here; otherwise just build view separately
    var nmat = new OpenTK.Mathematics.Matrix3(viewOnly);
    if (_uNormalMat >= 0)
        GL.UniformMatrix3(_uNormalMat, false, ref nmat);

    using var sprite = new MeshSprite(array.Mesh);
    GL.BindVertexArray(sprite.Vao);
    GL.DrawElements(PrimitiveType.Triangles, sprite.IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
    GL.BindVertexArray(0);

    GL.Disable(EnableCap.DepthTest);
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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

        private int _rbColor; // add this field next to _fbo/_texCells

private void InitOutputBuffers()
{
    _w = _h = COMPUTE_TEX_SIZE;

    // cleanup omitted for brevity...

    _texCells = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, _texCells);
    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0,
                  PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
    SetTexParamsBound();

    _texWatts = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, _texWatts);
    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0,
                  PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
    SetTexParamsBound();

    _texArea = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, _texArea);
    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0,
                  PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
    SetTexParamsBound();

    _texDepth = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, _texDepth);
    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, _w, _h, 0,
                  PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

    _fbo = GL.GenFramebuffer();
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texCells, 0);
    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _texWatts, 0);
    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, _texArea, 0);
    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,  TextureTarget.Texture2D, _texDepth, 0);

    var bufs = new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 };
    GL.DrawBuffers(bufs.Length, bufs);
    GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

    var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
    if (status != FramebufferErrorCode.FramebufferComplete)
        throw new InvalidOperationException($"FBO incomplete: {status}");

    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    _mrtEnabled = true;
}

private static OpenTK.Mathematics.Matrix4 MakeSunMvp(ArraySpec array, System.Numerics.Vector3 sunDir)
{
    var bb = array.Mesh.BoundingBox;

    var center = new OpenTK.Mathematics.Vector3(
        (float)((bb.Min.X + bb.Max.X) * 0.5),
        (float)((bb.Min.Y + bb.Max.Y) * 0.5),
        (float)((bb.Min.Z + bb.Max.Z) * 0.5)
    );

    float dim = (float)(bb.Max - bb.Min).Length();
    float dist = 50f; // same idea as legacy (far enough to avoid near clipping)

    var s = new OpenTK.Mathematics.Vector3(sunDir.X, sunDir.Y, sunDir.Z);
    if (s.LengthSquared < 1e-8f) s = OpenTK.Mathematics.Vector3.UnitZ;
    s = OpenTK.Mathematics.Vector3.Normalize(s);

    var eye = center + s * dist;

    // Match legacy up = -Y (important for orientation consistency)
    var view = OpenTK.Mathematics.Matrix4.LookAt(eye, center, -OpenTK.Mathematics.Vector3.UnitY);

    // Orthographic to cover the full model
    float r = dim * 0.5f;
    var proj = OpenTK.Mathematics.Matrix4.CreateOrthographicOffCenter(-r, r, -r, r, 0.1f, dist * 2f);

    // OpenTK uses row-major but UniformMatrix4(transpose=false) is correct for their Matrix4
    return view * proj; // NOTE: if your math ends up flipped, swap order to (proj * view)
}

    private static OpenTK.Mathematics.Matrix3 NormalMatFromView(OpenTK.Mathematics.Matrix4 view)
    {
        return new OpenTK.Mathematics.Matrix3(view);
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
            if (Math.Abs(sunDir.Length() - 1.0f) > 1e-3)
            {
                sunDir = Vector3.Normalize(sunDir);
            }
            
            EnsureGlResources();

            var t1 = DateTime.Now;
            SetUniforms(array, wPerM2Insolation);
            ComputeRender(array, sunDir, wPerM2Insolation);
            var output = AnalyzeComputeTex(array, wPerM2Insolation, wPerM2Indirect, cTemp);
            var t2 = DateTime.Now;
            Debug.WriteLine($"finished sim step! {(t2 - t1).TotalSeconds:0.000}s {output.WattsInsolation:0.0}/{output.WattsOutput:0.0}W");
            return output;
        }
        
        private (int vao, int vbo) _clipTri;
        
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

            double totalArea = 0;
            double totalWattsIn = 0;
            
            for (int i = 0; i < ncells; i++)
            {
                totalWattsIn += wattsIn[i]; 
                totalArea += areas[i];
            }

            var cellSpec = array.CellSpec;
            int nstrings = array.Strings.Count;
            
            double totalWattsOutByCell = 0;
            double totalWattsOutByString = 0;
            
            var strings = new ArraySimStringOutput[nstrings];

            int cellIx = 0;
            for (int s = 0; s < nstrings; s++)
            {
                ArraySpec.CellString cellStr = array.Strings[s];

                double stringWattsIn = 0;
                double stringWattsOutByCell = 0; // "perfect MPPT" per-cell sum (upper bound)
                double stringLitArea = 0;

                var cellSweeps = new IVTrace[cellStr.Cells.Count];

                // Build cell IV sweeps and accumulate per-cell stats
                for (int j = 0; j < cellStr.Cells.Count; j++)
                {
                    double cellWattsIn = wattsIn[cellIx];
                    double cellLitArea = areas[cellIx];
                    cellIx++;

                    double cellInsolation = cellWattsIn / cellSpec.Area;
                    IVTrace cellSweep = CellSimulator.CalcSweep(cellSpec, cellInsolation, cTemp);

                    cellSweeps[j] = cellSweep;

                    stringWattsIn        += cellWattsIn;
                    stringWattsOutByCell += cellSweep.Pmp;
                    totalWattsOutByCell  += cellSweep.Pmp;

                    stringLitArea += cellLitArea;
                }

                // One MPPT per string: compute string IV and take its Pmp
                IVTrace stringIVTrace = StringSimulator.CalcStringIV(cellStr, cellSweeps, array.BypassDiodeSpec);
                double eta  = array.ArrayMPPT.getEfficiency(stringIVTrace.Vmp, stringIVTrace.Imp);
                double pOut = eta * stringIVTrace.Pmp;
                
                totalWattsOutByString += pOut;

                // "Ideal" reference: all cells at effective full irradiance
                double effIrr = (wPerM2Insolation + wPerM2Indirect) * (1.0 - array.EncapsulationLoss);
                double idealCellPmp = CellSimulator.CalcSweep(cellSpec, effIrr, cTemp).Pmp;

                var outStr = new ArraySimStringOutput
                {
                    WattsIn = stringWattsIn,
                    WattsOutputByCell = stringWattsOutByCell,
                    IVTrace = stringIVTrace,
                    WattsOutput = pOut,
                    String = cellStr,
                    Area = cellStr.Cells.Count * cellSpec.Area,
                    WattsOutputIdeal = idealCellPmp * cellStr.Cells.Count,
                    WattsStringMppElectrical = stringIVTrace.Pmp,
                    MpptEta = eta,
                    WattsInMaxDirect = wPerM2Insolation * cellStr.Cells.Count * cellSpec.Area,
                    WattsInMaxEff = (wPerM2Insolation + wPerM2Indirect) * (1.0 - array.EncapsulationLoss) * cellStr.Cells.Count * cellSpec.Area,
                };

                outStr.AreaShaded = Math.Max(0.0, outStr.Area - stringLitArea);

                strings[s] = outStr;
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
}
