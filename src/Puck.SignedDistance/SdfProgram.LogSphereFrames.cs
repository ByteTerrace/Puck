using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // A log-sphere fold's shell boundaries are spheres about its local origin. When every operation the fold's chain
    // applies before it is a translation, a rotation or a uniform scale, optionally after one dynamic transform at the
    // chain's head, they are spheres in world space too, and a march can cross them exactly (sdfMarchAdvance). This
    // pass writes the fold's Data1: xyz the local origin in the chain head's frame (world, or the dynamic slot's), w one
    // when the shells are world spheres, zero (the builder's value) when the chain warps them, so the march crosses
    // them by its acceptance distance instead. The shader reads it in SDF_OP_LOG_SPHERE.
    private void BakeLogSphereFrames() {
        var chainStart = 0;

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op == SdfOp.ResetPoint) {
                chainStart = (index + 1);
            } else if (instruction.Op == SdfOp.LogSphere) {
                var frame = LogSphereFrameOf(chainStart: chainStart, fold: index);

                m_instructions[index] = (instruction with {
                    Data1 = ((frame is { } center) ? new Vector4(value: center, w: 1f) : Vector4.Zero),
                });
            }
        }
    }
    // The fold's local origin in the chain head's frame, or null when the chain before the fold is not a similarity.
    private Vector3? LogSphereFrameOf(int chainStart, int fold) {
        var head = chainStart;

        if ((head < fold) && (m_instructions[head].Op == SdfOp.TransformDynamic)) {
            head++;
        }
        for (var index = head; (index < fold); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.ShapeBlend:
                    break;
                case SdfOp.Scale: {
                        var scale = Vector3.Abs(value: new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z));

                        if ((scale.X != scale.Y) || (scale.X != scale.Z) || (instruction.Data0.W != scale.X)) {
                            return null;
                        }
                        break;
                    }
                default:
                    return null;
            }
        }
        var center = Vector3.Zero;

        // Undo the chain from the fold back to its head: the shader maps a point forward by p - t, the inverse rotation
        // and p / s, so the local origin maps back by s, the rotation and + t.
        for (var index = (fold - 1); (index >= head); index--) {
            var data = m_instructions[index].Data0;

            switch (m_instructions[index].Op) {
                case SdfOp.Translate:
                    center += new Vector3(x: data.X, y: data.Y, z: data.Z);
                    break;
                case SdfOp.Rotate:
                    center = Vector3.Transform(rotation: new Quaternion(w: data.W, x: data.X, y: data.Y, z: data.Z), value: center);
                    break;
                case SdfOp.Scale:
                    center *= new Vector3(x: data.X, y: data.Y, z: data.Z);
                    break;
            }
        }

        return center;
    }
}
