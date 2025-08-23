using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Avalonia.Rendering;
using SixLabors.ImageSharp.Advanced;
using Point = Avalonia.Point;
using SixLabors.ImageSharp.Processing; // <= add this

namespace SSCP.ShellPower {
    public class ArrayModelControl : OpenGlControlBase {
        /* convenience */
        private const float PI = (float)Math.PI;

        /* render stats */
        private double emaDelay = 1;
        private int framesRendered = 0;

        /* input state */
        private PixelPoint _lastMousePx;
        private bool _mouseRotate = false;

        /* view state */
        private const double INITIAL_ZOOM = 20;
        private double _zoom = INITIAL_ZOOM;                  // meters away from the model
        private Matrix4 _rotation = Matrix4.CreateRotationX(-PI / 2f); // top-down

        /* GL state */
        private bool _glReady = false;
        private int _uniformX0, _uniformX1, _uniformZ0, _uniformZ1;
        private int _uniformSolarCells, _uniformSunDirection;
        private int _shaderProg = 0;
        private int _texArray = 0;
        
        // uniforms
        private int _uX0, _uX1, _uZ0, _uZ1;
        private int _uSunDir, _uSampler;
        private int _uViewProj, _uModel;
        private int _uMode, _uColor;


        /* public model/view properties */
        private Sprite? _sprite;
        public Sprite? Sprite {
            get => _sprite;
            set {
                _sprite = value;
                if (_sprite is ShadowMeshSprite s) {
                    double arrayMaxDim = (s.BoundingBox.Max - s.BoundingBox.Min).Length();
                    _zoom = Math.Max(1e-3, arrayMaxDim * 1.8);
                    Debug.WriteLine($"[ArrayModelControl] Sprite assigned. zoom={_zoom:0.###}, bbox={s.BoundingBox.Min}..{s.BoundingBox.Max}");
                }
            }
        }

        public ArraySpec? Array { get; set; }


        private Image<Rgba32>? _lastLayoutTex;
        public static readonly Image<Rgba32> DEFAULT_TEX = CreateDefaultTexImage(800, 400);
        private static Image<Rgba32> CreateDefaultTexImage(int w, int h)
        {
            // 50% gray, fully opaque
            return new Image<Rgba32>(w, h, new Rgba32(128, 128, 128, 255));
        }

        /* render timer (~60 FPS, Avalonia owns swap) */
        private readonly DispatcherTimer _renderTimer;

        public ArrayModelControl() {
            Focusable = true;

            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _renderTimer.Tick += (_, __) => InvalidateVisual();
            _renderTimer.Start();

            // Input wiring (Avalonia)
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerMoved += OnPointerMoved;
            PointerWheelChanged += OnPointerWheel;
            KeyDown += OnKeyDown;
        }

        // ---------- OpenTK binding to Avalonia GL ----------
        private sealed class OpenTKBindingsContext : OpenTK.IBindingsContext {
            private readonly GlInterface _gl;
            public OpenTKBindingsContext(GlInterface gl) => _gl = gl;
            public IntPtr GetProcAddress(string procName) => _gl.GetProcAddress(procName);
        }

        // ---------- GL lifecycle ----------
        protected override void OnOpenGlInit(GlInterface gl) {
            
            GL.LoadBindings(new OpenTKBindingsContext(gl));
            
            try
            {
                // Works on macOS via ARB_debug_output if present
                var exts = GL.GetString(StringName.Extensions);
                if (exts?.Contains("GL_KHR_debug") == true || exts?.Contains("GL_ARB_debug_output") == true)
                {
                    GL.Enable(EnableCap.DebugOutput);
                    GL.Enable(EnableCap.DebugOutputSynchronous);
                    _debugProc ??= DebugCallback;
                    GL.DebugMessageCallback(_debugProc, IntPtr.Zero);
                }
            }
            catch { /* ignore if unsupported */ }

            
            Debug.WriteLine($"GL Version: {GL.GetString(StringName.Version)}");
            Debug.WriteLine($"GLSL Version: {GL.GetString(StringName.ShadingLanguageVersion)}");
            Debug.WriteLine($"Vendor: {GL.GetString(StringName.Vendor)} Renderer: {GL.GetString(StringName.Renderer)}");
            
            // depth, cull, blending
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            InitGLShaders();
            InitGLTextures();
            _glReady = true;
        }

