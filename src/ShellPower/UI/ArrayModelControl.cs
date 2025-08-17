using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL; // <-- use this namespace

namespace SSCP.ShellPower {
    public class ArrayModelControl : OpenGlControlBase
    {
        private readonly DispatcherTimer _renderTimer;

        public ArrayModelControl()
        {
            // Set up ~60 FPS render timer
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _renderTimer.Tick += (s, e) => InvalidateVisual();
            _renderTimer.Start();
        }

        // your fields
        private int shaderProg;
        public Sprite? Sprite { get; set; }

        private double emaDelay;
        private int framesRendered;

        // Adapter so OpenTK’s GL gets procedures from Avalonia’s context
        private sealed class OpenTKBindingsContext : OpenTK.IBindingsContext {
            private readonly GlInterface _gl;
            public OpenTKBindingsContext(GlInterface gl) => _gl = gl;
            public IntPtr GetProcAddress(string procName) => _gl.GetProcAddress(procName);
        }

        protected override void OnOpenGlInit(GlInterface gl) {
            // Wire OpenTK to Avalonia’s context
            GL.LoadBindings(new OpenTKBindingsContext(gl));

            // One-time GL state
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.Disable(EnableCap.Blend);

            // TODO: compile/link shaders, create VAOs/VBOs/textures here as needed
            // shaderProg = CompileYourProgram();
        }

        // protected override void OnOpenGlDeinit(GlInterface gl, int framebuffer) {
        //     // TODO: delete GL resources you own
        //     // GL.DeleteProgram(shaderProg);
        // }

        protected override void OnOpenGlRender(GlInterface gl, int framebuffer) {
            var start = DateTime.Now;

            try {
                // Bind the FBO provided by Avalonia (don’t assume 0 is correct)
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

                // Viewport must use framebuffer pixel size (accounts for DPI scale)
                var px = GetFramebufferPixelSize();
                GL.Viewport(0, 0, px.Width, px.Height);

                // Clear
                GL.ClearColor(0f, 0f, 0.1f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                if (Sprite != null) {
                    // Use pixel size for projection
                    GLUtils.SetCameraProjectionPerspective(px.Width, px.Height);

                    Sprite.PushTransform();

                    GL.UseProgram(shaderProg);
                    
                    GLUtils.UploadCameraUniforms(shaderProg);
                    
                    SetUniforms();
                    SetTexture();
                    Sprite.RenderMesh();

                    GL.UseProgram(0);
                    GL.BindTexture(TextureTarget.Texture2D, 0); // <-- Texture2D (capital D)

                    Sprite.RenderShadowOutline();
                    Sprite.RenderShadowVolume();

                    Sprite.PopTransform();
                }

                // No SwapBuffers here; Avalonia handles that.

                // EMA FPS stats
                framesRendered++;
                int period = Math.Min(1000, framesRendered);
                emaDelay = (DateTime.Now - start).TotalSeconds / period
                           + emaDelay * (period - 1) / period;
                if (framesRendered % 1000 == 0) {
                    Debug.WriteLine($"{1.0 / Math.Max(1e-9, emaDelay):0.00} fps");
                }
            }
            catch (Exception ex) {
                Debug.WriteLine("ArrayModelControl render error: " + ex);
            }
        }

        private PixelSize GetFramebufferPixelSize() {
            var scale = VisualRoot?.RenderScaling ?? 1.0;
            int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
            int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
            return new PixelSize(w, h);
        }

        private void SetUniforms() {
            // GL.Uniform* calls for matrices/colors/etc.
        }

        private void SetTexture() {
            // Example:
            // GL.ActiveTexture(TextureUnit.Texture0);
            // GL.BindTexture(TextureTarget.Texture2D, yourTexId);
            // GL.Uniform1(samplerLocation, 0);
        }
    }
}
