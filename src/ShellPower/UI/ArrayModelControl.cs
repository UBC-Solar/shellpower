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
using Point = Avalonia.Point;

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

        /* public model/view properties */
        private Sprite? _sprite;
        public Sprite? Sprite {
            get => _sprite;
            set {
                _sprite = value;
                if (_sprite is ShadowMeshSprite s) {
                    double arrayMaxDim = (s.BoundingBox.Max - s.BoundingBox.Min).Length();
                    _zoom = Math.Max(1e-3, arrayMaxDim * 1.8);
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

        protected override void OnOpenGlRender(GlInterface gl, int framebuffer) {
            if (!_glReady) return;
            var start = DateTime.Now;

            try {
                // Bind Avalonia-provided FBO (never assume 0)
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

                // Viewport uses pixel size (accounts for DPI)
                var px = GetFramebufferPixelSize();
                GL.Viewport(0, 0, px.Width, px.Height);

                // Clear
                GL.DrawBuffers(1, new[] { DrawBuffersEnum.ColorAttachment0 });
                GL.ClearColor(0f, 0f, 0.1f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                // Render
                if (Sprite != null && Array != null) {
                    SetModelViewCamera();
                    GLUtils.SetCameraProjectionPerspective(px.Width, px.Height);

                    Sprite.PushTransform();

                    GL.UseProgram(_shaderProg);
                    SetUniforms();
                    SetTexture();
                    Sprite.RenderMesh();

                    GL.UseProgram(0);
                    GL.BindTexture(TextureTarget.Texture2D, 0);

                    Sprite.RenderShadowOutline();
                    Sprite.RenderShadowVolume();

                    Sprite.PopTransform();
                }

                // EMA FPS
                framesRendered++;
                int period = Math.Min(1000, framesRendered);
                emaDelay = (DateTime.Now - start).TotalSeconds / period + emaDelay * (period - 1) / period;
                if (framesRendered % 1000 == 0) {
                    Debug.WriteLine($"{1.0 / Math.Max(1e-9, emaDelay):0.00} fps");
                }
            } catch (Exception ex) {
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
            Debug.WriteLine("compiling shaders");
            int shaderFrag = GL.CreateShader(ShaderType.FragmentShader);
            int shaderVert = GL.CreateShader(ShaderType.VertexShader);

            // NOTE: This uses legacy built-ins (gl_*). If your platform enforces a core profile,
            // replace these with a modern MVP + attributes pipeline and adjust Sprite accordingly.
            const string VERT_SRC = @"
uniform float x0, x1, z0, z1;
uniform vec3 sunDirection;
varying float cosRule;
void main()
{
    vec4 mv = gl_ModelViewMatrix * gl_Vertex;
    gl_Position = gl_ProjectionMatrix * mv;
    cosRule = dot(gl_Normal, sunDirection);
    gl_TexCoord[0] = vec4((gl_Vertex.x - x0) / (x1 - x0), (gl_Vertex.z - z0) / (z1 - z0), 0, 0);
}";
            const string FRAG_SRC = @"
varying float cosRule;
uniform sampler2D solarCells;
void main()
{
    vec4 solarCell = texture2D(solarCells, gl_TexCoord[0].xy);
    float watts = cosRule;
    if (solarCell.x == solarCell.y && solarCell.y == solarCell.z) {
        gl_FragData[0] = vec4(watts, watts, watts, 1.0);
    } else {
        gl_FragData[0] = vec4(solarCell.xyz, 1.0);
    }
}";

            GL.ShaderSource(shaderVert, VERT_SRC);
            GL.CompileShader(shaderVert);
            Debug.WriteLine("info (vert): " + GL.GetShaderInfoLog(shaderVert));

            GL.ShaderSource(shaderFrag, FRAG_SRC);
            GL.CompileShader(shaderFrag);
            Debug.WriteLine("info (frag): " + GL.GetShaderInfoLog(shaderFrag));

            _shaderProg = GL.CreateProgram();
            GL.AttachShader(_shaderProg, shaderVert);
            GL.AttachShader(_shaderProg, shaderFrag);
            GL.LinkProgram(_shaderProg);
            Debug.WriteLine("shader linked");

            GL.DeleteShader(shaderVert);
            GL.DeleteShader(shaderFrag);

            // uniform locations
            _uniformX0 = GL.GetUniformLocation(_shaderProg, "x0");
            _uniformX1 = GL.GetUniformLocation(_shaderProg, "x1");
            _uniformZ0 = GL.GetUniformLocation(_shaderProg, "z0");
            _uniformZ1 = GL.GetUniformLocation(_shaderProg, "z1");
            _uniformSolarCells = GL.GetUniformLocation(_shaderProg, "solarCells");
            _uniformSunDirection = GL.GetUniformLocation(_shaderProg, "sunDirection");
            Debug.Assert(_uniformX0 != -1 && _uniformX1 != -1 && _uniformZ0 != -1 && _uniformZ1 != -1);
            Debug.Assert(_uniformSolarCells != -1 && _uniformSunDirection != -1);
        }

        private void InitGLTextures() {
            _texArray = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);
            GLUtils.FastTexSettings(_texArray); // your helper (wraps MIN/MAG/WRAP params)
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void SetUniforms() {
            Debug.Assert(Array != null);

            GL.Uniform1(_uniformX0, (float)Array.LayoutBounds.MinX);
            GL.Uniform1(_uniformX1, (float)Array.LayoutBounds.MaxX);
            GL.Uniform1(_uniformZ0, (float)Array.LayoutBounds.MinZ);
            GL.Uniform1(_uniformZ1, (float)Array.LayoutBounds.MaxZ);
            GL.Uniform1(_uniformSolarCells, 0); // Texture unit 0

            var sunDir = Vector3.Zero;
            if (Sprite is ShadowMeshSprite s && s.Shadow.Light.Length() > 0) {
                sunDir = new Vector3(s.Shadow.Light.X, s.Shadow.Light.Y, s.Shadow.Light.Z);
                sunDir.Normalize();
            }
            GL.Uniform3(_uniformSunDirection, sunDir);
        }

        private void SetTexture()
        {
            // Use ImageSharp image (fallback to default)
            var img = Array?.LayoutTexture ?? DEFAULT_TEX;

            // Only (re)upload when reference changes
            if (_lastLayoutTex != null && ReferenceEquals(img, _lastLayoutTex))
                return;

            // Ensure a texture object exists
            if (_texArray == 0)
                _texArray = GL.GenTexture();

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            // Your helper uploads the pixels to the currently-bound texture
            GLUtils.LoadTexture(img, TextureUnit.Texture0, _texArray);

            // (Optional) if your helper doesn’t set filtering/wrap, keep this
            GLUtils.FastTexSettings(_texArray);

            _lastLayoutTex = img;
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