        protected override void OnOpenGlDeinit(GlInterface gl) {
            try {
                if (_shaderProg != 0) {
                    GL.DeleteProgram(_shaderProg);
                    _shaderProg = 0;
                }
                if (_texArray != 0) {
                    GL.DeleteTexture(_texArray);
                    _texArray = 0;
                }
            } catch { /* best effort */ }
            _glReady = false;
        }

        protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
        {
            if (!_glReady) return;
            
            GL.ClearColor(0.10f, 0.10f, 0.12f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(_shaderProg);

// Upload an identity uViewProj / uModel so clip-space positions work directly
            var ident = OpenTK.Mathematics.Matrix4.Identity;
            GL.UniformMatrix4(_uViewProj, false, ref ident);
            GL.UniformMatrix4(_uModel, false, ref ident);
            GL.Uniform1(_uMode, 1);
            GL.Uniform4(_uColor, 0.2f, 0.8f, 0.4f, 1f);

// Quick-and-dirty immediate buffer (one frame) just to see something
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            float[] verts = {
                -0.5f, -0.5f, 0f,
                0.5f, -0.5f, 0f,
                0.0f,  0.5f, 0f
            };
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.DisableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
            GL.DeleteBuffer(vbo);
            GL.DeleteVertexArray(vao);

            GL.Enable(EnableCap.DepthTest);

            var start = DateTime.Now;

            try
            {
                // Bind shader program
                GL.UseProgram(_shaderProg);
                
                // --- 🔎 DEBUG BLOCK GOES HERE ---
                int currentProgram;
                GL.GetInteger(GetPName.CurrentProgram, out currentProgram);
                // Debug.WriteLine($"Current program={currentProgram}, expected={_shaderProg}");

                // if (_uViewProj >= 0)
                //     Debug.WriteLine($"uViewProj loc={_uViewProj}");
                // else
                //     Debug.WriteLine("Warning: uViewProj invalid");

                // if (_uModel >= 0)
                //     Debug.WriteLine($"uModel loc={_uModel}");
                // else
                //     Debug.WriteLine("Warning: uModel invalid");
                //
                // if (_uSampler >= 0)
                //     Debug.WriteLine($"uSampler loc={_uSampler}");
                // else
                //     Debug.WriteLine("Warning: uSampler invalid");

                var errCheck = GL.GetError();
                if (errCheck != ErrorCode.NoError)
                    Debug.WriteLine($"GL error immediately after UseProgram: {errCheck}");
                // --- END DEBUG BLOCK ---

                // viewport/proj
                var px = GetFramebufferPixelSize();
                GL.Viewport(0, 0, px.Width, px.Height);
                SetViewProjUniform(px);

                // uniforms (bounds + sun direction)
                SetUniforms();

                // Make sure texture is uploaded + bound
                SetTexture();

                // --- EXTRA DEBUG ---
                // Bind texture again explicitly
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _texArray);
                GL.Uniform1(_uSampler, 0);

                int boundTex;
                GL.GetInteger(GetPName.TextureBinding2D, out boundTex);
                // Debug.WriteLine($"[Render] texId={_texArray}, boundTex={boundTex}");

                var err = GL.GetError();
                if (err != ErrorCode.NoError)
                    Debug.WriteLine($"[Render] GL error before draw: {err}");
                // --- END DEBUG ---

                // Render
                if (Sprite is ShadowMeshSprite sms)
                {
                    sms.EnsureGlResourcesCreated();   // ensure VAOs/VBOs only when context is current

                    // Mesh (textured/lambert)
                    SetMode(0);
                    SetModelUniform(sms.Position);
                    
                    // Debug before draw
                    err = GL.GetError();
                    if (err != ErrorCode.NoError)
                        Debug.WriteLine($"GL error pre-RenderMesh: {err}");

                    sms.RenderMesh();

                    err = GL.GetError();
                    if (err != ErrorCode.NoError)
                        Debug.WriteLine($"GL error post-RenderMesh: {err}");

                    // // Shadow volume (solid translucent), only if sun is above horizon
                    // if (sms.ShowShadowVolume && sms.Shadow.Light.Y > 0f)
                    // {
                    //     sms.UpdateShadowVolumeVertices(); // refresh dynamic VBO
                    //     if (sms.HasVolume)
                    //     {
                    //         SetMode(1);
                    //         SetColor(0f, 0f, 1f, 0.4f);
                    //         SetModelUniform(sms.Position);
                    //         
                    //         // Debug before draw
                    //         err = GL.GetError();
                    //         if (err != ErrorCode.NoError)
                    //             Debug.WriteLine($"GL error pre-RenderShadowVolume: {err}");
                    //
                    //         sms.RenderShadowVolume(); // binds VAO and draws
                    //
                    //         err = GL.GetError();
                    //         if (err != ErrorCode.NoError)
                    //             Debug.WriteLine($"GL error post-RenderShadowVolume: {err}");
                    //         
                    //     }
                    // }

                    // // Shadow outline (solid red), only if sun is above horizon
                    // if (sms.ShowShadowOutline && sms.Shadow.Light.Y > 0f && sms.HasOutline)
                    // {
                    //     SetMode(1);
                    //     SetColor(1f, 0f, 0f, 1f);
                    //     SetModelUniform(sms.Position);
                    //     
                    //     // Debug before draw
                    //     err = GL.GetError();
                    //     if (err != ErrorCode.NoError)
                    //         Debug.WriteLine($"GL error pre-RenderShadowOutline: {err}");
                    //
                    //     sms.RenderShadowOutline();
                    //
                    //     err = GL.GetError();
                    //     if (err != ErrorCode.NoError)
                    //         Debug.WriteLine($"GL error post-RenderShadowOutline: {err}");
                    // }
                }

                // Cleanup
                GL.UseProgram(0);
                GL.BindTexture(TextureTarget.Texture2D, 0);

                err = GL.GetError();
                if (err != ErrorCode.NoError)
                    Debug.WriteLine($"[Render] GL error after draw: {err}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ArrayModelControl render error: " + ex);
            }
        }


