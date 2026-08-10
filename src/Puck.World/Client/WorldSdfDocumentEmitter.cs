using System.Numerics;
using Puck.SdfVm;
using Puck.World.Client.Sdf;

namespace Puck.World.Client;

/// <summary>
/// A first-party <c>puck.sdf.v1</c> geometry document, loaded through the <c>world.sdf.load</c> console verb and
/// composed as its own <see cref="ISdfSceneEmitter"/> beside <see cref="WorldSceneEmitter"/> (see
/// <see cref="SdfCompositionFrameSource"/> — the same live seam <see cref="WorldSceneEmitter"/> already exercises,
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
/// That reservation alone is not enough: the probed envelope is shared with <see cref="WorldSceneEmitter"/> (one
/// combined worst-case build — see <see cref="Puck.World.Client.WorldFrameSource"/>'s constructor), and a live scene
/// mutation may have already spent capacity this emitter's own reservation covers but the scene's did not need at
/// the time. So <see cref="Load"/> also runs a composed admission check (<see cref="Configure"/>) — the same joint
/// measurer <see cref="Puck.World.WorldRenderEnvelope"/> uses for a scene mutation, with the roles swapped: the
/// candidate is the incoming document, composed against the current live world definition — before committing.
/// Refused there, the previously loaded document (if any) keeps rendering unchanged, exactly like every other
/// refusal this door can produce.
/// </para>
/// </remarks>
internal sealed class WorldSdfDocumentEmitter : ISdfSceneEmitter {
    private SdfDocumentProgram? m_program;
    private int m_revision;
    private int m_programWordCapacity;
    private int m_instanceCapacity;
    private Func<SdfDocumentProgram, (int Words, int Instances)>? m_measureComposed;

    /// <inheritdoc/>
    public bool OwnsMaterialScope => true;

    /// <inheritdoc/>
    public int RevisionComponentCount => 1;

    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) => destination[0] = m_revision;

    /// <summary>The currently loaded document's content hash (FNV-1a over its received UTF-8 bytes, computed before
    /// decode), or <see langword="null"/> when no document has loaded successfully yet.</summary>
    public ulong? ContentHash => m_program?.ContentHash;

    /// <summary>The currently loaded document (or <see langword="null"/> when none has loaded successfully yet) —
    /// read by <see cref="Puck.World.Client.WorldFrameSource"/>'s composed measurer so a scene mutation is checked
    /// against whatever this emitter is actually holding, never a stale or assumed value.</summary>
    internal SdfDocumentProgram? CurrentProgram => m_program;

    /// <summary>Records the probed envelope floors and the composed-candidate measurer: given a candidate document,
    /// returns the program-word/instance counts of that document composed alongside the current live world
    /// definition. Configured once by <see cref="Puck.World.Client.WorldFrameSource"/>'s constructor, reusing the
    /// exact same joint-measurement method <see cref="Puck.World.WorldRenderEnvelope"/> uses for a scene mutation
    /// (roles swapped) — see the type remarks. Unconfigured (a load somehow racing startup, or a probe-only test
    /// double), <see cref="Load"/> skips the composed check — the same "unconfigured reads as fits" posture
    /// <see cref="Puck.World.WorldRenderEnvelope"/> documents.</summary>
    /// <param name="programWordCapacity">The probed program-word ceiling (the same frozen floor the scene-mutation
    /// check is measured against).</param>
    /// <param name="instanceCapacity">The probed instance ceiling.</param>
    /// <param name="measureComposed">Composes a candidate document against the current world definition and measures
    /// the result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="measureComposed"/> is <see langword="null"/>.</exception>
    public void Configure(int programWordCapacity, int instanceCapacity, Func<SdfDocumentProgram, (int Words, int Instances)> measureComposed) {
        ArgumentNullException.ThrowIfNull(argument: measureComposed);

        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_measureComposed = measureComposed;
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
    /// composed with the CURRENT live world definition — would exceed the probed render envelope.</exception>
    public (int Ops, int Materials, ulong Hash) Load(ReadOnlyMemory<byte> utf8Json) {
        var program = SdfDocumentDecoder.Decode(utf8Json: utf8Json);

        // Dry-run against a THROWAWAY builder, in its own material scope (mirrors WorldSceneEmitter.ComposeCandidate's
        // own scope) — every builder throw the document could ever trigger surfaces HERE, at load time, rather than
        // silently waiting for the next composition rebuild to discover it. ISOLATED on purpose: this catches a
        // structurally-broken document (a builder-level refusal) regardless of what else is loaded; it says nothing
        // about capacity, which the composed check below owns.
        var probeBuilder = new SdfProgramBuilder();

        using (probeBuilder.BeginMaterialScope()) {
            SdfDocumentDecoder.Replay(builder: probeBuilder, program: program);
        }

        _ = probeBuilder.Build();

        // THE COMPOSED-ADMISSION CHECK (the fix for the asymmetric join): a document that validates alone can still,
        // combined with whatever the live world scene is currently spending, exceed the ONE shared envelope the two
        // sides split — the packed tables a program carries are computed over the WHOLE composed build, not
        // additively per contributor. Measured and refused HERE, before commit, exactly mirroring how a scene
        // mutation is refused against this same envelope rather than discovered later as an UploadProgram throw.
        if (m_measureComposed is { } measure) {
            var (words, instances) = measure(program);

            if (words > m_programWordCapacity) {
                throw new SdfDocumentException(reason: SdfRefusal.ComposedProgramWordsExceeded, message: $"document: composed with the live world scene, program words {words} exceed the probed render envelope {m_programWordCapacity} — the previously loaded document (if any) keeps rendering unchanged.");
            }

            if (instances > m_instanceCapacity) {
                throw new SdfDocumentException(reason: SdfRefusal.ComposedInstancesExceeded, message: $"document: composed with the live world scene, instances {instances} exceed the probed render envelope {m_instanceCapacity} — the previously loaded document (if any) keeps rendering unchanged.");
            }
        }

        m_program = program;
        m_revision++;

        return (program.Ops.Count, program.Materials.Count, program.ContentHash);
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (context.Probe) {
            EmitProbeReservation(builder: builder);

            return;
        }

        if (m_program is { } program) {
            SdfDocumentDecoder.Replay(builder: builder, program: program);
        }
    }

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
}
