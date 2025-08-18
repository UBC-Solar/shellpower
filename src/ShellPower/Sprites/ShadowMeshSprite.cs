using System;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace SSCP.ShellPower
{
    /// <summary>
    /// Renders the mesh plus its shadow outline/volume using the
    /// camera + shader already set up by ArrayModelControl.
    /// </summary>
    public sealed class ShadowMeshSprite : Sprite
    {
        // Public knobs
        public Vector3 Position { get; set; } = Vector3.Zero;
        public bool ShowShadowVolume { get; set; } = true;
        public bool ShowShadowOutline { get; set; } = true;

        // Inputs
        public Mesh Mesh { get; }
        public Shadow Shadow { get; }

        // Helpful for callers (e.g., to size camera/zoom)
        public Quad3 BoundingBox => Mesh.BoundingBox; // proxies underlying mesh AABB (Min/Max)

        public ShadowMeshSprite(Shadow shadow)
        {
            Shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
            Mesh   = shadow.Mesh ?? throw new ArgumentNullException(nameof(shadow.Mesh));
        }

        // --------- sprite transform hooks (called by ArrayModelControl) ----------
        public override void PushTransform()
        {
            // Compatibility (matrix stack). If you move to core profile later,
            // replace with your own MVP multiplication path.
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.Translate(Position.X, Position.Y, Position.Z);
        }

        public override void PopTransform()
        {
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();
        }

        // --------- high-level render entrypoints ----------
        public override void RenderMesh()
        {
            // Draw solid mesh; color is irrelevant (the control’s shader shades via gl_Normal)
            GL.Begin(BeginMode.Triangles);
            for (int i = 0; i < Mesh.triangles.Length; i++)
            {
                var tri = Mesh.triangles[i];

                // A
                var nA = Mesh.normals[tri.vertexA];
                var pA = Mesh.points[tri.vertexA];
                GL.Normal3(nA.X, nA.Y, nA.Z);
                GL.Vertex3(pA.X, pA.Y, pA.Z);

                // B
                var nB = Mesh.normals[tri.vertexB];
                var pB = Mesh.points[tri.vertexB];
                GL.Normal3(nB.X, nB.Y, nB.Z);
                GL.Vertex3(pB.X, pB.Y, pB.Z);

                // C
                var nC = Mesh.normals[tri.vertexC];
                var pC = Mesh.points[tri.vertexC];
                GL.Normal3(nC.X, nC.Y, nC.Z);
                GL.Vertex3(pC.X, pC.Y, pC.Z);
            }
            GL.End();
        }

        public void RenderShadowVolume()
        {
            if (!ShowShadowVolume) return;

            // no upward shadows
            if (Shadow.Light.Y <= 0f) return;

            // floor Y coordinate (account for sprite Position)
            float minY = Mesh.points.Min(p => p.Y) + Position.Y;

            // project edges along light to the floor (Y = minY)
            var L = Shadow.Light; // OpenTK.Mathematics.Vector4 expected
            float Ly = MathF.Abs(L.Y) < 1e-8f ? 1e-8f : L.Y;

            // Unlit translucent blue-ish; lighting not used by our simple fragment path
            GL.Disable(EnableCap.Lighting);
            GL.Color4(0f, 0f, 1f, 0.4f);

            foreach (var edge in Shadow.SilhouetteEdges)
            {
                var p0 = Mesh.points[edge.First];
                var p1 = Mesh.points[edge.Second];

                // in world (add Position)
                var w0 = new Vector3(p0.X + Position.X, p0.Y + Position.Y, p0.Z + Position.Z);
                var w1 = new Vector3(p1.X + Position.X, p1.Y + Position.Y, p1.Z + Position.Z);

                // project to floor along light
                float k0 = (w0.Y - minY) / Ly;
                float k1 = (w1.Y - minY) / Ly;
                var w0b = new Vector3(w0.X - L.X * k0, w0.Y - L.Y * k0, w0.Z - L.Z * k0);
                var w1b = new Vector3(w1.X - L.X * k1, w1.Y - L.Y * k1, w1.Z - L.Z * k1);

                // triangle strip (p0, p1, p0b, p1b)
                GL.Begin(BeginMode.TriangleStrip);
                GL.Vertex3(w0.X,  w0.Y,  w0.Z);
                GL.Vertex3(w1.X,  w1.Y,  w1.Z);
                GL.Vertex3(w0b.X, w0b.Y, w0b.Z);
                GL.Vertex3(w1b.X, w1b.Y, w1b.Z);
                GL.End();
            }

            GL.Enable(EnableCap.Lighting);
        }

        public void RenderShadowOutline()
        {
            if (!ShowShadowOutline) return;
            if (Shadow.Light.Y <= 0f) return;

            GL.Disable(EnableCap.Lighting);
            GL.Color3(1f, 0f, 0f);

            GL.Begin(BeginMode.Lines);
            foreach (var edge in Shadow.SilhouetteEdges)
            {
                var a = Mesh.points[edge.First];
                var b = Mesh.points[edge.Second];
                GL.Vertex3(a.X + Position.X, a.Y + Position.Y, a.Z + Position.Z);
                GL.Vertex3(b.X + Position.X, b.Y + Position.Y, b.Z + Position.Z);
            }
            GL.End();

            GL.Enable(EnableCap.Lighting);
        }

        // For callers that prefer a one-call entrypoint:
        public void Render()
        {
            RenderMesh();
            RenderShadowVolume();
            RenderShadowOutline();
        }
    }
}
