using System.Numerics;
using Puck.Forge.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The per-seat sculpt workbench — the client-local context a <see cref="SculptModel"/> edits inside. The live
/// preview is a composed pending placement: <see cref="ComposeCreations"/>/<see cref="ComposePlacements"/> overlay a
/// synthetic creation row (the model's document, timeline frames stripped — the preview shows the live pose) and a
/// synthetic placement at the workbench origin onto the delivered rows, so the preview renders through the same
/// <see cref="WorldPlacementStamper"/>/<see cref="CreationGeometry"/> path a committed stamp uses — what you sculpt is
/// what stamps, byte-for-byte. Nothing here crosses the wire: commit is the verb layer's one <c>UpsertCreation</c>.
/// </summary>
/// <remarks>Capacity: entry pre-verifies the composed candidate against the probed render envelope, matching the
/// ghost-spawn check, and the apply-time measure charges every stamp at worst case — so a preview that entered under budget
/// stays in budget for any model within the <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> cap the model itself
/// enforces. Single-threaded like every input-fold type (verb mutators in the pump's apply window, <see cref="Tick"/>
/// and the composes during frame produce — one window-pump thread).</remarks>
public sealed class WorldWorkbench {
    // The reserved client-local row-id namespace the preview composes under (the colon keeps it out of every
    // authored-id convention; see NextFreePlacementId's "place-N" grammar).
    private const string PreviewIdPrefix = "workbench:";

    // The orbit pivot's lift above the workbench origin — frames the bench's spawn height, not the ground plane.
    private static readonly Vector3 PivotLift = new(
        x: 0f,
        y: 1f,
        z: 0f
    );

    private readonly Bench[] m_benches;
    private readonly WorldClient m_client;
    private readonly WorldEditorDrag m_drag;
    private readonly WorldRenderEnvelope m_envelope;
    private readonly int[] m_seenModelRevisions;

    // The frame source's rebuild watch: MONOTONIC — structural changes bump it directly, and Tick folds each active
    // model's own revision delta in once per produced frame.
    private int m_revision;

    // One seat's bench: the model, its world anchor, the committed-state marker (dirty tracking + the HUD's
    // narration), and the cached preview document (rebuilt only when the model's revision moves).
    private sealed class Bench {
        public bool Active;
        // A commit is submitted and awaiting the server's verdict: the clean flip lands only when the delivered
        // definition carries the row at PendingCommitHash (the accept), so a rejected apply leaves the work dirty.
        public bool CommitPending;
        // The model revision at the last commit/load — edits past it are "uncommitted" (the two-domain narration).
        public int CommittedRevision;
        public SculptModel? Model;
        public Vector3 Origin;
        public int PendingCommitRevision;
        public CreationDocument? PreviewDocument;

        public string RowId = string.Empty;
        public string PendingCommitHash = string.Empty;
        public int PreviewRevision = -1;
    }

    /// <summary>Initializes a new instance of the <see cref="WorldWorkbench"/> class.</summary>
    /// <param name="client">The delivered-definition view the preview composes over.</param>
    /// <param name="envelope">The render-capacity oracle entry pre-verifies against.</param>
    /// <param name="drag">The drag channel whose pending ghosts join the entry candidate (both client-local
    /// overlays must fit the envelope together — see the remarks).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldWorkbench(WorldClient client, WorldRenderEnvelope envelope, WorldEditorDrag drag) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: envelope);
        ArgumentNullException.ThrowIfNull(argument: drag);

