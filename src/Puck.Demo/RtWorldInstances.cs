using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>One ray-tracing world instance: a world-space axis-aligned bound for a single SDF primitive, expressed
/// as a center and a per-axis half-extent (the unit-AABB BLAS is scaled by the half-extent and placed at the
/// center). The <see cref="CustomIndex"/> is reported back by ray queries that hit it.</summary>
/// <param name="Center">The world-space center of the primitive's bound.</param>
/// <param name="HalfExtent">The per-axis half-extent of the primitive's bound (already smooth-blend padded).</param>
/// <param name="CustomIndex">The primitive's index among the emitted instances.</param>
internal readonly record struct RtWorldInstance(Vector3 Center, Vector3 HalfExtent, uint CustomIndex);

/// <summary>
/// Derives ray-tracing TLAS instances from an <see cref="SdfProgram"/> — one world-space AABB per FINITE primitive
/// (the infinite ground plane is skipped). It walks the typed instruction stream tracking the point translation the
/// VM accumulates, so a primitive authored as <c>ResetPoint().Translate(c).Shape(...)</c> lands at world center
/// <c>c</c> with a bound sized from the shape's dimensions plus its smooth-blend padding.
/// <para>
/// This is the demo scene's instance-decomposition answer (the plan's open question #1). It handles the transforms
/// the demo uses precisely — <c>ResetPoint</c> and <c>Translate</c> — and is deliberately CONSERVATIVE for the
/// transforms it does not model exactly (rotation falls back to an isotropic bounding-sphere extent; repeat/symmetry
/// tile or mirror without bound, so a primitive under them is skipped rather than under-bounded). A loose bound only
/// costs a few wasted ray-box tests; an under-bound would drop geometry.
/// </para>
/// </summary>
internal static class RtWorldInstances {
    /// <summary>Extracts the scene's infinite ground plane (the first <c>Plane</c> primitive) in world space, as a
    /// <c>(normal.xyz, offset)</c> vector where the surface is <c>dot(p, normal) + offset = 0</c>; returns
    /// <see cref="Vector4.Zero"/> when the scene has no plane. The ray-query kernel uses it to bound the march start
    /// for floor rays (the plane is infinite, so it is not a TLAS instance). Tracks the translate accumulator so a
    /// translated plane's world offset is correct.</summary>
    /// <param name="program">The SDF program to scan.</param>
    /// <returns>The world-space ground plane, or <see cref="Vector4.Zero"/> if none.</returns>
    public static Vector4 ExtractGroundPlane(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        var center = Vector3.Zero;

        foreach (var instruction in program.Instructions) {
            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                    center = Vector3.Zero;

                    break;
                }
                case SdfOp.Translate: {
                    center += new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z);

                    break;
                }
                case SdfOp.ShapeBlend when ((SdfShapeType)instruction.Shape == SdfShapeType.Plane): {
                    var normal = Vector3.Normalize(value: new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z));
                    // A translate shifts the point by -center, so the world offset gains -dot(center, normal).
                    var offset = (instruction.Data0.W - Vector3.Dot(vector1: center, vector2: normal));

                    return new Vector4(normal, offset);
                }
                default: {
                    break;
                }
            }
        }

        return Vector4.Zero;
    }

    /// <summary>Extracts the finite-primitive world bounds from a program, in instruction order.</summary>
    /// <param name="program">The SDF program to decompose.</param>
    /// <returns>The derived instances, each carrying its center, padded half-extent, and custom index.</returns>
    public static IReadOnlyList<RtWorldInstance> Extract(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        var instances = new List<RtWorldInstance>();
        var center = Vector3.Zero;
        var isotropic = false; // a transform we do not model exactly is active → bound by a sphere, not an AABB
        var skip = false;      // a repeat/symmetry tiles without bound → the next shape cannot be a single instance

        foreach (var instruction in program.Instructions) {
            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                    center = Vector3.Zero;
                    isotropic = false;
                    skip = false;

                    break;
                }
                case SdfOp.Translate: {
                    center += new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z);

                    break;
                }
                case SdfOp.Rotate:
                case SdfOp.Scale: {
                    // Not modeled exactly here — fall back to an isotropic (bounding-sphere) extent so the bound
                    // stays conservative regardless of the orientation/scale.
                    isotropic = true;

                    break;
                }
                case SdfOp.Repeat:
                case SdfOp.RepeatLimited:
                case SdfOp.SymmetryX:
                case SdfOp.SymmetryY:
                case SdfOp.SymmetryZ: {
                    // These produce many/mirrored copies; a single instance cannot represent them.
                    skip = true;

                    break;
                }
                case SdfOp.ShapeBlend: {
                    if (!skip && TryShapeHalfExtent(instruction: instruction, isotropic: isotropic, halfExtent: out var halfExtent)) {
                        instances.Add(item: new RtWorldInstance(
                            Center: center,
                            CustomIndex: (uint)instances.Count,
                            HalfExtent: halfExtent
                        ));
                    }

                    break;
                }
                default: {
                    break;
                }
            }
        }

        return instances;
    }

    // The world half-extent for a shape, padded by its smooth-blend radius (Data1.x). Returns false for shapes with
    // no finite bound (the plane). When isotropic, the extent is the bounding-sphere radius on every axis.
    private static bool TryShapeHalfExtent(SdfInstruction instruction, bool isotropic, out Vector3 halfExtent) {
        var smooth = MathF.Max(0f, instruction.Data1.X);
        var data = instruction.Data0;

        halfExtent = Vector3.Zero;

        switch ((SdfShapeType)instruction.Shape) {
            case SdfShapeType.Box:
            case SdfShapeType.ScreenSlab: {
                // Data0 = (halfX, halfY, halfZ, roundingRadius).
                halfExtent = new Vector3(data.X, data.Y, data.Z) + new Vector3(data.W + smooth);

                break;
            }
            case SdfShapeType.Sphere: {
                // Data0.x = radius.
                halfExtent = new Vector3(data.X + smooth);

                break;
            }
            case SdfShapeType.Torus: {
                // Data0 = (majorRadius, minorRadius, _, _); the ring lies in the XZ plane.
                var ring = (data.X + data.Y + smooth);

                halfExtent = new Vector3(ring, (data.Y + smooth), ring);

                break;
            }
            case SdfShapeType.RoundCone: {
                // Data0 = (lowerRadius, upperRadius, height, _); conservative upright bound.
                var radius = (MathF.Max(data.X, data.Y) + smooth);

                halfExtent = new Vector3(radius, ((data.Z * 0.5f) + radius), radius);

                break;
            }
            case SdfShapeType.Plane:
            default: {
                // Infinite (or unknown): no finite bound — skip.
                return false;
            }
        }

        if (isotropic) {
            halfExtent = new Vector3(MathF.Max(halfExtent.X, MathF.Max(halfExtent.Y, halfExtent.Z)));
        }

        return true;
    }
}
