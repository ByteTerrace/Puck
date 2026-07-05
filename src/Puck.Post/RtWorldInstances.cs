using System.Numerics;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>One ray-tracing world instance: a world-space axis-aligned bound for a single SDF primitive, expressed
/// as a center and a per-axis half-extent (the unit-AABB BLAS is scaled by the half-extent and placed at the
/// center). The <see cref="CustomIndex"/> is reported back by ray queries that hit it.</summary>
/// <param name="Center">The world-space center of the primitive's bound.</param>
/// <param name="HalfExtent">The per-axis half-extent of the primitive's bound (already smooth-blend padded).</param>
/// <param name="CustomIndex">The primitive's index among the emitted instances.</param>
internal readonly record struct RtWorldInstance(Vector3 Center, Vector3 HalfExtent, uint CustomIndex);

/// <summary>
/// Derives ray-tracing TLAS instances from an <see cref="SdfProgram"/> — one world-space AABB per FINITE primitive
/// (the infinite ground plane is skipped). This is now the ONE implementation: it was ported from the demo's
/// <c>RtWorldInstances</c>, and that copy retired with the demo's live <c>--world-rt</c> producer. It walks the typed
/// instruction stream tracking the point translation the VM accumulates, so a primitive authored as
/// <c>ResetPoint().Translate(c).Shape(...)</c> lands at world center <c>c</c> with a bound sized from the shape's
/// dimensions plus its smooth-blend padding. It is deliberately CONSERVATIVE for the transforms it does not model
/// exactly: rotation/scale fall back to an isotropic bounding-sphere extent, and any op that tiles, mirrors, or
/// re-poses copies (repeat/symmetry/wallpaper/dynamic transforms — and every unknown FUTURE op, by default) routes
/// the shapes after it into one whole-march-envelope catch-all instance. A loose bound only costs wasted ray-box
/// tests; an under-bound (or the old silent skip, which dropped such geometry from the RT image entirely) would be
/// wrong.
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

    /// <summary>The catch-all instance's per-axis half-extent: the march envelope (KEEP IN SYNC with
    /// <c>MaxDistance</c> in <c>sdf-world.hlsli</c>), so every camera ray enters it at ~TMin and marches the full
    /// field — slower, never wrong.</summary>
    private const float CatchAllHalfExtent = 60f;

    /// <summary>Extracts the finite-primitive world bounds from a program, in instruction order. A shape whose chain
    /// holds an op no single AABB can represent (repeat/symmetry/wallpaper tile or mirror copies; a dynamic transform
    /// can be anywhere per frame; any FUTURE op lands in the conservative default) emits a whole-march-envelope
    /// CATCH-ALL instance instead of silently vanishing: rays over such geometry march from ~0 — slower, never
    /// dropped.</summary>
    /// <param name="program">The SDF program to decompose.</param>
    /// <returns>The derived instances, each carrying its center, padded half-extent, and custom index.</returns>
    public static IReadOnlyList<RtWorldInstance> Extract(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        var instances = new List<RtWorldInstance>();
        var center = Vector3.Zero;
        var isotropic = false;       // a transform we do not model exactly is active → bound by a sphere, not an AABB
        var padding = Vector3.Zero;  // accumulated elongation extents, added to the following shapes' bounds
        var skip = false;            // an op tiles/mirrors/moves without bound → the next shape cannot be a single instance
        var anyUnbounded = false;    // some finite shape was skipped → the catch-all instance must cover it

        foreach (var instruction in program.Instructions) {
            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        center = Vector3.Zero;
                        isotropic = false;
                        padding = Vector3.Zero;
                        skip = false;

                        break;
                    }
                case SdfOp.Translate: {
                        center += new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z);

                        break;
                    }
                case SdfOp.Rotate:
                case SdfOp.Scale:
                case SdfOp.TwistY:
                case SdfOp.BendX:
                case SdfOp.BendY:
                case SdfOp.BendZ: {
                        // Not modeled exactly here — fall back to an isotropic (bounding-sphere) extent so the bound
                        // stays conservative regardless of the orientation/scale. The twist/bends preserve the norm
                        // of the plane pair they rotate, so the bounding sphere survives them.
                        isotropic = true;

                        break;
                    }
                case SdfOp.Elongate: {
                        // Elongation sweeps the shape's cross-section over ±extents: pad the following shapes by them.
                        padding += new Vector3(MathF.Abs(instruction.Data0.X), MathF.Abs(instruction.Data0.Y), MathF.Abs(instruction.Data0.Z));

                        break;
                    }
                case SdfOp.Onion:
                case SdfOp.Dilate: {
                        // FIELD ops thicken/inflate the ENTIRE field accumulated so far — retroactively pad every
                        // instance already emitted (dilate pushes the surface out by r; onion's skin reaches abs(d)−t
                        // ≤ 0, i.e. t outward).
                        var inflate = new Vector3(MathF.Max(0f, instruction.Data0.X));

                        for (var index = 0; (index < instances.Count); index++) {
                            var instance = instances[index];

                            instances[index] = instance with { HalfExtent = (instance.HalfExtent + inflate) };
                        }

                        break;
                    }
                case SdfOp.ShapeBlend: {
                        if (skip) {
                            // The chain tiles/mirrors/moves this shape beyond one AABB: covered by the catch-all.
                            anyUnbounded |= ((SdfShapeType)instruction.Shape != SdfShapeType.Plane);
                        } else if (TryShapeHalfExtent(instruction: instruction, isotropic: isotropic, halfExtent: out var halfExtent)) {
                            // Elongation padding stretches the bound; under an isotropic fallback the padded box
                            // re-collapses to its bounding sphere (a rotate may reorient the elongation).
                            halfExtent += padding;

                            if (isotropic && (padding != Vector3.Zero)) {
                                halfExtent = new Vector3(MathF.Max(halfExtent.X, MathF.Max(halfExtent.Y, halfExtent.Z)));
                            }

                            instances.Add(item: new RtWorldInstance(
                                Center: center,
                                CustomIndex: (uint)instances.Count,
                                HalfExtent: halfExtent
                            ));
                        }

                        break;
                    }
                default: {
                        // Repeat/RepeatLimited/Symmetry* tile or mirror copies, TransformDynamic moves per frame,
                        // WallpaperFold tiles a lattice — and any FUTURE op is conservatively assumed to do the same:
                        // a single instance cannot represent the shapes that follow. Skip-by-default replaces the old
                        // silent ignore (an unknown op used to leave the bound WRONG rather than loose).
                        skip = true;

                        break;
                    }
            }
        }

        // The vanish fix: without this, geometry under a skipped op got NO instance and rays over it read SKY. One
        // march-envelope box restores correctness for all of it at march-from-zero cost.
        if (anyUnbounded) {
            instances.Add(item: new RtWorldInstance(
                Center: Vector3.Zero,
                CustomIndex: (uint)instances.Count,
                HalfExtent: new Vector3(CatchAllHalfExtent)
            ));
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
                    // Data0 = (lowerRadius, upperRadius, height, _); the shape spans y in
                    // [-lowerRadius, height + upperRadius] from its LOCAL ORIGIN (not centered), so a bound symmetric
                    // about the center must reach the full height both ways — up to 2x loose, but conservative.
                    var radius = (MathF.Max(data.X, data.Y) + smooth);

                    halfExtent = new Vector3(radius, (data.Z + radius), radius);

                    break;
                }
            case SdfShapeType.Capsule: {
                    // Data0 = (endX, endY, endZ, radius); the segment runs from the local origin to the endpoint, so a
                    // bound symmetric about the center must reach |endpoint| both ways — up to 2x loose, but conservative.
                    halfExtent = (new Vector3(MathF.Abs(data.X), MathF.Abs(data.Y), MathF.Abs(data.Z)) + new Vector3(data.W + smooth));

                    break;
                }
            case SdfShapeType.Cylinder: {
                    // Data0 = (radius, halfHeight, _, _); upright and centered — exact.
                    halfExtent = new Vector3((data.X + smooth), (data.Y + smooth), (data.X + smooth));

                    break;
                }
            case SdfShapeType.Ellipsoid: {
                    // Data0 = (radiusX, radiusY, radiusZ, _); centered — exact.
                    halfExtent = (new Vector3(data.X, data.Y, data.Z) + new Vector3(smooth));

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
