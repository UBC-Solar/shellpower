using System.Buffers.Binary;
using System.Text.Json;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using SSCP.ShellPower;

namespace SSCP.ShellPower.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static int Main(string[] args)
    {
        try
        {
            var gws = GameWindowSettings.Default;
            var nws = new NativeWindowSettings
            {
                Size = new Vector2i(64, 64),
                StartVisible = false,
                StartFocused = false,
                API = ContextAPI.OpenGL,
                Profile = ContextProfile.Core,
                APIVersion = new Version(4, 1)
            };

            using var window = new GameWindow(gws, nws);
            window.MakeCurrent();

            // GL init once
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Keep simulator alive (lets it keep GL resources warm)
            var sim = new ArraySimulator();

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            while (true)
            {
                // Read next message length (4 bytes). EOF => exit.
                if (!TryReadInt32(stdin, out var n))
                    break;

                var reqBytes = ReadExactly(stdin, n);
                var req = JsonSerializer.Deserialize<SimulationRequest>(reqBytes, JsonOpts)
                          ?? throw new ArgumentException("Invalid request JSON");

                // Run sim
                var input = SimulationBuilder.BuildInput(req);
                var step  = sim.Simulate(input);
                var resp  = SimulationBuilder.ToResponse(step);

                // IMPORTANT: if you add caching, DO NOT dispose cached assets here.
                input.Array.LayoutTexture?.Dispose();

                // Write response as length-prefixed JSON
                var respBytes = JsonSerializer.SerializeToUtf8Bytes(resp);
                WriteInt32(stdout, respBytes.Length);
                stdout.Write(respBytes);
                stdout.Flush();

                // Keep OpenTK event loop alive (light pump)
                window.ProcessEvents(10);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static bool TryReadInt32(Stream s, out int value)
    {
        Span<byte> b = stackalloc byte[4];
        int got = 0;
        while (got < 4)
        {
            int r = s.Read(b.Slice(got));
            if (r == 0) { value = default; return false; }
            got += r;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(b);
        return true;
    }

    static byte[] ReadExactly(Stream s, int len)
    {
        var buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int r = s.Read(buf, off, len - off);
            if (r == 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }

    static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }
}