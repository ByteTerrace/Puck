using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client.Sdf;

/// <summary>Thrown by <see cref="SdfDocumentDecoder"/> for a structurally invalid <c>puck.sdf.v1</c> document
/// (an unknown schema tag, member, op name, or enum name; a duplicate key; a material reference outside the
/// document's own palette; a field-scope imbalance; a declared array past this prototype's fixed reservation) and,
/// wrapped with the refusing op's index and name, for any exception a decoded builder call itself throws — the
/// front door's whole validation surface, structural and inherited alike, surfaces through this one type.
/// <see cref="Reason"/> is REQUIRED (not optional) — every throw site names one of the finite <see cref="SdfRefusal"/>
/// members, so there is no code path that raises this exception with a reason <c>world.refusals</c>' catalog (which
/// reads that enum) would not already list.</summary>
/// <param name="reason">Which of this door's finite refusal reasons fired.</param>
/// <param name="message">The refusal, with enough document context (member path, or op index/name) to locate the
/// offending part of the source file.</param>
internal sealed class SdfDocumentException(SdfRefusal reason, string message) : Exception(message) {
    /// <summary>Which of this door's finite refusal reasons fired.</summary>
    public SdfRefusal Reason { get; } = reason;
}

/// <summary>One decoded <c>puck.sdf.v1</c> op — the typed argument record <see cref="SdfDocumentDecoder"/> produces
/// per array entry, replayed against an <see cref="SdfProgramBuilder"/> by <see cref="SdfDocumentDecoder.Replay"/>.
/// Which fields are meaningful depends on <see cref="Kind"/> (see the decoder's per-kind allowed-member tables);
/// unused fields carry their default.</summary>
/// <param name="Index">This op's position in the document's <c>ops</c> array — the context a refusal names.</param>
/// <param name="Kind">Which builder call this op replays as.</param>
/// <param name="Vector0">A translation offset, rotation axis, scale, box half-extent, capsule endpoint, or plane
/// normal, depending on <paramref name="Kind"/>.</param>
/// <param name="Scalar0">A rotation angle (degrees), sphere/cylinder/torus radius, box/capsule round radius, or
/// plane offset, depending on <paramref name="Kind"/>.</param>
/// <param name="Scalar1">A cylinder half-height or torus minor radius, depending on <paramref name="Kind"/>.</param>
/// <param name="Material">The shape's material index into <see cref="SdfDocumentProgram.Materials"/> (shape kinds
/// only) — a document ordinal, translated to the live builder's material id at <see cref="SdfDocumentDecoder.Replay"/>
/// time (the document has no spelling for an absolute id; see <see cref="SdfProgramBuilder.BeginMaterialScope"/>).</param>
/// <param name="Blend">The compose blend (shape and <see cref="SdfDocumentOpKind.Push"/> kinds).</param>
/// <param name="Smooth">The blend/compose smooth radius (shape and <see cref="SdfDocumentOpKind.Push"/> kinds).</param>
internal sealed record SdfDocumentOp(
    int Index,
    SdfDocumentOpKind Kind,
    Vector3 Vector0 = default,
    float Scalar0 = 0f,
    float Scalar1 = 0f,
    int Material = 0,
    SdfBlendOp Blend = SdfBlendOp.Union,
    float Smooth = 0f
);

/// <summary>The <c>puck.sdf.v1</c> op vocabulary this prototype decoder covers — see the front door's report for the
/// full skipped-op list (glyph/text, the positional-recolor folds, screens, instances, sampled regions, and every
/// warp/bend/repeat op).</summary>
internal enum SdfDocumentOpKind {
    /// <summary><see cref="SdfProgramBuilder.ResetPoint"/> — no fields.</summary>
    Reset,
    /// <summary><see cref="SdfProgramBuilder.Translate"/> — <see cref="SdfDocumentOp.Vector0"/> is the offset.</summary>
    Translate,
    /// <summary><see cref="SdfProgramBuilder.Rotate"/> — <see cref="SdfDocumentOp.Vector0"/> is the axis (normalized
    /// at replay), <see cref="SdfDocumentOp.Scalar0"/> the angle in degrees.</summary>
    Rotate,
    /// <summary><see cref="SdfProgramBuilder.Scale"/> — <see cref="SdfDocumentOp.Vector0"/> is the scale.</summary>
    Scale,
    /// <summary><see cref="SdfProgramBuilder.PushField"/> — <see cref="SdfDocumentOp.Blend"/>/<see cref="SdfDocumentOp.Smooth"/>
    /// are the compose blend/radius. Opens the document's ONE allowed field-scope nesting level (decision 9): a
    /// document's non-union blend is refused OUTSIDE a push/pop pair and freely available inside one.</summary>
    Push,
    /// <summary><see cref="SdfProgramBuilder.PopField"/> — no fields.</summary>
    Pop,
    /// <summary><see cref="SdfProgramBuilder.Sphere"/>.</summary>
    Sphere,
    /// <summary><see cref="SdfProgramBuilder.Box"/>.</summary>
    Box,
    /// <summary><see cref="SdfProgramBuilder.Capsule"/>.</summary>
    Capsule,
    /// <summary><see cref="SdfProgramBuilder.Cylinder"/>.</summary>
    Cylinder,
    /// <summary><see cref="SdfProgramBuilder.Torus"/>.</summary>
    Torus,
    /// <summary><see cref="SdfProgramBuilder.Plane"/>.</summary>
    Plane,
}

/// <summary>A fully decoded, dry-validated <c>puck.sdf.v1</c> document — the immutable, replayable result of
/// <see cref="SdfDocumentDecoder.Decode"/>. Replaying the same program twice against a fresh builder in a fresh
/// material scope produces byte-identical instructions (no wall-clock, no RNG — every value came from the source
/// bytes), so this is what <see cref="WorldSdfDocumentEmitter"/> holds between rebuilds rather than re-parsing JSON
/// every frame.</summary>
/// <param name="Materials">The document's own material palette, in declaration order (ordinal <c>k</c> is what
/// <see cref="SdfDocumentOp.Material"/> indexes).</param>
/// <param name="Ops">The decoded op stream, in document order.</param>
/// <param name="ContentHash">The 64-bit FNV-1a hash of the document's received UTF-8 bytes, computed BEFORE any
/// decoding (decision 7) — identity is over the bytes, never a re-serialization.</param>
internal sealed record SdfDocumentProgram(
    IReadOnlyList<SdfMaterial> Materials,
    IReadOnlyList<SdfDocumentOp> Ops,
    ulong ContentHash
);