        private PixelSize GetFramebufferPixelSize() {
            var scale = VisualRoot?.RenderScaling ?? 1.0;
            int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
            int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
            return new PixelSize(w, h);
        }

        // ---------- Camera ----------
        private void SetViewport() {
            var px = GetFramebufferPixelSize();
            GL.Viewport(0, 0, px.Width, px.Height);
        }

        private void SetModelViewCamera() {
            var position = -Vector3.UnitZ * (float)_zoom;
            var modelview = Matrix4.LookAt(position, Vector3.Zero, Vector3.UnitY) * _rotation;
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref modelview);
        }

        // ---------- Shaders / Textures ----------
        private void InitGLShaders() {
            Debug.WriteLine("compiling shaders (core)");

            int shaderFrag = GL.CreateShader(ShaderType.FragmentShader);
            int shaderVert = GL.CreateShader(ShaderType.VertexShader);

            const string VERT_SRC = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;

uniform mat4 uViewProj;
uniform mat4 uModel;

uniform float x0, x1, z0, z1;
uniform vec3  sunDirection;

out vec2 vTex;
out float vCos;

void main(){
    vec4 world = uModel * vec4(aPos,1.0);
    gl_Position = uViewProj * world;

    // Safe texture coords: guard against divide by zero
    float dx = x1 - x0;
    float dz = z1 - z0;
    vTex = vec2(0.0, 0.0);
    if (abs(dx) > 1e-6 && abs(dz) > 1e-6) {
        vTex = vec2((aPos.x - x0) / dx,
                    (aPos.z - z0) / dz);
    }

    // simple lambert factor
    vec3 n = normalize(aNormal);
    vec3 l = sunDirection;
    float lenL = length(l);
    l = (lenL > 1e-6) ? (l / lenL) : vec3(0.0, 1.0, 0.0); // fallback up-vector
    vCos = dot(n, l);
}
";

            const string FRAG_SRC = @"
#version 330 core
in vec2  vTex;
in float vCos;

uniform sampler2D solarCells;
uniform int  uMode;   // 0 = textured/lambert, 1 = solid color
uniform vec4 uColor;

out vec4 FragColor;

void main(){
    if (uMode == 0) {
        vec2 tc = clamp(vTex, 0.0, 1.0);  // avoid NaN/Inf sampling
        vec4 solarCell = texture(solarCells, tc);

        float watts = max(vCos, 0.0); // clamp to avoid negative light

        // if grayscale, show watts; else show cell color
        if (abs(solarCell.r - solarCell.g) < 1e-5 &&
            abs(solarCell.g - solarCell.b) < 1e-5)
        {
            FragColor = vec4(watts, watts, watts, 1.0);
        }
        else
        {
            FragColor = vec4(solarCell.rgb, 1.0);
        }
    } else {
        FragColor = uColor;
    }
}
"
;

            GL.ShaderSource(shaderVert, VERT_SRC);
            GL.CompileShader(shaderVert);
            var vLog = GL.GetShaderInfoLog(shaderVert);
            if (!string.IsNullOrWhiteSpace(vLog)) Debug.WriteLine("vert log: " + vLog);

            GL.ShaderSource(shaderFrag, FRAG_SRC);
            GL.CompileShader(shaderFrag);
            var fLog = GL.GetShaderInfoLog(shaderFrag);
            if (!string.IsNullOrWhiteSpace(fLog)) Debug.WriteLine("frag log: " + fLog);

            _shaderProg = GL.CreateProgram();
            GL.AttachShader(_shaderProg, shaderVert);
            GL.AttachShader(_shaderProg, shaderFrag);
            GL.LinkProgram(_shaderProg);

            GL.GetProgram(_shaderProg, GetProgramParameterName.LinkStatus, out int linked);
            var pLog = GL.GetProgramInfoLog(_shaderProg);
            if (!string.IsNullOrWhiteSpace(pLog)) Debug.WriteLine("prog log: " + pLog);
            if (linked == 0)
                throw new InvalidOperationException("Shader link failed (core). See logs above.");

            GL.DeleteShader(shaderVert);
            GL.DeleteShader(shaderFrag);

            // uniform locations
            _uX0 = GL.GetUniformLocation(_shaderProg, "x0");
            _uX1 = GL.GetUniformLocation(_shaderProg, "x1");
            _uZ0 = GL.GetUniformLocation(_shaderProg, "z0");
            _uZ1 = GL.GetUniformLocation(_shaderProg, "z1");
            _uSunDir   = GL.GetUniformLocation(_shaderProg, "sunDirection");
            _uSampler  = GL.GetUniformLocation(_shaderProg, "solarCells");
            _uViewProj = GL.GetUniformLocation(_shaderProg, "uViewProj");
            _uModel    = GL.GetUniformLocation(_shaderProg, "uModel");
            _uMode     = GL.GetUniformLocation(_shaderProg, "uMode");
            _uColor    = GL.GetUniformLocation(_shaderProg, "uColor");

            Debug.Assert(_uX0!=-1 && _uX1!=-1 && _uZ0!=-1 && _uZ1!=-1);
            Debug.Assert(_uSunDir!=-1 && _uSampler!=-1 && _uViewProj!=-1 && _uModel!=-1);
            Debug.Assert(_uMode!=-1 && _uColor!=-1);
        }

