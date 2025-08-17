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
    vLayoutUV = vec2((aPos.x - x0) / (x1 - x0), (aPos.z - z0) / (z1 - z0));
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

            _texCells = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texCells);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texCells);

            _texWatts = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texWatts);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texWatts);

            _texArea = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texArea);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _w, _h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            SetTexParams(_texArea);

            _texDepth = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _texDepth);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, _w, _h, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TextureParameter(_texCells, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(_texCells, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texCells, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _texWatts, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, _texArea, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _texDepth, 0);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException($"FBO incomplete: {status}");

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private static void SetTexParams(int tex)
        {
            GL.TextureParameter(tex, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(tex, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TextureParameter(tex, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TextureParameter(tex, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
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
            GL.UseProgram(_prog);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.DrawBuffers(3, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 });
            GL.Viewport(0, 0, _w, _h);
            GL.ClearColor(1f, 1f, 1f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var center = ComputeArrayCenter(array);
            double maxDim = ComputeArrayMaxDimension(array);
            Matrix4 mvp = BuildSunPOVMvp(sunDir, center, maxDim);
            
            GL.UseProgram(_prog);

            // // Get the 4 column locations of the mat4 uniform
            // int c0 = GL.GetUniformLocation(_prog, "uMvp[0]");
            // int c1 = GL.GetUniformLocation(_prog, "uMvp[1]");
            // int c2 = GL.GetUniformLocation(_prog, "uMvp[2]");
            // int c3 = GL.GetUniformLocation(_prog, "uMvp[3]");
            //
            // // Build column vectors (mat4 is column-major in GLSL)
            // var col0 = new OpenTK.Mathematics.Vector4(mvp.M11, mvp.M21, mvp.M31, mvp.M41);
            // var col1 = new OpenTK.Mathematics.Vector4(mvp.M12, mvp.M22, mvp.M32, mvp.M42);
            // var col2 = new OpenTK.Mathematics.Vector4(mvp.M13, mvp.M23, mvp.M33, mvp.M43);
            // var col3 = new OpenTK.Mathematics.Vector4(mvp.M14, mvp.M24, mvp.M34, mvp.M44);
            //
            // // Send each column with Uniform4 (no unsafe needed)
            // GL.Uniform4f(c0, 1, col0);
            // GL.Uniform4f(c1, 1, col1);
            // GL.Uniform4f(c2, 1, col2);
            // GL.Uniform4f(c3, 1, col3);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            var sprite = new MeshSprite(array.Mesh); // assumes VAO uses locations 0/1/2
            sprite.Render(mvp, Matrix4.Identity);

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
            GL.UseProgram(_prog);
            GL.Uniform1(_uX0, (float)array.LayoutBounds.MinX);
            GL.Uniform1(_uX1, (float)array.LayoutBounds.MaxX);
            GL.Uniform1(_uZ0, (float)array.LayoutBounds.MinZ);
            GL.Uniform1(_uZ1, (float)array.LayoutBounds.MaxZ);

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
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _texArray);

            var pixels = img.GetPixelMemoryGroup()[0].ToArray(); // get Rgba32[]
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, img.Width, img.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            SetTexParams(_texArray);
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
                GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, buf);

                float[] decoded = new float[_w * _h];
                for (int i = 0; i < decoded.Length; i++)
                {
                    byte r = buf[i * 4 + 0], g = buf[i * 4 + 1], b = buf[i * 4 + 2], a = buf[i * 4 + 3];
                    if (r == 255 && g == 255 && b == 255) continue;
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


        private Rgba32[] ReadColorTexture(FramebufferAttachment attachment)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.ReadBuffer((ReadBufferMode)attachment);

            var raw = new byte[_w * _h * 4];
            // Read directly into managed array
            GL.ReadPixels(0, 0, _w, _h, PixelFormat.Rgba, PixelType.UnsignedByte, raw);

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
                if (ColorUtils.IsGrayscale(color)) continue; // TODO: implement for ImageSharp
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
                    double cellWattsIn = wattsIn[cellIx++];
                    double cellInsolation = cellWattsIn / cellSpec.Area;
                    var cellSweep = CellSimulator.CalcSweep(cellSpec, cellInsolation, cTemp);
                    cellSweeps[j] = cellSweep;
                    stringWattsIn += cellWattsIn;
                    stringWattsOutByCell += cellSweep.Pmp;
                    totalWattsOutByCell += cellSweep.Pmp;
                    stringLitArea += areas[s]; // preserves legacy behavior
                }

                strings[s] = new ArraySimStringOutput
                {
                    WattsIn = stringWattsIn,
                    WattsOutputByCell = stringWattsOutByCell,
                    IVTrace = StringSimulator.CalcStringIV(cellStr, cellSweeps, array.BypassDiodeSpec),
                    String = cellStr,
                    Area = cellStr.Cells.Count * cellSpec.Area,
                    AreaShaded = 0,
                    WattsOutputIdeal = CellSimulator.CalcSweep(cellSpec, wPerM2Insolation, cTemp).Pmp * cellStr.Cells.Count,
                };
                strings[s].WattsOutput = strings[s].IVTrace.Pmp;
                strings[s].AreaShaded = strings[s].Area - stringLitArea;
                totalWattsOutByString += strings[s].IVTrace.Pmp;
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

    // public sealed class ArraySimComputeSurface : OpenGlControlBase
    // {
    //     private readonly ArraySimulator _sim = new();
    //     public ArraySimulationStepInput? PendingInput { get; set; }
    //     public ArraySimulationStepOutput? LastOutput { get; private set; }
    //
    //     // protected override void OnOpenGlInit(GlInterface gl, int fb) { }
    //     // protected override void OnOpenGlDeinit(GlInterface gl, int fb) { }
    //
    //     protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    //     {
    //         _sim.EnsureGlResources();
    //         if (PendingInput != null)
    //         {
    //             try { LastOutput = _sim.Simulate(PendingInput); }
    //             catch (Exception ex) { Debug.WriteLine(ex); }
    //             finally { PendingInput = null; }
    //         }
    //         GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    //         GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
    //         GL.ClearColor(0, 0, 0, 1);
    //         GL.Clear(ClearBufferMask.ColorBufferBit);
    //         // TODO: blit/visualize if desired
    //     }
    // }
}
