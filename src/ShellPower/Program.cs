using System;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL;

// namespace SSCP.ShellPower
// {
//     static class Program
//     {
//         [STAThread]
//         static void Main()
//         {
//             Logger.info("starting shellpower");
//
//             // 1) Configure window settings
//             var nativeSettings = new NativeWindowSettings
//             {
//                 Size = new OpenTK.Mathematics.Vector2i(1280, 720),
//                 Title = "ShellPower",
//                 // Uncomment if you want to force a specific API version:
//                 // APIVersion = new Version(4, 6),
//             };
//             
//             // 2) Create the GameWindow
//             using var window = new GameWindow(GameWindowSettings.Default, nativeSettings);
//
//             // 3) Hook lifecycle events
//             window.Load += OnLoad;
//             window.UpdateFrame += OnUpdateFrame;
//             window.RenderFrame += OnRenderFrame;
//             window.Unload += OnUnload;
//
//             // 4) Start the render loop (blocks until window is closed)
//             window.Run();
//         }
//
//         private static void OnLoad()
//         {
//             // Called once after window and GL context are ready
//             GL.ClearColor(0.1f, 0.2f, 0.3f, 1.0f);
//             Logger.info("OpenGL version: " + GL.GetString(StringName.Version));
//             // Load shaders, textures, setup VAOs/VBOs…
//         }
//
//         private static void OnUpdateFrame(FrameEventArgs args)
//         {
//             // Called on a fixed timestep (~60Hz by default)
//             // Handle input, update game logic, GUI state…
//         }
//
//         private static void OnRenderFrame(FrameEventArgs args)
//         {
//             // Called as fast as possible by default
//             GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
//
//             // Draw your scene here…
//
//             // Swap front/back buffers to display this frame
//             ((GameWindow)args.Window).SwapBuffers();
//         }
//
//         private static void OnUnload()
//         {
//             // Clean up any GL resources (shaders, buffers, textures…) here
//         }
//     }
// }
