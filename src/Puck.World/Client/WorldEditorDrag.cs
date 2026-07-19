using System.Globalization;
using System.Numerics;
using Puck.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The drag preview channel — the client-local pending-row overlay every continuous manipulation rides. A grab copies
/// the selected row (or a spawn act creates a ghost row) into a per-seat pending slot; stick/verb motion moves the
/// pending position through <see cref="GridSnap"/>; the frame source composes the pending rows over the delivered
/// definition so the EXISTING rebuild path renders the preview (drag-cadence rebuild ≈ the proven seat-recolor cost);
/// and release submits exactly ONE whole-row mutation over the link (the wire-boundary drag coalescing — a 10-second
/// drag is one journal entry, one undo step). Cancel clears the pending row: the drag never existed.
/// </summary>
/// <remarks>Release FREEZES the overlay instead of clearing it: the screen already shows the committed pose, so no
/// rebuild fires until the released act's OWN result retires it — an APPLY (the delivered definition's keyed row
/// equals the frozen expected row: identical document truth, no pixel pop) or a REJECTION (the server's edit echo
/// correlates back through <see cref="NoteRejected"/>: the row snaps honestly back while the rejection toast narrates
/// why). An UNRELATED delivery — another principal's mutation advancing the definition revision — leaves the frozen
/// preview standing (UIE-3: the old global revision watch retired it early, snapping the row back before the released
/// act resolved). The frame deadline stays as the honest fallback for a missing response. Single-threaded on the
/// window-pump thread, like every editor type here.</remarks>
internal sealed class WorldEditorDrag {
    private const float DefaultSnapPitch = 0.5f;

    // One seat's pending-row channel. A mutable class per seat so the per-frame paths never copy.
    private sealed class Channel {
        public bool Active;
        public bool Frozen;
        public bool IsGhost;
        public int FreezeRevision;
        public int FreezeFrames;
        public WorldSceneRow? SceneRow;
        public WorldScreen? Screen;
        public WorldPlacement? Placement;
        // The whole row the release-edge mutation submitted — the frozen preview's retirement correlator: an apply
        // delivers exactly this row (record equality); a rejection names it through NoteRejected.
        public WorldSceneRow? ExpectedRow;
        public WorldScreen? ExpectedScreen;
        public WorldPlacement? ExpectedPlacement;
        public Vector3 Origin;
        public Vector3 Intent;
        public Vector3 Snapped;
        public SnapConfig Snap = SnapConfig.Planar(pitch: DefaultSnapPitch);
    }

    private readonly WorldClient m_client;
    private readonly IServerLink m_link;
    private readonly WorldRenderEnvelope m_envelope;
    private readonly Channel[] m_channels;
    // The last id number ISSUED per prefix this session. Mutations buffer at the tick boundary, so two placements in
    // one apply window would otherwise scan the same delivered definition and collide on the same id; the watermark
    // makes issuance monotonic regardless of delivery timing (a deleted id is deliberately never reissued).
    private readonly Dictionary<string, int> m_idWatermarks = new(comparer: StringComparer.Ordinal);
    private int m_revision;

    /// <summary>Initializes a new instance of the <see cref="WorldEditorDrag"/> class.</summary>
    /// <param name="client">The client view supplying the live definition pending rows overlay.</param>
    /// <param name="link">The server link the release-edge mutation rides.</param>
    /// <param name="envelope">The render-capacity oracle a ghost spawn is pre-checked against (a ghost renders before
    /// any server-side gate sees it, so headroom is verified at spawn).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEditorDrag(WorldClient client, IServerLink link, WorldRenderEnvelope envelope) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: envelope);