        m_client = client;
        m_envelope = envelope;
        m_drag = drag;
        m_benches = new Bench[PlayerRoster.MaxSlots];
        m_seenModelRevisions = new int[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            m_benches[slot] = new Bench();
        }
    }

    /// <summary>The monotonic rebuild watch the frame source folds in (see <see cref="Tick"/>).</summary>
    public int Revision => m_revision;

    // The commit apply witness: the delivered definition carries the bench's row at the submitted canonical hash
    // (mirrors WorldEditorDrag.DeliveredExpected). Until it does — a rejected or not-yet-applied commit — the bench
    // stays dirty.
    private bool CommitDelivered(Bench bench) {
        foreach (var creation in m_client.Definition.Creations) {
            if (
                string.Equals(
                a: creation.Id,
                b: bench.RowId,
                comparisonType: StringComparison.Ordinal
            ) &&
                string.Equals(
                a: creation.Hash,
                b: bench.PendingCommitHash,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    // The preview document: the model's document with its timeline frames STRIPPED — the preview stamps statically
    // and shows the model's LIVE pose (playback/frame steps write the live pose, so scrubbing still previews).
    // Cached per model revision; a mid-drag frame rebuild reuses the cache.
    private static CreationDocument PreviewDocument(Bench bench, SculptModel model) {
        if (
            (bench.PreviewDocument is { } cached) &&
            (bench.PreviewRevision == model.Revision)
        ) {
            return cached;
        }

        var document = (model.Document with { Frames = null });

        bench.PreviewDocument = document;
        bench.PreviewRevision = model.Revision;

        return document;
    }
    // A synthetic content tag, never a canonical hash: the preview never crosses a validator, and per-Build palette
    // registration keys nothing on it — it exists so debug dumps show WHICH revision rendered.
    private static string PreviewHash(int revision) => $"{PreviewIdPrefix}rev-{revision}";
    private static string PreviewId(int slot) => $"{PreviewIdPrefix}{PlayerRoster.DisplayNumber(slot: slot)}";
    private static WorldPlacement PreviewPlacement(int slot, Vector3 origin) => new(
        Id: PreviewId(slot: slot),
        CreationId: PreviewId(slot: slot),
        Position: origin,
        YawDegrees: 0f,
        Scale: 1f
    );
    private int SlotOrFirst(int slot) => ((((uint)slot) < ((uint)m_benches.Length))
        ? slot
        : 0
    );

    /// <summary>Composes a candidate definition with every active bench's preview rows — the drag channel's ghost
    /// pre-checks ride this (property-injected there), so both client-local overlays fit the envelope together.</summary>
    /// <param name="candidate">The candidate definition (already carrying the caller's own new row).</param>
    public WorldDefinition ComposeCandidate(WorldDefinition candidate) {
        return (candidate with {
            CreationsRaw = ComposeCreations(live: candidate.Creations),
            PlacementsRaw = ComposePlacements(live: candidate.Placements),
        });
    }
    /// <summary>Overlays the active benches' preview creation rows onto the delivered rows (reference-equal
    /// fast path when no bench is active). A live row sharing a preview id is replaced — the reserved
    /// <c>workbench:</c> namespace makes that unreachable for authored content.</summary>
    /// <param name="live">The delivered creation rows.</param>
    public IReadOnlyList<WorldCreation> ComposeCreations(IReadOnlyList<WorldCreation> live) {
        List<WorldCreation>? rows = null;

        for (var slot = 0; (slot < m_benches.Length); slot++) {
            var bench = m_benches[slot];

            if (
                !bench.Active ||
                (bench.Model is not { } model)
            ) {
                continue;
            }

            rows ??= [.. live];
            rows.Add(item: new WorldCreation(
                Id: PreviewId(slot: slot),
                Document: PreviewDocument(
                    bench: bench,
                    model: model
                ),
                HashRaw: PreviewHash(revision: model.Revision)
            ));
        }

        return (rows ?? live);
    }
    /// <summary>Overlays the active benches' preview placements onto the (drag-composed) rows.</summary>
    /// <param name="live">The delivered (and drag-composed) placement rows.</param>
    public IReadOnlyList<WorldPlacement> ComposePlacements(IReadOnlyList<WorldPlacement> live) {
        List<WorldPlacement>? rows = null;

        for (var slot = 0; (slot < m_benches.Length); slot++) {
            var bench = m_benches[slot];

            if (!bench.Active) {
                continue;
            }

            rows ??= [.. live];
            rows.Add(item: PreviewPlacement(
                origin: bench.Origin,
                slot: slot
            ));
        }

        return (rows ?? live);
    }
    /// <summary>Closes a seat's bench, discarding the model and its local ring (commit first to keep the work).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>Whether a bench was open.</returns>
    public bool Drop(int slot) {
        if (((uint)slot) >= ((uint)m_benches.Length)) {
            return false;
        }

        var bench = m_benches[slot];

        if (!bench.Active) {
            return false;
        }

        bench.Active = false;
        bench.Model = null;
        bench.RowId = string.Empty;
        bench.CommitPending = false;
        bench.PendingCommitHash = string.Empty;
        bench.PreviewDocument = null;
        bench.PreviewRevision = -1;
        m_revision++;

        return true;
    }
    /// <summary>Whether the seat has an active workbench.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool IsActive(int slot) => ((((uint)slot) < ((uint)m_benches.Length)) && m_benches[slot].Active);
    /// <summary>Whether the seat's bench has a commit submitted and still awaiting the server's accept/reject.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool IsCommitPending(int slot) {
        var bench = m_benches[SlotOrFirst(slot: slot)];

        return (
            bench.Active &&
            bench.CommitPending
        );
    }
    /// <summary>The seat's live sculpt model, or <see langword="null"/> when its bench is inactive.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public SculptModel? Model(int slot) => (((((uint)slot) < ((uint)m_benches.Length)) && m_benches[slot].Active)
        ? m_benches[slot].Model
        : null
    );
    /// <summary>Correlates a server-rejected <see cref="WorldMutation.UpsertCreation"/> back to the bench that
    /// submitted it (the echo tap calls this): the pending-commit flag clears so the bench stops awaiting an accept,
    /// while the committed revision stays put — the discarded work is honestly still uncommitted. Anything else is
    /// ignored.</summary>
    /// <remarks>Client-side UI-state correlation only, same shape as <see cref="WorldEditorDrag.NoteRejected"/>: the
    /// <see cref="PrincipalKind.Seat"/> filter is load-bearing because <see cref="WorldPrincipal.Index"/> is only a
    /// slot for Seat/Peer kinds (an Addon principal's Index is always 0 and would alias slot 0's bench). A claimed
    /// slot's rejected commit is simply not correlated here.</remarks>
    /// <param name="mutation">The rejected mutation.</param>
    public void NoteCommitRejected(WorldMutation mutation) {
        if (mutation is not WorldMutation.UpsertCreation { Principal.Kind: PrincipalKind.Seat } upsert) {
            return;
        }

        var slot = mutation.Principal.Index;

        if (((uint)slot) >= ((uint)m_benches.Length)) {
            return;
        }

        var bench = m_benches[slot];

        if (
            bench.Active &&
            bench.CommitPending &&
            string.Equals(
            a: bench.RowId,
            b: upsert.Creation.Id,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: bench.PendingCommitHash,
            b: upsert.Creation.Hash,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            bench.CommitPending = false;
            bench.PendingCommitHash = string.Empty;
        }
    }
    /// <summary>Records that a commit was submitted for the seat's bench. The clean flip is deferred to the server's
    /// accept — a delivered creation row carrying <paramref name="hash"/> (see <see cref="Tick"/>) — so a rejected
    /// apply leaves the work counted as uncommitted rather than falsely clean.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="hash">The canonical hash of the submitted creation document — the accept correlator.</param>
    public void NoteCommitSubmitted(int slot, string hash) {
        var bench = m_benches[SlotOrFirst(slot: slot)];

        if (
            bench.Active &&
            (bench.Model is { } model)
        ) {
            bench.CommitPending = true;
            bench.PendingCommitRevision = model.Revision;
            bench.PendingCommitHash = hash;
        }
    }
    /// <summary>The seat's workbench origin (where the preview placement stamps), meaningful while active.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3 Origin(int slot) => m_benches[SlotOrFirst(slot: slot)].Origin;
    /// <summary>The orbit pivot the editor camera frames while the seat sculpts, or <see langword="null"/> when its
    /// bench is inactive.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3? Pivot(int slot) => (IsActive(slot: slot)
        ? (m_benches[slot].Origin + PivotLift)
        : null
    );
    /// <summary>Routes one frame of latched stick motion into the seat's model in the camera's planar frame (the
    /// editor session calls this from its workbench camera branch): the move stick drives the sculpt target (shape
    /// or chain goal), the shoulder verticals lift/sink it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="planarRight">The camera's planar right axis.</param>
    /// <param name="planarForward">The camera's planar forward axis.</param>
    /// <param name="move">The latched (response-mapped) move-stick sample.</param>
    /// <param name="vertical">The held vertical channel (+1 rise, -1 sink, 0 none).</param>
    /// <param name="deltaSeconds">The clamped presentation interval.</param>
    public void RouteMove(int slot, Vector3 planarRight, Vector3 planarForward, Vector2 move, float vertical, float deltaSeconds) {
        if (Model(slot: slot) is not { } model) {
            return;
        }

        if (
            (move == Vector2.Zero) &&
            (vertical == 0f)
        ) {
            return;
        }

        // The camera-frame deflection maps into the model's world-aligned planar convention (+Y of the vector is
        // +Z); the workbench frame IS the world frame (the preview placement stamps yaw 0, scale 1), so no further
        // transform applies. Speed stays the model's fixed 3.2 u/s move rate.
        var world = ((planarRight * move.X) + (planarForward * move.Y));

        model.Move(
            planar: new Vector2(
                x: world.X,
                y: world.Z
            ),
            vertical: vertical,
            deltaSeconds: deltaSeconds
        );
    }
    /// <summary>The creation row id the bench authors toward (the commit's mutation address).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public string RowId(int slot) => m_benches[SlotOrFirst(slot: slot)].RowId;
    /// <summary>Advances the active benches once per produced frame: playback ticks on the render clock, the drag
    /// coalescer's frame boundary closes untouched drags, and each model's revision folds into the monotonic
    /// rebuild watch.</summary>
    /// <param name="deltaSeconds">The clamped presentation interval.</param>
    public void Tick(float deltaSeconds) {
        for (var slot = 0; (slot < m_benches.Length); slot++) {
            var bench = m_benches[slot];

            if (
                !bench.Active ||
                (bench.Model is not { } model)
            ) {
                continue;
            }

            model.TickPlayback(deltaSeconds: deltaSeconds);
            model.EndInputFrame();

            // The commit ACCEPT edge: a submitted commit flips the bench clean only once the delivered definition
            // carries the row at the submitted hash. Clean lands at the SUBMIT revision, so edits made after submit
            // stay uncommitted; a rejected (never-delivered) commit never reaches here and stays dirty.
            if (
                bench.CommitPending &&
                CommitDelivered(bench: bench)
            ) {
                bench.CommittedRevision = bench.PendingCommitRevision;
                bench.CommitPending = false;
                bench.PendingCommitHash = string.Empty;
            }

            if (m_seenModelRevisions[slot] != model.Revision) {
                m_seenModelRevisions[slot] = model.Revision;
                m_revision++;
            }
        }
    }
    /// <summary>Opens a seat's bench on a model (blank for a new creation, loaded for an existing row), pre-verifying
    /// the composed preview against the probed render envelope so the bench never renders past it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="rowId">The creation row id the bench authors toward.</param>
    /// <param name="origin">The workbench origin, world space.</param>
    /// <param name="document">The existing row's document to load, or <see langword="null"/> for a blank model.</param>
    /// <param name="error">The loud reason, when the method returns <see langword="false"/>.</param>
    public bool TryEnter(int slot, string rowId, Vector3 origin, CreationDocument? document, out string error) {
        error = string.Empty;

        if (((uint)slot) >= ((uint)m_benches.Length)) {
            error = "no such seat";

            return false;
        }

        var bench = m_benches[slot];

        if (bench.Active) {
            error = $"a sculpt of '{bench.RowId}' is already open — editor.sculpt.commit or editor.sculpt.exit first";

            return false;
        }

        var model = new SculptModel(shapeCapacity: WorldPlacementPolicy.MaxShapesPerStamp);

        if (document is { } loaded) {
            _ = model.Load(document: loaded);
        }

        model.SetName(name: rowId);

        // The envelope pre-check (matching the ghost-spawn check): the candidate composes the delivered definition, any
        // live drag ghosts, this bench's preview, AND every OTHER already-open bench's preview (the
        // rendered program in WorldSceneEmitter composes ALL active benches, so admission must charge all of them, not
        // only the one opening; two benches each fitting alone can exceed the frozen floor together). The measure
        // charges every stamp at worst case, so passing here keeps ANY later model state (≤ the per-stamp cap) inside
        // the probed floors.
        var definition = m_client.Definition;
        var previewDocument = (model.Document with { Frames = null });
        var creations = new List<WorldCreation>(capacity: (definition.Creations.Count + 1));
        var placements = new List<WorldPlacement>(capacity: (definition.Placements.Count + 2));

        creations.AddRange(collection: definition.Creations);
        creations.Add(item: new WorldCreation(
            Id: PreviewId(slot: slot),
            Document: previewDocument,
            HashRaw: PreviewHash(revision: model.Revision)
        ));
        placements.AddRange(collection: m_drag.ComposePlacements(live: definition.Placements));
        placements.Add(item: PreviewPlacement(
            origin: origin,
            slot: slot
        ));

        // Fold every currently-active bench (this one is not active yet, so no double-count) onto the candidate before
        // the fit check — the same ComposeCandidate the drag channel pre-checks ride, so concurrent client-local
        // overlays are admitted TOGETHER against the one frozen envelope.
        var candidate = ComposeCandidate(candidate: (definition with { CreationsRaw = creations, PlacementsRaw = placements }));

        if (!m_envelope.TryFit(
            candidate: candidate,
            reason: out var capacityReason
        )) {
            error = capacityReason;

            return false;
        }

        bench.Active = true;
        bench.Model = model;
        bench.Origin = origin;
        bench.RowId = rowId;
        bench.CommittedRevision = model.Revision;
        bench.CommitPending = false;
        bench.PendingCommitHash = string.Empty;
        bench.PreviewDocument = null;
        bench.PreviewRevision = -1;
        m_seenModelRevisions[slot] = model.Revision;
        m_revision++;

        return true;
    }
    /// <summary>How many edits sit past the last accepted commit/load (the HUD's "uncommitted" narration; 0 = clean).
    /// A submitted-but-unaccepted commit still counts here — the work is not clean until the server applies it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public int UncommittedEdits(int slot) {
        var bench = m_benches[SlotOrFirst(slot: slot)];

        return ((bench.Active && (bench.Model is { } model))
            ? Math.Max(
                val1: (model.Revision - bench.CommittedRevision),
                val2: 0
            )
            : 0
        );
    }
}