        private void InitGLTextures() {
            _texArray = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            // Replace FastTexSettings with:
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // Don’t forget to allocate storage at least once:
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        }

        private void SetUniforms()
        {
            Debug.Assert(Array != null);

            if (_uX0 >= 0) { GL.Uniform1(_uX0, (float)Array.LayoutBounds.MinX); CheckGLError("uX0"); }
            if (_uX1 >= 0) { GL.Uniform1(_uX1, (float)Array.LayoutBounds.MaxX); CheckGLError("uX1"); }
            if (_uZ0 >= 0) { GL.Uniform1(_uZ0, (float)Array.LayoutBounds.MinZ); CheckGLError("uZ0"); }
            if (_uZ1 >= 0) { GL.Uniform1(_uZ1, (float)Array.LayoutBounds.MaxZ); CheckGLError("uZ1"); }

            if (_uSampler >= 0)
            {
                GL.Uniform1(_uSampler, 0); // Texture unit 0
                CheckGLError("uSampler");
            }
            else
            {
                Debug.WriteLine("Warning: uSampler location invalid (-1)");
            }

            var sunDir = OpenTK.Mathematics.Vector3.Zero;
            if (Sprite is ShadowMeshSprite s && s.Shadow.Light.Length() > 0)
            {
                sunDir = new Vector3(s.Shadow.Light.X, s.Shadow.Light.Y, s.Shadow.Light.Z);
            }
            if (sunDir.LengthSquared < 1e-8f)
                sunDir = new Vector3(0, 1, 0);  // fallback to “up”
            
            if (_uSunDir >= 0)
            {
                GL.Uniform3(_uSunDir, sunDir);
                CheckGLError("uSunDir");
            }
            else
            {
                Debug.WriteLine("Warning: uSunDir location invalid (-1)");
            }
        }
        