        m_client = client;
        m_link = link;
        m_envelope = envelope;
        m_channels = new Channel[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < m_channels.Length); slot++) {
            m_channels[slot] = new Channel();
        }
    }

    /// <summary>The monotonic overlay revision — bumped whenever a pending row appears, moves, or clears; folded into
    /// the frame source's program-rebuild watch.</summary>
    public int Revision => m_revision;

    /// <summary>Folds every OTHER client-local preview overlay (the sculpt workbench's composed rows) into a ghost
    /// spawn's envelope candidate, so all client-side previews are capacity-checked TOGETHER — a ghost that fits
    /// alone but not beside an open bench must reject at spawn, never overflow the frozen floors at render.
    /// Property-injected (the workbench composes after this channel).</summary>
    public Func<WorldDefinition, WorldDefinition>? CandidateComposer { get; set; }

    // The ghost-spawn envelope gate: the candidate (already carrying the caller's new row) plus every other
    // client-local overlay, against the probed floors.
    private bool TryFitComposed(WorldDefinition candidate, out string reason) {
        return m_envelope.TryFit(candidate: (CandidateComposer?.Invoke(arg: candidate) ?? candidate), reason: out reason);
    }

    /// <summary>Whether the seat holds a live (unreleased) drag.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool IsDragging(int slot) => (((uint)slot < (uint)m_channels.Length) && m_channels[slot].Active);

    /// <summary>The seat's snap configuration (the toggle and the pitch verbs read/write it).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public SnapConfig Snap(int slot) => m_channels[SlotOrFirst(slot: slot)].Snap;

    /// <summary>Sets whether the seat's grid snap is enabled (the toggle chord / <c>editor.snap on|off</c>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="enabled">Whether snapping engages.</param>
    /// <returns>The applied config.</returns>
    public SnapConfig SetSnapEnabled(int slot, bool enabled) {
        var channel = m_channels[SlotOrFirst(slot: slot)];

        channel.Snap = (channel.Snap with { Enabled = enabled });

        return channel.Snap;
    }

    /// <summary>Sets the seat's planar snap pitch (X/Z lattice; Y stays free) and enables snapping.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="pitch">The lattice pitch, world units (finite, positive).</param>
    /// <returns>The applied config.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pitch"/> is not finite or not positive.</exception>
    public SnapConfig SetSnapPitch(int slot, float pitch) {
        FiniteGuard.ThrowIfNonFinite(value: pitch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: pitch);

        var channel = m_channels[SlotOrFirst(slot: slot)];

        channel.Snap = (SnapConfig.Planar(pitch: pitch) with { Enabled = true });

        return channel.Snap;
    }

    /// <summary>The pending row's current (snapped) position, while a drag is live.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3? PendingPosition(int slot) =>
        (IsDragging(slot: slot) ? m_channels[slot].Snapped : null);

    /// <summary>A one-line drag description for the HUD/status echoes — a live drag, a FROZEN released preview
    /// (awaiting its own apply/rejection — the pipe-observable freeze window), or <see langword="null"/> when idle.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public string? Describe(int slot) {
        if ((uint)slot >= (uint)m_channels.Length) {
            return null;
        }

        var channel = m_channels[slot];

        if (!channel.Active && !channel.Frozen) {
            return null;
        }

        var subject = Subject(channel: channel);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(channel.Frozen ? "frozen " : string.Empty)}{(channel.IsGhost ? "ghost " : string.Empty)}{subject} at ({channel.Snapped.X:0.00}, {channel.Snapped.Y:0.00}, {channel.Snapped.Z:0.00})"
        );
    }

    /// <summary>Begins a drag from the seat's selection: copies the row out of the live definition into the pending
    /// slot. Scene rows and screens drag; spawns and cameras move over the numeric verbs instead.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="selection">The seat's live selection.</param>
    /// <param name="error">The loud reason, when the method returns <see langword="false"/>.</param>
    public bool TryGrab(int slot, in EditorSelection selection, out string error) {
        error = string.Empty;

        var channel = m_channels[SlotOrFirst(slot: slot)];

        if (channel.Active) {
            error = "a drag is already live — release or cancel it first";

            return false;
        }

        var definition = m_client.Definition;

        switch (selection.Section) {
            case WorldSection.Scene:
                foreach (var row in definition.Scene.Rows) {
                    if (string.Equals(a: row.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        Begin(channel: channel, sceneRow: row, screen: null, placement: null, origin: row.Center, isGhost: false);

                        return true;
                    }
                }

                error = $"no scene row '{selection.Id}' to grab";

                return false;
            case WorldSection.Screens:
                foreach (var screen in definition.Screens) {
                    if (screen.Index == selection.Index) {
                        Begin(channel: channel, sceneRow: null, screen: screen, placement: null, origin: screen.Origin, isGhost: false);

                        return true;
                    }
                }

                error = $"no screen {selection.Index} to grab";

                return false;
            case WorldSection.Placements:
                foreach (var placement in definition.Placements) {
                    if (string.Equals(a: placement.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        Begin(channel: channel, sceneRow: null, screen: null, placement: placement, origin: placement.Position, isGhost: false);

                        return true;
                    }
                }

                error = $"no placement '{selection.Id}' to grab";

                return false;
            default:
                error = $"{selection.Describe()} does not drag — move it with editor.move/editor.nudge";

                return false;
        }
    }

    /// <summary>Begins a GHOST drag for a brand-new placement (the stamp act): the creation previews through the
    /// overlay's composed rows and enters the document only on release. Headroom is verified here against the probed
    /// envelope, so a ghost never renders past it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="placement">The new placement row (its id must be free; see <see cref="NextFreePlacementId"/>).</param>
    /// <param name="error">The loud reason, when the method returns <see langword="false"/>.</param>
    public bool TrySpawnPlacementGhost(int slot, WorldPlacement placement, out string error) {
        error = string.Empty;

        var channel = m_channels[SlotOrFirst(slot: slot)];

        if (channel.Active) {
            error = "a drag is already live — release or cancel it first";

            return false;
        }

        var definition = m_client.Definition;
        var rows = new List<WorldPlacement>(capacity: (definition.Placements.Count + 1));

        rows.AddRange(collection: definition.Placements);
        rows.Add(item: placement);

        if (!TryFitComposed(candidate: (definition with { Placements = rows }), reason: out var capacityReason)) {
            error = capacityReason;

            return false;
        }

        Begin(channel: channel, sceneRow: null, screen: null, placement: placement, origin: placement.Position, isGhost: true);

        return true;
    }

    /// <summary>Begins a GHOST drag for a brand-new scene row (the spawn act): the row previews through the overlay
    /// and enters the document only on release. Headroom is verified here, so a ghost never renders past the probed
    /// envelope.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="row">The new row (its id must be free; see <see cref="NextFreeSceneRowId"/>).</param>
    /// <param name="error">The loud reason, when the method returns <see langword="false"/>.</param>
    public bool TrySpawnGhost(int slot, WorldSceneRow row, out string error) {
        error = string.Empty;

        var channel = m_channels[SlotOrFirst(slot: slot)];

        if (channel.Active) {
            error = "a drag is already live — release or cancel it first";

            return false;
        }

        var definition = m_client.Definition;

        if (!TryFitComposed(candidate: (definition with { Scene = WithRow(scene: definition.Scene, row: row) }), reason: out var capacityReason)) {
            error = capacityReason;

            return false;
        }

        Begin(channel: channel, sceneRow: row, screen: null, placement: null, origin: row.Center, isGhost: true);

        return true;
    }

    /// <summary>Moves the pending row by a delta (the <c>editor.drag</c> console twin of stick motion). Client-local
    /// only — nothing crosses the wire.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="delta">The world-space delta (finite).</param>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="delta"/> is not finite.</exception>
    public void Move(int slot, Vector3 delta) {
        FiniteGuard.ThrowIfNonFinite(value: delta);

        if (!IsDragging(slot: slot)) {
            return;
        }

        var channel = m_channels[slot];

        channel.Intent += delta;
        ApplySnap(channel: channel);
    }

    /// <summary>Integrates one frame of stick motion into the pending row (the latched-stick path the editor session
    /// routes while a drag is live): the move stick translates in the camera's yaw frame, the shoulder verticals
    /// lift/sink.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="planarRight">The camera's planar right axis.</param>
    /// <param name="planarForward">The camera's planar forward axis.</param>
    /// <param name="move">The latched (response-mapped) move-stick sample.</param>
    /// <param name="vertical">The held vertical channel (+1 rise, -1 sink, 0 none).</param>
    /// <param name="speed">The seat's fly speed, world units per second (the drag rides the same lever).</param>
    /// <param name="deltaSeconds">The clamped presentation interval.</param>
    public void Advance(int slot, Vector3 planarRight, Vector3 planarForward, Vector2 move, float vertical, float speed, float deltaSeconds) {
        if (!IsDragging(slot: slot)) {
            return;
        }

        var velocity = ((planarForward * move.Y) + (planarRight * move.X) + (Vector3.UnitY * vertical));

        if (velocity == Vector3.Zero) {
            return;
        }

        var channel = m_channels[slot];

        channel.Intent += (velocity * (speed * deltaSeconds));
        ApplySnap(channel: channel);
    }

    /// <summary>Commits the drag: submits exactly ONE whole-row mutation (the release edge) and freezes the overlay
    /// until the applied definition delivers (see the type remarks).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="principal">The acting seat principal the mutation is checked against.</param>
    /// <returns>The echo line, or <see langword="null"/> when no drag was live.</returns>
    public string? Release(int slot, WorldPrincipal principal) {
        if (!IsDragging(slot: slot)) {
            return null;
        }

        var channel = m_channels[slot];
        string subject;

        if (channel.SceneRow is { } row) {
            var moved = row.WithCenter(center: channel.Snapped);

            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSceneRow(Principal: principal, Row: moved));
            channel.ExpectedRow = moved;
            subject = $"scene '{row.Id}'";
        } else if (channel.Placement is { } placement) {
            var moved = (placement with { Position = channel.Snapped });

            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertPlacement(Principal: principal, Placement: moved));
            channel.ExpectedPlacement = moved;
            subject = $"placement '{placement.Id}'";
        } else {
            var screen = channel.Screen!;
            var moved = (screen with { Origin = channel.Snapped });

            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: moved));
            channel.ExpectedScreen = moved;
            subject = $"screen {screen.Index}";
        }

        // Freeze: the pending row stays composed (the screen already shows the committed pose) until the released
        // act's own APPLY (Reconcile finds the expected row delivered) or REJECTION (NoteRejected) retires it, or
        // the deadline drops it (the missing-response fallback).
        channel.Active = false;
        channel.Frozen = true;
        channel.FreezeRevision = m_client.DefinitionRevision;
        channel.FreezeFrames = 0;

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{subject} ({channel.Origin.X:0.00}, {channel.Origin.Y:0.00}, {channel.Origin.Z:0.00}) -> ({channel.Snapped.X:0.00}, {channel.Snapped.Y:0.00}, {channel.Snapped.Z:0.00}) — one mutation submitted"
        );
    }

    /// <summary>Aborts the drag: the pending row drops and the mutation never existed.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The echo line, or <see langword="null"/> when no drag was live.</returns>
    public string? Cancel(int slot) {
        if (!IsDragging(slot: slot)) {
            return null;
        }

        var channel = m_channels[slot];
        var subject = Subject(channel: channel);
        var wasGhost = channel.IsGhost;

        Clear(channel: channel);

        return (wasGhost ? $"ghost {subject} discarded" : $"{subject} back at its document pose");
    }

    /// <summary>Drops the seat's pending-row channel unconditionally — live drag AND frozen released preview — the
    /// editor-deactivation teardown (explicit exit, controller departure). The command guards refuse cancel/release
    /// for a non-editing seat, so without this a re-entering or reused slot would inherit and commit the old pending
    /// row.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void Drop(int slot) {
        if ((uint)slot >= (uint)m_channels.Length) {
            return;
        }

        var channel = m_channels[slot];

        if (channel.Active || channel.Frozen) {
            Clear(channel: channel);
        }
    }

    /// <summary>Retires frozen overlays: called once per produced frame (before the compose reads). A frozen preview
    /// retires ONLY on its own act's result — a delivery whose keyed row equals the submitted expected row (the
    /// apply), or the frame deadline (the missing-response fallback; rejections retire through
    /// <see cref="NoteRejected"/>). An unrelated delivery re-arms nothing and retires nothing.</summary>
    public void Reconcile() {
        var definitionRevision = m_client.DefinitionRevision;

        for (var slot = 0; (slot < m_channels.Length); slot++) {
            var channel = m_channels[slot];

            if (!channel.Frozen) {
                continue;
            }

            if (definitionRevision != channel.FreezeRevision) {
                channel.FreezeRevision = definitionRevision;

                if (DeliveredExpected(channel: channel)) {
                    Retire(slot: slot, channel: channel, reason: "applied");

                    continue;
                }
            }

            // LIVE-CONSUMED: WorldAuthoringDefaults.PreviewDeadlineFrames, read fresh every tick — a
            // world.authoring.set mutation of the deadline takes effect on the very next frame, mid-freeze included.
            if (++channel.FreezeFrames > m_client.Definition.Authoring.PreviewDeadlineFrames) {
                Retire(slot: slot, channel: channel, reason: "deadline (no response)");
            }
        }
    }

    /// <summary>Correlates a server-rejected mutation back to the frozen preview that submitted it (the
    /// <c>WorldServer.EchoTap</c> wiring calls this beside the rejection toast): the matched seat's overlay retires
    /// and the row snaps honestly back to the unedited document. A rejection of anything else is ignored.</summary>
    /// <param name="mutation">The rejected mutation.</param>
    public void NoteRejected(WorldMutation mutation) {
        if (mutation is not { Principal.Kind: PrincipalKind.Seat }) {
            return;
        }

        var slot = mutation.Principal.Index;

        if ((uint)slot >= (uint)m_channels.Length) {
            return;
        }

        var channel = m_channels[slot];

        if (!channel.Frozen) {
            return;
        }

        var matches = (mutation switch {
            WorldMutation.UpsertSceneRow upsert => ((channel.ExpectedRow is { } expected) && expected.Equals(other: upsert.Row)),
            WorldMutation.UpsertScreen upsert => ((channel.ExpectedScreen is { } expected) && expected.Equals(other: upsert.Screen)),
            WorldMutation.UpsertPlacement upsert => ((channel.ExpectedPlacement is { } expected) && expected.Equals(other: upsert.Placement)),
            _ => false,
        });

        if (matches) {
            Retire(slot: slot, channel: channel, reason: "rejected");
        }
    }

    // Whether the client's live definition carries the frozen channel's expected row verbatim — the apply witness.
    private bool DeliveredExpected(Channel channel) {
        var definition = m_client.Definition;

        if (channel.ExpectedRow is { } expectedRow) {
            foreach (var row in definition.Scene.Rows) {
                if (row.Equals(other: expectedRow)) {
                    return true;
                }
            }

            return false;
        }

        if (channel.ExpectedPlacement is { } expectedPlacement) {
            foreach (var placement in definition.Placements) {
                if (placement.Equals(other: expectedPlacement)) {
                    return true;
                }
            }

            return false;
        }

        if (channel.ExpectedScreen is { } expectedScreen) {
            foreach (var screen in definition.Screens) {
                if (screen.Equals(other: expectedScreen)) {
                    return true;
                }
            }
        }

        return false;
    }

    // The channel's console-echo subject: the scene row, placement, or screen it carries.
    private static string Subject(Channel channel) => ((channel.SceneRow is { } row)
        ? $"scene '{row.Id}'"
        : ((channel.Placement is { } placement) ? $"placement '{placement.Id}'" : $"screen {channel.Screen!.Index}"));

    // Retire a frozen overlay with its honest reason narrated once (act-scale, never per frame) — the proof's
    // observable retire edge.
    private void Retire(int slot, Channel channel, string reason) {
        Console.Error.WriteLine(value: $"[editor.drag] seat {PlayerRoster.DisplayNumber(slot: slot)} frozen {Subject(channel: channel)} retired: {reason}");
        Clear(channel: channel);
    }

    /// <summary>Composes the pending scene rows over the live scene — the frame source's rebuild read. Returns the
    /// live scene unchanged when nothing is pending (the reference-equal fast path).</summary>
    /// <param name="live">The delivered definition's scene.</param>
    public WorldScene ComposeScene(WorldScene live) {
        List<WorldSceneRow>? rows = null;

        foreach (var channel in m_channels) {
            if ((!channel.Active && !channel.Frozen) || (channel.SceneRow is not { } pending)) {
                continue;
            }

            rows ??= [.. live.Rows];

            var moved = pending.WithCenter(center: channel.Snapped);
            var replaced = false;

            for (var index = 0; (index < rows.Count); index++) {
                if (string.Equals(a: rows[index].Id, b: moved.Id, comparisonType: StringComparison.Ordinal)) {
                    rows[index] = moved;
                    replaced = true;

                    break;
                }
            }

            if (!replaced) {
                rows.Add(item: moved);
            }
        }

        return ((rows is null) ? live : (live with { Rows = rows }));
    }

    /// <summary>Composes the pending screen rows over the live screens (see <see cref="ComposeScene"/>).</summary>
    /// <param name="live">The delivered definition's screens.</param>
    public IReadOnlyList<WorldScreen> ComposeScreens(IReadOnlyList<WorldScreen> live) {
        List<WorldScreen>? screens = null;

        foreach (var channel in m_channels) {
            if ((!channel.Active && !channel.Frozen) || (channel.Screen is not { } pending)) {
                continue;
            }

            screens ??= [.. live];

            var moved = (pending with { Origin = channel.Snapped });

            for (var index = 0; (index < screens.Count); index++) {
                if (screens[index].Index == moved.Index) {
                    screens[index] = moved;

                    break;
                }
            }
        }

        return (screens ?? live);
    }

    /// <summary>Composes the pending placement rows over the live placements (see <see cref="ComposeScene"/>).</summary>
    /// <param name="live">The delivered definition's placements.</param>
    public IReadOnlyList<WorldPlacement> ComposePlacements(IReadOnlyList<WorldPlacement> live) {
        List<WorldPlacement>? rows = null;

        foreach (var channel in m_channels) {
            if ((!channel.Active && !channel.Frozen) || (channel.Placement is not { } pending)) {
                continue;
            }

            rows ??= [.. live];

            var moved = (pending with { Position = channel.Snapped });
            var replaced = false;

            for (var index = 0; (index < rows.Count); index++) {
                if (string.Equals(a: rows[index].Id, b: moved.Id, comparisonType: StringComparison.Ordinal)) {
                    rows[index] = moved;
                    replaced = true;

                    break;
                }
            }

            if (!replaced) {
                rows.Add(item: moved);
            }
        }

        return (rows ?? live);
    }

    /// <summary>The next free placement id (the <c>place-</c> prefix), scanning the live rows, every pending ghost,
    /// and the session's issuance watermark — the placement twin of <see cref="NextFreeSceneRowId"/>.</summary>
    public string NextFreePlacementId() {
        const string prefix = "place-";
        var next = 1;

        foreach (var placement in m_client.Definition.Placements) {
            BumpPast(id: placement.Id, prefix: prefix, next: ref next);
        }

        foreach (var channel in m_channels) {
            if ((channel.Active || channel.Frozen) && (channel.Placement is { } pending)) {
                BumpPast(id: pending.Id, prefix: prefix, next: ref next);
            }
        }

        if (m_idWatermarks.TryGetValue(key: prefix, value: out var issued) && (issued >= next)) {
            next = (issued + 1);
        }

        m_idWatermarks[prefix] = next;

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{prefix}{next}");
    }

    /// <summary>The next free scene-row id under a kind prefix (<c>boulder-</c>/<c>slab-</c>), scanning the live rows,
    /// every pending ghost, and the session's issuance watermark (so placements buffered into the same tick window
    /// never collide).</summary>
    /// <param name="prefix">The kind prefix, including the trailing dash.</param>
    public string NextFreeSceneRowId(string prefix) {
        var next = 1;

        foreach (var row in m_client.Definition.Scene.Rows) {
            BumpPast(id: row.Id, prefix: prefix, next: ref next);
        }

        foreach (var channel in m_channels) {
            if ((channel.Active || channel.Frozen) && (channel.SceneRow is { } pending)) {
                BumpPast(id: pending.Id, prefix: prefix, next: ref next);
            }
        }

        if (m_idWatermarks.TryGetValue(key: prefix, value: out var issued) && (issued >= next)) {
            next = (issued + 1);
        }

        m_idWatermarks[prefix] = next;

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{prefix}{next}");
    }

    private static void BumpPast(string id, string prefix, ref int next) {
        if (id.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) &&
            int.TryParse(s: id.AsSpan(start: prefix.Length), provider: CultureInfo.InvariantCulture, result: out var number) &&
            (number >= next)) {
            next = (number + 1);
        }
    }

    private void Begin(Channel channel, WorldSceneRow? sceneRow, WorldScreen? screen, WorldPlacement? placement, Vector3 origin, bool isGhost) {
        channel.Active = true;
        channel.Frozen = false;
        channel.IsGhost = isGhost;
        channel.SceneRow = sceneRow;
        channel.Screen = screen;
        channel.Placement = placement;
        channel.ExpectedRow = null;
        channel.ExpectedScreen = null;
        channel.ExpectedPlacement = null;
        channel.Origin = origin;
        channel.Intent = origin;
        channel.Snapped = origin;
        m_revision++;
    }

    private void Clear(Channel channel) {
        channel.Active = false;
        channel.Frozen = false;
        channel.IsGhost = false;
        channel.SceneRow = null;
        channel.Screen = null;
        channel.Placement = null;
        channel.ExpectedRow = null;
        channel.ExpectedScreen = null;
        channel.ExpectedPlacement = null;
        m_revision++;
    }

    // Snap the integrated intent (the retained pre-snap cursor) through the shared authoring math; a real movement of
    // the snapped result bumps the overlay revision (the rebuild watch).
    private void ApplySnap(Channel channel) {
        var snapped = GridSnap.Apply(intent: channel.Intent, config: in channel.Snap, candidateLocalHalfExtents: Vector3.Zero, previousSnapped: channel.Snapped);

        if (snapped != channel.Snapped) {
            channel.Snapped = snapped;
            m_revision++;
        }
    }

    private static WorldScene WithRow(WorldScene scene, WorldSceneRow row) {
        var rows = new List<WorldSceneRow>(capacity: (scene.Rows.Count + 1));

        rows.AddRange(collection: scene.Rows);
        rows.Add(item: row);

        return (scene with { Rows = rows });
    }

    private int SlotOrFirst(int slot) => (((uint)slot < (uint)m_channels.Length) ? slot : 0);
}
