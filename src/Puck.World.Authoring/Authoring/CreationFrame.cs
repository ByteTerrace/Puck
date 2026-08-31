using System.Numerics;

namespace Puck.World.Authoring;

/// <summary>
/// THE one place a <c>puck.creation.v1</c> document crosses between the author's frame and the engine's — every
/// position, rotation, and camera offset in a creation document is authored in the AUTHOR frame (+Y up, +Z the
/// front a shape faces, +X screen-right when looking at that front — the sculpt workbench's own view), a 180° yaw
/// about +Y away from the engine's own frame (+Y up, −Z forward, +X right facing −Z). <see cref="ToEngine"/> converts
/// an authored document for rendering, collision, and anchor resolution; <see cref="ToAuthor"/> inverts it back for
/// echo/save. Both directions are the SAME transform — a half turn is its own inverse — so one private helper serves
/// both names. The engine's own frame, and every document OTHER than <c>puck.creation.v1</c>, is untouched; nothing
/// downstream of this class ever names either frame again.
/// </summary>
/// <remarks>The yaw is applied as an exact axis swap/negate (never <see cref="Quaternion.CreateFromAxisAngle"/>'s
/// sin/cos of π), so it is bit-exact regardless of whether the caller composes it in float (render/anchor
/// consumers) or lets it flow into a downstream <c>FixedQ4816</c>/<c>FixedQuaternion</c> conversion (collider
/// compilation): negation and the (0,1,0,0) quaternion pre-multiply are both exact under
/// <see cref="Puck.Maths.FixedQ4816.FromDouble(double)"/> and <see cref="Puck.Maths.FixedQuaternion.FromQuaternion"/>,
/// so converting in float first changes no bit either path would not already produce.</remarks>
public static class CreationFrame {
    // (0,1,0,0): a unit quaternion (axis (0,1,0), angle 180°) built directly rather than through
    // Quaternion.CreateFromAxisAngle, so no sin/cos ever runs. Multiplying any quaternion q by this on the left
    // reduces, term by term, to a component permutation with sign flips (result = (q.Z, q.W, -q.X, -q.Y)) — every
    // step is a copy or a negation, never a lossy multiply-accumulate.
    private static readonly Quaternion Yaw180 = new(
        w: 0f,
        x: 0f,
        y: 1f,
        z: 0f
    );

    private static List<T>? ConvertList<T>(IReadOnlyList<T>? source, Func<T, T> convert) {
        if (source is not { Count: > 0 }) {
            return null;
        }

        var converted = new List<T>(capacity: source.Count);

        foreach (var item in source) {
            converted.Add(item: convert(arg: item));
        }

        return converted;
    }
    private static Vector3 Flip(Vector3 value) => new(
        x: -value.X,
        y: value.Y,
        z: -value.Z
    );
    private static Quaternion Flip(Quaternion value) => Quaternion.Normalize(value: (Yaw180 * value));
    private static CreationDocument Apply(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return document with {
            Cameras = ConvertList(
                source: document.Cameras,
                convert: camera => camera with { Position = Flip(value: camera.Position) }
            ),
            Frames = ConvertList(
                source: document.Frames,
                convert: frame => frame with {
                    Transforms = (ConvertList(
                        source: frame.Transforms,
                        convert: transform => transform with {
                            Position = Flip(value: transform.Position),
                            Rotation = Flip(value: transform.Rotation),
                        }
                    ) ?? []),
                }
            ),
            Shapes = ConvertList(
                source: document.Shapes,
                convert: shape => shape with {
                    Position = Flip(value: shape.Position),
                    Rotation = Flip(value: shape.Rotation),
                    Domain = ConvertList(
                        source: shape.Domain,
                        convert: FlipDomainOp
                    ),
                }
            ),
            // A run riding a shape is expressed in that shape's local frame, which the shape's own conversion carries.
            TextRuns = ConvertList(
                source: document.TextRuns,
                convert: run => ((run.ShapeId is null)
                    ? run with {
                        Position = Flip(value: run.Position),
                        Rotation = Flip(value: run.Rotation),
                    }
                    : run)
            ),
        };
    }

    /// <summary>Converts an author-frame document to the engine frame — every render, collision, and anchor
    /// consumer's entry point. A creation camera's <see cref="CreationCameraDocument.Yaw"/>/<see cref="CreationCameraDocument.Pitch"/>
    /// offsets and a chain's IK goal/pole are left untouched: nothing in the engine reads them today, and they never
    /// leave the author-frame sculpt session that solves them.</summary>
    /// <param name="document">The author-frame document.</param>
    /// <returns>The equivalent engine-frame document.</returns>
    public static CreationDocument ToEngine(CreationDocument document) => Apply(document: document);

    // Every ShapeDomainOp field other than a Symmetry normal is either a scalar (an offset, a spacing/limit/cell
    // magnitude, a material stride) or an axis/plane-selecting enum — both invariant under Yaw180, a diag(-1,1,-1)
    // proper rotation: it negates two axes without swapping which axis is which, so "the X axis"/"the XZ plane"
    // still names the same axis/plane, and a plane's signed offset along its (now-flipped) normal is unchanged (the
    // substitution dot(M(p), n) = dot(p, M(n)) holds because M is its own transpose). Only a direction — the
    // Symmetry normal — needs the same Flip a position or rotation gets.
    private static ShapeDomainOp FlipDomainOp(ShapeDomainOp op) => ((op is ShapeDomainOp.Symmetry symmetry)
        ? (symmetry with { Normal = Flip(value: symmetry.Normal) })
        : op
    );

    /// <summary>Converts an engine-frame document back to the author frame — the echo/save boundary. The identical
    /// transform to <see cref="ToEngine"/>: a 180° yaw is its own inverse.</summary>
    /// <param name="document">The engine-frame document.</param>
    /// <returns>The equivalent author-frame document.</returns>
    public static CreationDocument ToAuthor(CreationDocument document) => Apply(document: document);
}
