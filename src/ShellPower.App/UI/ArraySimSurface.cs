namespace SSCP.ShellPower;

using System;
using System.Diagnostics;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL;   // GL API (core, cross-version)


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