        [Conditional("DEBUG")]
        private void CheckGLError(string where)
        {
            var err = GL.GetError();
            if (err != ErrorCode.NoError)
                Debug.WriteLine($"GL error at {where}: {err}");
        }

        
        private static Matrix4 CreatePerspective(float fovDeg, float aspect, float near, float far) {
            return Matrix4.CreatePerspectiveFieldOfView((float)(Math.PI * fovDeg / 180.0), aspect, near, far);
        }

        private void SetViewProjUniform(PixelSize px)
        {
            float aspect = px.Width / Math.Max(1f, (float)px.Height);
            var proj = CreatePerspective(60f, aspect, 0.01f, 5000f);

            var eye = -OpenTK.Mathematics.Vector3.UnitZ * (float)_zoom;
            var view = Matrix4.LookAt(eye, OpenTK.Mathematics.Vector3.Zero, OpenTK.Mathematics.Vector3.UnitY) * _rotation;

            var vp = proj * view;

            if (_uViewProj >= 0)
            {
                GL.UniformMatrix4(_uViewProj, false, ref vp);
                var err = GL.GetError();
                if (err != ErrorCode.NoError)
                    Debug.WriteLine($"GL error uploading uViewProj: {err}");
            }
            else
            {
                Debug.WriteLine("Warning: uViewProj location invalid (-1)");
            }
        }


        private void SetModelUniform(OpenTK.Mathematics.Vector3 pos) {
            var m = Matrix4.CreateTranslation(pos);
            GL.UniformMatrix4(_uModel, false, ref m);
        }

        private void SetMode(int mode) => GL.Uniform1(_uMode, mode);
        private void SetColor(float r, float g, float b, float a) => GL.Uniform4(_uColor, r, g, b, a);

        private void SetTexture()
        {
            var img = Array?.LayoutTexture ?? DEFAULT_TEX;

            // Is our GL texture alive in *this* context?
            bool texAlive = _texArray != 0 && GL.IsTexture(_texArray);

            // Re-upload if the image changed OR the GL object isn't alive yet.
            bool needUpload = !ReferenceEquals(img, _lastLayoutTex) || !texAlive;

            if (!texAlive)
            {
                // (Re)create the texture object for this context.
                _texArray = GL.GenTexture();
            }

            // Always bind before use
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            if (needUpload)
            {
                // Hardened upload: alloc first, then subimage; explicit pixel store.
                // If you already have GLUtils.UploadImageToTexture with these protections, call that instead.
                UploadImageToBoundTexture(img);

                _lastLayoutTex = img;
            }

            // Make sure the sampler points at unit 0 while the program is bound
            GL.Uniform1(_uSampler, 0);
        }
        
        // Local helper that does a robust upload to the *currently bound* 2D texture.
        private static void UploadImageToBoundTexture(Image<Rgba32> img)
        {
            GL.GetInteger(GetPName.MaxTextureSize, out int maxTex);
            int w = img.Width, h = img.Height;

            if (w > maxTex || h > maxTex)
            {
                img = img.Clone(ctx => ctx.Resize(new SixLabors.ImageSharp.Size(
                    Math.Min(w, maxTex), Math.Min(h, maxTex))));
                w = img.Width; h = img.Height;
            }

            // Safe pixel-store
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
#if !WINDOWS
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
#endif

            // Allocate storage
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            // ✅ Make a single contiguous buffer of size w*h*4
            var bytes = new byte[w * h * 4];
            img.CopyPixelDataTo(bytes);

            // Upload
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
                PixelFormat.Rgba, PixelType.UnsignedByte, bytes);

