using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client.Sdf;

namespace Puck.World.Client;

/// <summary>
/// A first-party <c>puck.sdf.v1</c> geometry document, loaded through the <c>world.sdf.load</c> console verb and
/// composed as its own <see cref="ISdfSceneEmitter"/> beside <c>WorldSceneEmitter</c> (see
/// <see cref="SdfCompositionFrameSource"/> — the same live seam <c>WorldSceneEmitter</c> already exercises,
/// never resurrected). Static world-set geometry only: no dynamic transforms, no screens, no instances — those axes
/// (and the per-contributor cost ledger a document-driven capacity model would need) are explicitly out of scope
/// for this prototype; see <see cref="SdfDocumentDecoder"/>'s remarks for the full covered/skipped op list.
/// </summary>
/// <remarks>
/// <see cref="Emit"/> under <see cref="SdfEmitContext.Probe"/> reserves a fixed worst case —
/// <see cref="SdfDocumentDecoder.MaxOps"/> instructions and <see cref="SdfDocumentDecoder.MaxMaterials"/> materials —
/// rather than a per-document ledger (deliberately out of scope; see the front door's report). Because
/// <see cref="SdfDocumentDecoder.Decode"/> refuses any document declaring more ops or materials than those same two
/// constants, a document that loads successfully in isolation can never outgrow what this probe already reserved for
/// this emitter alone.
/// <para>
/// The composed program's ceilings are shared with the world scene, so <see cref="Load"/> also runs the composed
/// admission check <see cref="ConfigureAdmission"/> installs: the incoming document's materials counted beside the
/// scene the live world definition draws (<see cref="WorldSceneEnvelope"/>), before committing. Refused there, the
/// previously loaded document (if any) keeps rendering unchanged, exactly like every other refusal this door can
/// produce.
/// </para>
/// </remarks>
public sealed class WorldSdfDocumentEmitter : ISdfSceneEmitter {
    private Func<SdfDocumentProgram, string?>? m_refuseComposed;
    private SdfDocumentProgram? m_program;
    private int m_revision;

    /// <summary>The currently loaded document's content hash (FNV-1a over its received UTF-8 bytes, computed before
    /// decode), or <see langword="null"/> when no document has loaded successfully yet.</summary>
    public ulong? ContentHash => m_program?.ContentHash;
    /// <summary>The currently loaded document (or <see langword="null"/> when none has loaded successfully yet), whose
    /// materials the world scene's count adds (<see cref="WorldSceneEnvelope.DocumentMaterials"/>).</summary>
    public SdfDocumentProgram? CurrentProgram => m_program;
    /// <inheritdoc/>
    public bool OwnsMaterialScope => true;
    /// <inheritdoc/>
    public int RevisionComponentCount => 1;

    // THE ONE construction-time worst case (never rendered): SdfDocumentDecoder.MaxMaterials materials, then
    // (SdfDocumentDecoder.MaxOps + 1) consecutive BARE ResetPoints — not a reset+translate+sphere group.
    //
    // SdfProgram.AnalyzeBounds starts a new segment at every ResetPoint, and every segment — with or without a
    // shape — buys a segment-directory row
    // (2 uvec4), a world-segment-list entry (1 uvec4; this emitter never declares instances, so every segment is a
    // world segment), and a rigid-plan directory row (1 uvec4) = 4 uvec4/segment; a shape inside a segment ALSO buys
    // a rigid-leaf record (3 uvec4) but never buys another segment. So at a FIXED total instruction count (the
    // document's op budget is capped, never the composition's choice), swapping one "shape" op for one "reset" op
    // costs +4 and saves -3 uvec4 — a net +1 per swap — so the reservation-maximizing document is the one with NO
    // shapes at all: (MaxOps + 1) resets (the +1 is Replay's own leading reset, which every live document pays too)
    // yields (MaxOps + 1) segments and 0 leaves, at 4·(MaxOps + 1) uvec4 — strictly more than ANY reset/shape mix a
    // legal document could produce at the same op budget, including one built from an unbounded shape (a plane's
    // segment never merges with a neighbour, but it still costs only the ordinary 4 + 3 = 7 uvec4/segment a bounded
    // shape does). No document can buy a segment cheaper than one Reset, and none can exceed MaxOps ops, so this
    // dominates every legal document unconditionally. MaxMaterials materials dominates every legal document's
    // material cost independently of ops.
    private static void EmitProbeReservation(SdfProgramBuilder builder) {
        for (var index = 0; (index < SdfDocumentDecoder.MaxMaterials); index++) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        }

