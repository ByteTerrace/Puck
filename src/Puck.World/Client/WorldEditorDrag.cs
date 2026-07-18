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
/// rebuild fires until the applied definition delivers (the overlay then retires against identical document truth — no
/// pixel pop), or a short frame deadline passes without a delivery (the rejection case — the row snaps honestly back
/// to the unedited document while the rejection toast narrates why). Single-threaded on the window-pump thread, like
/// every editor type here.</remarks>
internal sealed class WorldEditorDrag {
    // The rejection deadline: a released overlay with no definition delivery after this many produced frames drops.
    private const int FreezeFrameDeadline = 12;
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
    /// <param name="pitch">The lattice pitch, world units.</param>
    /// <returns>The applied config.</returns>
    public SnapConfig SetSnapPitch(int slot, float pitch) {
        var channel = m_channels[SlotOrFirst(slot: slot)];

        channel.Snap = (SnapConfig.Planar(pitch: pitch) with { Enabled = true });

        return channel.Snap;
    }

    /// <summary>The pending row's current (snapped) position, while a drag is live.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3? PendingPosition(int slot) =>
        (IsDragging(slot: slot) ? m_channels[slot].Snapped : null);

    /// <summary>A one-line drag description for the HUD/status echoes, or <see langword="null"/> when idle.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public string? Describe(int slot) {
        if (!IsDragging(slot: slot)) {
            return null;
        }

        var channel = m_channels[slot];
        var subject = ((channel.SceneRow is { } row) ? $"scene '{row.Id}'" : $"screen {channel.Screen!.Index}");

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{(channel.IsGhost ? "ghost " : string.Empty)}{subject} at ({channel.Snapped.X:0.00}, {channel.Snapped.Y:0.00}, {channel.Snapped.Z:0.00})"
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
                        Begin(channel: channel, sceneRow: row, screen: null, origin: row.Center, isGhost: false);

                        return true;
                    }
                }

                error = $"no scene row '{selection.Id}' to grab";

                return false;
            case WorldSection.Screens:
                foreach (var screen in definition.Screens) {
                    if (screen.Index == selection.Index) {
                        Begin(channel: channel, sceneRow: null, screen: screen, origin: screen.Origin, isGhost: false);

                        return true;
                    }
                }

                error = $"no screen {selection.Index} to grab";

                return false;
            default:
                error = $"{selection.Describe()} does not drag — move it with editor.move/editor.nudge";

                return false;
        }
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

        if (!m_envelope.TryFit(scene: WithRow(scene: definition.Scene, row: row), screens: definition.Screens, reason: out var capacityReason)) {
            error = capacityReason;

            return false;
        }

        Begin(channel: channel, sceneRow: row, screen: null, origin: row.Center, isGhost: true);

        return true;
    }

    /// <summary>Moves the pending row by a delta (the <c>editor.drag</c> console twin of stick motion). Client-local
    /// only — nothing crosses the wire.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="delta">The world-space delta.</param>
    public void Move(int slot, Vector3 delta) {
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
            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSceneRow(Principal: principal, Row: row.WithCenter(center: channel.Snapped)));
            subject = $"scene '{row.Id}'";
        } else {
            var screen = channel.Screen!;

            m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: (screen with { Origin = channel.Snapped })));
            subject = $"screen {screen.Index}";
        }

        // Freeze: the pending row stays composed (the screen already shows the committed pose) until Reconcile
        // retires it against the delivered definition or drops it on the rejection deadline.
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
        var subject = ((channel.SceneRow is { } row) ? $"scene '{row.Id}'" : $"screen {channel.Screen!.Index}");
        var wasGhost = channel.IsGhost;

        Clear(channel: channel);

        return (wasGhost ? $"ghost {subject} discarded" : $"{subject} back at its document pose");
    }

    /// <summary>Retires frozen overlays: called once per produced frame (before the compose reads) with the client's
    /// current definition revision. See the type remarks for the two retirement edges.</summary>
    /// <param name="definitionRevision">The client's current definition-delivery revision.</param>
    public void Reconcile(int definitionRevision) {
        foreach (var channel in m_channels) {
            if (!channel.Frozen) {
                continue;
            }

            if ((definitionRevision != channel.FreezeRevision) || (++channel.FreezeFrames > FreezeFrameDeadline)) {
                Clear(channel: channel);
            }
        }
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

    private void Begin(Channel channel, WorldSceneRow? sceneRow, WorldScreen? screen, Vector3 origin, bool isGhost) {
        channel.Active = true;
        channel.Frozen = false;
        channel.IsGhost = isGhost;
        channel.SceneRow = sceneRow;
        channel.Screen = screen;
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
