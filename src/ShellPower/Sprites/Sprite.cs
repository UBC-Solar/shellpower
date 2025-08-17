using System.Collections.Generic;
using OpenTK.Mathematics;

namespace SSCP.ShellPower {
    public abstract class Sprite {
        // CPU-side model transform for this sprite
        private Matrix4 transform = Matrix4.Identity;
        public Matrix4 Transform {
            get => transform;
            set => transform = value;
        }

        // Translation convenience (OpenTK uses M41..M43 for translation)
        public Vector4 Position {
            get => new Vector4(transform.M41, transform.M42, transform.M43, transform.M44);
            set {
                transform.M41 = value.X;
                transform.M42 = value.Y;
                transform.M43 = value.Z;
                transform.M44 = value.W == 0 ? 1f : value.W; // keep w sane
            }
        }

        // -------- CPU-side model matrix stack (replacement for GL matrix stack) --------
        private static readonly Stack<Matrix4> s_modelStack = new Stack<Matrix4>(new[] { Matrix4.Identity });

        /// Current composed model matrix (top of stack)
        public static Matrix4 CurrentModel => s_modelStack.Peek();

        /// Emulate your old PushTransform(): multiply current by this sprite's Transform and push
        public void PushTransform() {
            var top = s_modelStack.Peek();
            s_modelStack.Push(top * transform);  // world = world * local
        }

        /// Emulate your old PopTransform(): discard last pushed model
        public void PopTransform() {
            if (s_modelStack.Count > 1) s_modelStack.Pop();
        }

        public virtual void Initialize() { }
        public virtual void Dispose() { }

        // Your geometry entry point(s)
        public abstract void RenderMesh();
        public virtual void RenderShadowOutline() { }
        public virtual void RenderShadowVolume() { }
    }
}