        var reservedInstructions = (SdfDocumentDecoder.MaxOps + 1);

        for (var index = 0; (index < reservedInstructions); index++) {
            _ = builder.ResetPoint();
        }
    }

    /// <summary>Installs the composed admission check <see cref="Load"/> runs before committing a document.
    /// Unconfigured, <see cref="Load"/> skips it.</summary>
    /// <param name="refuse">Counts a candidate document composed with the live world scene and names the engine
    /// ceiling it exceeds, or returns <see langword="null"/> when it fits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="refuse"/> is <see langword="null"/>.</exception>
    public void ConfigureAdmission(Func<SdfDocumentProgram, string?> refuse) {
        ArgumentNullException.ThrowIfNull(argument: refuse);

        m_refuseComposed = refuse;
    }
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (context.Probe) {
            EmitProbeReservation(builder: builder);

            return;
        }

        if (m_program is { } program) {
            SdfDocumentDecoder.Replay(
                builder: builder,
                program: program
            );
        }
    }
    /// <summary>Decodes, dry-validates, and — only on success — composes <paramref name="utf8Json"/> as the live
    /// document, replacing whatever was loaded before. An invalid document (structurally, or because it would
    /// overflow the composed render envelope) leaves the previously loaded one (if any) rendering unchanged; nothing
    /// is ever partially applied.</summary>
    /// <param name="utf8Json">The document's raw UTF-8 bytes, exactly as received from its source file (the hash
    /// identity is computed over these bytes, before any decoding).</param>
    /// <returns>The op count, material count, and content hash of what loaded.</returns>
    /// <exception cref="SdfDocumentException">The document is structurally invalid, names an unknown op or enum
    /// value, a decoded call the builder itself refuses (surfaced with the refusing op's index and name), or —
    /// composed with the CURRENT live world definition — would exceed the active renderer's limits.</exception>
    public (int Ops, int Materials, ulong Hash) Load(ReadOnlyMemory<byte> utf8Json) {
        var program = SdfDocumentDecoder.Decode(utf8Json: utf8Json);

        // Dry-run against a THROWAWAY builder, in its own material scope (the scope the composition wraps this emitter
        // in) — every builder throw the document could ever trigger surfaces HERE, at load time, rather than
        // silently waiting for the next composition rebuild to discover it. ISOLATED on purpose: this catches a
        // structurally-broken document (a builder-level refusal) regardless of what else is loaded; it says nothing
        // about capacity, which the composed check below owns.
        try {
            _ = SdfProgramMeasure.Measure(emit: probeBuilder => {
                using (probeBuilder.BeginMaterialScope()) {
                    SdfDocumentDecoder.Replay(
                        builder: probeBuilder,
                        program: program
                    );
                }
            });
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            // A PROGRAM-level pack refusal (no single op owns it - e.g. an uncontainable cellJitter prototype) must
            // reach world.sdf.load as a document rejection, never an unhandled throw.
            throw new SdfDocumentException(
                message: exception.Message,
                reason: SdfRefusal.BuilderRejectedProgram
            );
        }

        // Composed with the scene the live world draws, before commit: a document that validates alone can still spend
        // what the composed program's ceilings leave.
        if (m_refuseComposed?.Invoke(arg: program) is { } refusal) {
            throw new SdfDocumentException(
                message: $"document: composed with the live world scene, {refusal} — the previously loaded document (if any) keeps rendering unchanged.",
                reason: SdfRefusal.ComposedEnvelopeExceeded
            );
        }

        m_program = program;
        m_revision++;

        return (program.Ops.Count, program.Materials.Count, program.ContentHash);
    }
    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) => destination[0] = m_revision;
}
