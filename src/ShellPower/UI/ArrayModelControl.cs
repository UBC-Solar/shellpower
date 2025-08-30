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
using OTQ = OpenTK.Mathematics; // (optional alias)

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
        // private Matrix4 _rotation = Matrix4.CreateRotationX(-PI / 2f); // top-down

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
        
        private int _uMapMin, _uMapMax, _uAxes;

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

        private float _currentRotation = 90;
        
        private static Matrix4 BuildClipFitModel(ShadowMeshSprite sms, float margin = 0.1f)
        {
            var min = sms.BoundingBox.Min;
            var max = sms.BoundingBox.Max;
            var center = (min + max) * 0.5f;
            var size   = (max - min);
            float maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
            float s = (maxDim > 0) ? (2f * (1f - margin) / maxDim) : 1f;

            var S = Matrix4.CreateScale(s);
            var T = Matrix4.CreateTranslation(-new Vector3(center.X, center.Y, center.Z));
            return S * T;  // scale then translate to center at origin
        }
        
        private static Vector3 SunDirFromAzAlt(float azDeg, float altDeg)
        {
            float az  = MathF.PI * azDeg  / 180f;
            float alt = MathF.PI * altDeg / 180f;

            float y = MathF.Sin(alt);            // up
            float r = MathF.Cos(alt);
            float x = r * MathF.Sin(az);         // right
            float z = r * MathF.Cos(az);         // forward (+Z)

            return new Vector3(x, y, z);
        }
        
        private struct VaoBundle
        {
            public int Vao, Vbo, Ebo, IndexCount;
            public DrawElementsType IndexType;
        }

        private VaoBundle _arrayVao;
        
        private void EnsureArrayVaoBuilt()
        {
            if (_arrayVao.Vao != 0 || Array?.Mesh == null) return;
            _arrayVao = BuildVaoFromMesh(Array.Mesh);
        }

        private static VaoBundle BuildVaoFromMesh(Mesh m)
        {
            if (m.points.Length != m.normals.Length)
                throw new InvalidOperationException("Array mesh points/normals mismatch.");

            int vcount = m.points.Length;
            var interleaved = new float[vcount * 6];
            for (int i = 0; i < vcount; i++)
            {
                var p = m.points[i];
                var n = m.normals[i];
                int o = i * 6;
                interleaved[o+0]=p.X; interleaved[o+1]=p.Y; interleaved[o+2]=p.Z;
                interleaved[o+3]=n.X; interleaved[o+4]=n.Y; interleaved[o+5]=n.Z;
            }

            bool useShort = vcount <= ushort.MaxValue;
            int indexCount = m.triangles.Length * 3;

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            int ebo = GL.GenBuffer();

            GL.BindVertexArray(vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            if (useShort)
            {
                var idx = new ushort[indexCount];
                int k = 0;
                foreach (var t in m.triangles) { idx[k++]=(ushort)t.vertexA; idx[k++]=(ushort)t.vertexB; idx[k++]=(ushort)t.vertexC; }
                GL.BufferData(BufferTarget.ElementArrayBuffer, idx.Length * sizeof(ushort), idx, BufferUsageHint.StaticDraw);
            }
            else
            {
                var idx = new uint[indexCount];
                int k = 0;
                foreach (var t in m.triangles) { idx[k++]=(uint)t.vertexA; idx[k++]=(uint)t.vertexB; idx[k++]=(uint)t.vertexC; }
                GL.BufferData(BufferTarget.ElementArrayBuffer, idx.Length * sizeof(uint), idx, BufferUsageHint.StaticDraw);
            }

            // aPos
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6*sizeof(float), (IntPtr)0);
            // aNormal
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6*sizeof(float), (IntPtr)(3*sizeof(float)));

            GL.BindVertexArray(0);

            return new VaoBundle {
                Vao = vao, Vbo = vbo, Ebo = ebo, IndexCount = indexCount,
                IndexType = useShort ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt
            };
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

            GL.Enable(EnableCap.DepthTest);
            
            try
            {
                // Bind shader program
                GL.UseProgram(_shaderProg);
                
                // --- 🔎 DEBUG BLOCK GOES HERE ---
                int currentProgram;
                GL.GetInteger(GetPName.CurrentProgram, out currentProgram);

                var errCheck = GL.GetError();
                if (errCheck != ErrorCode.NoError)
                    Debug.WriteLine($"GL error immediately after UseProgram: {errCheck}");
                // --- END DEBUG BLOCK ---

                // viewport/proj
                var px = GetFramebufferPixelSize();
                GL.Viewport(0, 0, px.Width, px.Height);
                
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

                var err = GL.GetError();
                if (err != ErrorCode.NoError)
                    Debug.WriteLine($"[Render] GL error before draw: {err}");
                // --- END DEBUG ---
                
                // Render
                if (Sprite is ShadowMeshSprite sms)
                {
                    sms.EnsureGlResourcesCreated();
                    
                    double yawRad = _currentRotation * (Math.PI / 180); // 45°
                    _currentRotation += 0.5f;
                    double pitchRad = -30f * (Math.PI / 180); // 30°
                    
                    var R = Matrix4.CreateFromQuaternion( OTQ.Quaternion.Normalize( OTQ.Quaternion.FromAxisAngle(OTQ.Vector3.UnitX, (float)pitchRad) * OTQ.Quaternion.FromAxisAngle(OTQ.Vector3.UnitY, (float)yawRad)));
                    ident = R * OpenTK.Mathematics.Matrix4.Identity;
                    GL.UniformMatrix4(_uViewProj, false, ref ident);
                    SetModelUniform(sms.Position); // centers mesh at origin
                    
                    sms.RebuildOutlineBuffer();       // outline uses silhouette edges
                    sms.UpdateShadowVolumeVertices(); // volume uses same
                    
                    // sun dir normalized (defensive)
                    var L = new Vector3(sms.Shadow.Light.X, sms.Shadow.Light.Y, sms.Shadow.Light.Z);
                    if (L.LengthSquared < 1e-12f) L = new Vector3(0,1,0);
                    else L = Vector3.Normalize(L);
                    GL.Uniform3(_uSunDir, L);
                    
                    EnsureArrayVaoBuilt();
                    
                    SetMode(1);
                    SetColor(0.85f, 0.85f, 0.85f, 1f);
                    SetModelUniform(sms.Position);
                    GL.Enable(EnableCap.DepthTest);
                    GL.Enable(EnableCap.CullFace);
                    GL.FrontFace(FrontFaceDirection.Cw);
                    sms.RenderMesh();
                    
                    // 2) Array deck, with projector texture
                    if (_arrayVao.Vao != 0)
                    {
                        SetMode(0);
                        SetTexture();                         // ensures _texArray bound to unit 0
                        GL.Uniform1(_uSampler, 0);

                        SetProjectorAutoFromArrayMesh();        // <-- the important part

                        GL.BindVertexArray(_arrayVao.Vao);
                        GL.DrawElements(PrimitiveType.Triangles, _arrayVao.IndexCount, _arrayVao.IndexType, IntPtr.Zero);
                        GL.BindVertexArray(0);
                    }
                    
                    if (sms.ShowShadowVolume && sms.Shadow.Light.Y > 0f && sms.HasVolume)
                    {
                        SetMode(1);
                        SetColor(0f, 0f, 1f, 0.35f);
                        SetModelUniform(sms.Position);

                        GL.Enable(EnableCap.Blend);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                        GL.Enable(EnableCap.DepthTest);
                        GL.DepthMask(false);            // don’t write depth so it layers nicely
                        GL.Disable(EnableCap.CullFace); // volumes often need both sides

                        sms.RenderShadowVolume();

                        GL.DepthMask(true);
                        GL.Enable(EnableCap.CullFace);
                    }
                    
                    if (sms.ShowShadowOutline && sms.Shadow.Light.Y > 0f && sms.HasOutline)
                    {
                        SetMode(1);
                        SetColor(1f, 0f, 0f, 1f);
                        SetModelUniform(sms.Position);

                        GL.Disable(EnableCap.DepthTest); // draw on top
                        sms.RenderShadowOutline();
                        GL.Enable(EnableCap.DepthTest);
                    }

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

uniform vec2  mapMin;   // object-space mins
uniform vec2  mapMax;   // object-space maxs
uniform ivec2 axes;     // 0=X, 1=Y, 2=Z

uniform vec3  sunDirection;

out vec2  vTex;
out float vCos;

float pickComponent(vec3 v, int ix) {
    if (ix == 0) return v.x;
    if (ix == 1) return v.y;
    return v.z;
}

void main() {
    // Positions still transformed for drawing
    vec4 world = uModel * vec4(aPos, 1.0);
    gl_Position = uViewProj * world;

    // BUT: map texcoords from OBJECT space to avoid any model translate/scale
    float uu = pickComponent(aPos, axes.x);
    float vv = pickComponent(aPos, axes.y);

    vec2 span = mapMax - mapMin;
    vTex = vec2(0.0);
    if (abs(span.x) > 1e-6 && abs(span.y) > 1e-6) {
        vTex = (vec2(uu, vv) - mapMin) / span;
    }

    vec3 n = normalize(aNormal);
    vec3 l = normalize(sunDirection);
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

    vec2 tc = clamp(vTex, 0.0, 1.0);
    //FragColor = vec4(tc, 0.0, 1.0); // red=s, green=t
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
            _uMapMin  = GL.GetUniformLocation(_shaderProg, "mapMin");
            _uMapMax  = GL.GetUniformLocation(_shaderProg, "mapMax");
            _uAxes    = GL.GetUniformLocation(_shaderProg, "axes");
            _uSunDir  = GL.GetUniformLocation(_shaderProg, "sunDirection");
            _uSampler = GL.GetUniformLocation(_shaderProg, "solarCells");
            _uViewProj= GL.GetUniformLocation(_shaderProg, "uViewProj");
            _uModel   = GL.GetUniformLocation(_shaderProg, "uModel");
            _uMode    = GL.GetUniformLocation(_shaderProg, "uMode");
            _uColor   = GL.GetUniformLocation(_shaderProg, "uColor");

            // New assert set:
            Debug.Assert(_uMapMin!=-1 && _uMapMax!=-1 && _uAxes!=-1);
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
        
        private void SetProjectorAutoFromArrayMesh()
{
    Debug.Assert(Array != null);

    // Array deck geometry in OBJECT space
    var bb = Array.Mesh.BoundingBox;
    float bx0 = bb.Min.X, bx1 = bb.Max.X;
    float bz0 = bb.Min.Z, bz1 = bb.Max.Z;
    float bSpanX = MathF.Max(1e-12f, bx1 - bx0);
    float bSpanZ = MathF.Max(1e-12f, bz1 - bz0);

    // Texture aspect. Prefer actual image; fallback to LayoutBounds aspect if image missing.
    float texAspect = 1f;
    if (_lastLayoutTex != null)
        texAspect = (float)_lastLayoutTex.Width / MathF.Max(1, _lastLayoutTex.Height);
    else
    {
        var lb = Array.LayoutBounds;
        float lSpanX = MathF.Abs((float)(lb.MaxX - lb.MinX));
        float lSpanZ = MathF.Abs((float)(lb.MaxZ - lb.MinZ));
        if (lSpanZ > 1e-12f) texAspect = lSpanX / lSpanZ;
    }

    // Two candidates:
    // A) u<=X, v<=Z  → aspect = X/Z
    // B) u<=Z, v<=X  → aspect = Z/X
    float aspectA = bSpanX / bSpanZ;
    float aspectB = bSpanZ / bSpanX;

    bool useA = MathF.Abs(aspectA - texAspect) <= MathF.Abs(aspectB - texAspect);

    // We typically want V flipped so “top row” of the texture lands at larger Z (top of model).
    bool flipV = true;

    int axU, axV;           // shader 'axes' (0=X, 1=Y, 2=Z)
    float uMin, uMax, vMin, vMax;

    if (useA)
    {
        // u <- X, v <- Z
        axU = 0; axV = 2;
        uMin = bx0; uMax = bx1;
        vMin = flipV ? bz1 : bz0;
        vMax = flipV ? bz0 : bz1;
    }
    else
    {
        // u <- Z, v <- X  (swap)
        axU = 2; axV = 0;
        uMin = bz0; uMax = bz1;
        vMin = flipV ? bx1 : bx0;
        vMax = flipV ? bx0 : bx1;
    }

    // Upload mapping (shader builds UV from aPos using these mins/maxes and selected axes)
    GL.Uniform2(_uMapMin, uMin, vMin);
    GL.Uniform2(_uMapMax, uMax, vMax);
    GL.Uniform2(_uAxes, axU, axV);

#if DEBUG
    Debug.WriteLine($"[UV-Auto] useA={useA}  texAspect={texAspect:0.###}  " +
                    $"meshSpan=({bSpanX:0.###},{bSpanZ:0.###})  " +
                    $"mapMin=({uMin:0.###},{vMin:0.###}) mapMax=({uMax:0.###},{vMax:0.###})  axes=({axU},{axV})");
#endif
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
        
        // private OTQ.Quaternion _rotation = OTQ.Quaternion.FromAxisAngle(OTQ.Vector3.UnitX, -PI / 6f);
// ^ a gentle -30° tilt so you don’t stare edge-on

        
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
        
        private OpenTK.Graphics.OpenGL.DebugProc? _debugProc;
        private void DebugCallback(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
        {
            var msg = Marshal.PtrToStringAnsi(message, length);
            Debug.WriteLine($"[GL DEBUG] {severity} {type} {id}: {msg}");
        }
    }
}
