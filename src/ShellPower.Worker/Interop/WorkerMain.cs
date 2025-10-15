using System.Text.Json;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Mathematics;
using SSCP.ShellPower;

namespace SSCP.ShellPower.Worker;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            // Read one JSON request from stdin
            var json = Console.In.ReadToEnd();
            var req = JsonSerializer.Deserialize<SimulationRequest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new ArgumentException("Invalid request JSON");

            // Create hidden GL context (Core 4.1 works on macOS/Windows/Linux)
            var gws = GameWindowSettings.Default;
            var nws = new NativeWindowSettings
            {
                Size = new Vector2i(64,64),
                StartVisible = false,
                StartFocused = false,
                API = ContextAPI.OpenGL,
                Profile = ContextProfile.Core,
                APIVersion = new Version(4,1)
            };

            using var window = new GameWindow(gws, nws);
            window.MakeCurrent();

            // Run sim (Core assumes GL is current on this thread)
            var input = SimulationBuilder.BuildInput(req);
            var sim   = new ArraySimulator();
            var step  = sim.Simulate(input);
            var resp  = SimulationBuilder.ToResponse(step);

            // Let's not leak this memory!
            input.Array.LayoutTexture?.Dispose();
            
            Console.Out.Write(JsonSerializer.Serialize(resp));
            Console.Out.Flush();
            // Optionally pump events once
            
            window.ProcessEvents(10);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}