            // Params
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
        
        // ---------- Screenshot for Save Render ----------
        public Bitmap GrabScreenshot()
        {
            var px = GetFramebufferPixelSize();
            int w = px.Width, h = px.Height;

            // Read pixels from the currently bound framebuffer (BGRA8)
            var data = new byte[w * h * 4];
            GL.ReadPixels(0, 0, w, h, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data);

            // Flip vertically (OpenGL origin is bottom-left)
            int stride = w * 4;
            for (int y = 0; y < h / 2; y++)
            {
                int iTop = y * stride;
                int iBot = (h - 1 - y) * stride;
                for (int x = 0; x < stride; x++)
                {
                    (data[iTop + x], data[iBot + x]) = (data[iBot + x], data[iTop + x]);
                }
            }

            // Write into an Avalonia WriteableBitmap
            var wb = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888
            );

            using (var fb = wb.Lock())
            {
                Marshal.Copy(data, 0, fb.Address, data.Length);
            }

            // Return an immutable Bitmap
            using var ms = new MemoryStream();
            wb.Save(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }

        // ---------- Input (mouse, wheel, keyboard) ----------
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e) {
            Focus(); // ensure we receive KeyDown
            _lastMousePx = e.GetPosition(this).ToPixelPoint(VisualRoot);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                _mouseRotate = true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) {
            _lastMousePx = e.GetPosition(this).ToPixelPoint(VisualRoot);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                _mouseRotate = false;
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e) {
            var cur = e.GetPosition(this).ToPixelPoint(VisualRoot);
            if (_mouseRotate) {
                float sensitivity = 1.0f / 100;
                var xdelta = cur.X - _lastMousePx.X;
                var ydelta = cur.Y - _lastMousePx.Y;
                _rotation *= Matrix4.CreateRotationY(xdelta * sensitivity)
                           * Matrix4.CreateRotationX(-ydelta * sensitivity);
            }
            _lastMousePx = cur;
        }

        private void OnPointerWheel(object? sender, PointerWheelEventArgs e) {
            // Avalonia e.Delta.Y is in “lines”; positive is usually up
            double sensitivity = 1.0 / 300.0;
            _zoom *= Math.Exp(-e.Delta.Y * 120.0 * sensitivity); // approximate WinForms wheel step (120)
            _zoom = Math.Clamp(_zoom, 0.01, 1e6);
        }

        private OpenTK.Graphics.OpenGL.DebugProc? _debugProc;
        private void DebugCallback(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
        {
            var msg = Marshal.PtrToStringAnsi(message, length);
            Debug.WriteLine($"[GL DEBUG] {severity} {type} {id}: {msg}");
        }

        private void OnKeyDown(object? sender, KeyEventArgs e) {
            float zoomSensitivity = 0.05f;
            float rotateSensitivity = PI / 16;
            bool isShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (isShift) {
                zoomSensitivity = .5f;
                rotateSensitivity = PI / 2;
            }

            switch (e.Key) {
                case Key.W: _zoom *= (1 - zoomSensitivity); break;
                case Key.S: _zoom /= (1 - zoomSensitivity); break;
                case Key.A: _rotation *= Matrix4.CreateRotationY(-rotateSensitivity); break;
                case Key.D: _rotation *= Matrix4.CreateRotationY(rotateSensitivity); break;

                case Key.X: _rotation = Matrix4.CreateRotationY(PI / 2 * (isShift ? -1 : 1)); break;
                case Key.Y: _rotation = Matrix4.CreateRotationX(PI / 2 * (isShift ? -1 : 1)); break;
                case Key.Z: _rotation = Matrix4.CreateRotationY(PI / 2 * (isShift ? 2 : 0)); break;
                case Key.D0:
                case Key.NumPad0:
                    _rotation = Matrix4.Identity;
                    _zoom = INITIAL_ZOOM;
                    break;
            }
        }
    }

    static class AvaloniaPointerExtensions
    {
        public static PixelPoint ToPixelPoint(this Point p, IRenderRoot? root)
        {
            var scale = root?.RenderScaling ?? 1.0;
            return new PixelPoint(
                (int)Math.Round(p.X * scale),
                (int)Math.Round(p.Y * scale)
            );
        }
    }
}
