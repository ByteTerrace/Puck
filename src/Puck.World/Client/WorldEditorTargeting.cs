using System.Numerics;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>A seat's editor selection — pure client data over the stable-id convention every document row already has
/// (never protocol; the server-visible reflection of "I'm editing this" is an exclusive grant).</summary>
/// <param name="Section">The world-document section the selected row lives in.</param>
/// <param name="Id">The row's stable string id (empty for a screen).</param>
/// <param name="Index">The engine screen index (<c>-1</c> for every non-screen row).</param>
internal readonly record struct EditorSelection(WorldSection Section, string Id, int Index) {
    /// <summary>The console-echo label: <c>screen &lt;n&gt;</c> or <c>&lt;section&gt; '&lt;id&gt;'</c>.</summary>
    public string Describe() => ((Section == WorldSection.Screens)
        ? $"screen {Index}"
        : $"{Section.ToString().ToLowerInvariant()} '{Id}'");
}

/// <summary>
/// The per-seat selection state and targeting acts: proximity candidates cycled by distance from the seat's editor
/// focus point, the look-ray pick through <see cref="WorldEditorPicker"/>, and the live-document resolution every
/// consumer (highlight, orbit pivot, HUD, the manipulation verbs) reads a selection through. Selections self-heal: a
/// selected row that vanished from the delivered definition (a delete, an undo, a load) clears on the next read, so a
/// stale id never dangles.
/// </summary>
/// <remarks>Single-threaded, like every editor type here: the verb/chord mutators run during the command pump's apply
/// window and the render-path reads (<see cref="IsSceneRowSelected"/>, the HUD feed) run during frame produce, all on
/// the launcher's window-pump thread.</remarks>
internal sealed class WorldEditorTargeting {
    private readonly WorldClient m_client;
    private readonly WorldEditorPicker m_picker;
    private readonly WorldEditorSession m_session;
    private readonly EditorSelection?[] m_selected = new EditorSelection?[PlayerRoster.MaxSlots];
    // Reused candidate-sort scratch (an act-driven path; the array is sized to the pick table on demand).
    private (float DistanceSquared, int Target)[] m_sortScratch = [];
    private int m_revision;

    /// <summary>Initializes a new instance of the <see cref="WorldEditorTargeting"/> class.</summary>
    /// <param name="client">The client view whose delivered definition selections resolve against.</param>
    /// <param name="picker">The look-ray picking program (also the proximity candidate pool).</param>
    /// <param name="session">The editor session supplying each seat's camera focus and look ray.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEditorTargeting(WorldClient client, WorldEditorPicker picker, WorldEditorSession session) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: picker);
        ArgumentNullException.ThrowIfNull(argument: session);

        m_client = client;
        m_picker = picker;
        m_session = session;
    }

    /// <summary>The monotonic selection revision — bumped on every selection change, folded into the frame source's
    /// program-rebuild watch so a highlight lands at human cadence.</summary>
    public int Revision => m_revision;

    /// <summary>LIVE-CONSUMED: the proximity-candidate radius around a seat's editor focus point, world units — the
    /// EXPLICIT candidate policy (UIE-10): cycling never walks the whole world. Rows beyond this reach are selected
    /// by pick or by the <c>editor.select</c> console twin. Read fresh from the delivered definition's
    /// <see cref="WorldAuthoringDefaults.CandidateRadius"/> at every gather — a <c>world.authoring.set</c> mutation
    /// takes effect at the very next cycle, no restart.</summary>
    public float CandidateRadius => m_client.Definition.Authoring.CandidateRadius;

    /// <summary>LIVE-CONSUMED: the candidate-count cap (<see cref="WorldAuthoringDefaults.CandidateCap"/>) — at most
    /// this many nearest in-radius rows enter the cycle ring, so the chord stays usable against large placement
    /// catalogs.</summary>
    public int CandidateCap => m_client.Definition.Authoring.CandidateCap;

    /// <summary>The seat's current selection, or <see langword="null"/>. A selection whose row no longer resolves in
    /// the delivered definition is cleared here (the self-heal read).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public EditorSelection? Selected(int slot) {
        if (((uint)slot >= (uint)m_selected.Length) || (m_selected[slot] is not { } selection)) {
            return null;
        }

        if (ResolvePosition(selection: in selection) is null) {
            m_selected[slot] = null;
            m_revision++;

            return null;
        }

        return selection;
    }

    /// <summary>Whether any editing seat currently selects the scene row with this id — the render highlight query.</summary>
    /// <param name="id">The scene-row id.</param>
    public bool IsSceneRowSelected(string id) {
        foreach (var selection in m_selected) {
            if (selection is { Section: WorldSection.Scene } selected && string.Equals(a: selected.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any editing seat currently selects the placement with this id — the render-tint query
    /// (the placement twin of <see cref="IsSceneRowSelected"/>).</summary>
    /// <param name="id">The placement id.</param>
    public bool IsPlacementSelected(string id) {
        foreach (var selection in m_selected) {
            if (selection is { Section: WorldSection.Placements } selected && string.Equals(a: selected.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The selected row's authored position, resolved from the LIVE definition — the orbit pivot and the
    /// numeric-verb base. An anchored camera resolves approximately (anchor render pose + raw offset).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector3? SelectionPosition(int slot) {
        return ((Selected(slot: slot) is { } selection) ? ResolvePosition(selection: in selection) : null);
    }

    /// <summary>Selects a document row explicitly (the <c>editor.select</c> twin).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="section">The section to select in.</param>
    /// <param name="key">The row key: a stable id, or the screen index rendered as an integer.</param>
    /// <param name="selection">The applied selection, when the method returns <see langword="true"/>.</param>
    /// <param name="error">The loud reason, when the method returns <see langword="false"/>.</param>
    public bool TrySelect(int slot, WorldSection section, string key, out EditorSelection selection, out string error) {
        selection = default;
        error = string.Empty;

        if (section == WorldSection.Screens) {
            if (!int.TryParse(s: key, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var index)) {
                error = $"screen key '{key}' must be an integer index";

                return false;
            }

            selection = new EditorSelection(Section: WorldSection.Screens, Id: string.Empty, Index: index);
        } else {
            selection = new EditorSelection(Section: section, Id: key, Index: -1);
        }

        if (ResolvePosition(selection: in selection) is null) {
            error = $"no {selection.Describe()} in the live definition";

            return false;
        }

        Apply(slot: slot, selection: selection);

        return true;
    }

    /// <summary>Cycles the selection through the BOUNDED proximity-candidate ring around the seat's editor focus
    /// point: the nearest at most <see cref="CandidateCap"/> rows within <see cref="CandidateRadius"/>, sorted
    /// nearest-first (the chord cycle; UIE-10's explicit policy). With no current selection — or a selection outside
    /// the ring — the nearest candidate is taken regardless of direction.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="direction">+1 next (farther), -1 previous (nearer); wraps within the ring.</param>
    /// <returns>The new selection, its distance from the focus point, and the ring size, or <see langword="null"/>
    /// with no in-radius candidates.</returns>
    public (EditorSelection Selection, float Distance, int Count)? Cycle(int slot, int direction) {
        var count = GatherCandidates(slot: slot);

        if (count == 0) {
            return null;
        }

        var targets = m_picker.Targets;
        var span = m_sortScratch.AsSpan(start: 0, length: count);
        var position = 0;

        if (Selected(slot: slot) is { } current) {
            for (var rank = 0; (rank < span.Length); rank++) {
                if (Matches(target: in targets[span[rank].Target], selection: in current)) {
                    position = ((((rank + direction) % span.Length) + span.Length) % span.Length);

                    break;
                }
            }
        }

        var picked = targets[span[position].Target];
        var selection = ToSelection(target: in picked);

        Apply(slot: slot, selection: selection);

        return (selection, MathF.Sqrt(x: span[position].DistanceSquared), count);
    }

    /// <summary>The seat's current candidate-ring size — the in-radius, capped count the cycle chord walks (the
    /// <c>editor.status</c> narration).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public int CandidateCount(int slot) => GatherCandidates(slot: slot);

    // Fill the sort scratch with the in-radius candidates around the seat's focus, sorted nearest-first and capped
    // (the ONE candidate-policy implementation both the cycle and the narration read). Returns the ring size.
    private int GatherCandidates(int slot) {
        var targets = m_picker.Targets;

        if (targets.Length == 0) {
            return 0;
        }

        if (m_sortScratch.Length < targets.Length) {
            m_sortScratch = new (float, int)[targets.Length];
        }

        var focus = m_session.Focus(slot: slot);
        var radius = CandidateRadius;
        var radiusSquared = (radius * radius);
        var count = 0;

        for (var index = 0; (index < targets.Length); index++) {
            var distanceSquared = Vector3.DistanceSquared(value1: targets[index].Focus, value2: focus);

            if (distanceSquared <= radiusSquared) {
                m_sortScratch[count++] = (distanceSquared, index);
            }
        }

        if (count == 0) {
            return 0;
        }

        m_sortScratch.AsSpan(start: 0, length: count).Sort(comparison: static (a, b) => a.DistanceSquared.CompareTo(value: b.DistanceSquared));

        return Math.Min(val1: count, val2: CandidateCap);
    }

    /// <summary>Picks the row under the seat's look ray (the crosshair pick — the precision path).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="selection">The applied selection, when the method returns <see langword="true"/>.</param>
    public bool TryPick(int slot, out EditorSelection selection) {
        selection = default;

        if (!m_picker.TryPick(eye: m_session.Eye(slot: slot), direction: m_session.Facing(slot: slot), target: out var target)) {
            return false;
        }

        selection = ToSelection(target: in target);
        Apply(slot: slot, selection: selection);

        return true;
    }

    /// <summary>Clears the seat's selection.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>Whether a selection existed to clear.</returns>
    public bool Deselect(int slot) {
        if (((uint)slot >= (uint)m_selected.Length) || (m_selected[slot] is null)) {
            return false;
        }

        m_selected[slot] = null;
        m_revision++;

        return true;
    }

    /// <summary>The selectable-row count of the current definition (the HUD's candidate-pool hint).</summary>
    public int TargetCount => m_picker.Targets.Length;

    private void Apply(int slot, in EditorSelection selection) {
        if ((uint)slot >= (uint)m_selected.Length) {
            return;
        }

        if (m_selected[slot] is { } previous && (previous == selection)) {
            return;
        }

        m_selected[slot] = selection;
        m_revision++;
    }

    private Vector3? ResolvePosition(in EditorSelection selection) {
        var definition = m_client.Definition;

        switch (selection.Section) {
            case WorldSection.Scene:
                foreach (var row in definition.Scene.Rows) {
                    if (string.Equals(a: row.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        return row.Center;
                    }
                }

                return null;
            case WorldSection.Screens:
                foreach (var screen in definition.Screens) {
                    if (screen.Index == selection.Index) {
                        return screen.Origin;
                    }
                }

                return null;
            case WorldSection.Spawns:
                foreach (var spawn in definition.SpawnPoints) {
                    if (string.Equals(a: spawn.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        return spawn.Position;
                    }
                }

                return null;
            case WorldSection.Placements:
                foreach (var placement in definition.Placements) {
                    if (string.Equals(a: placement.Id, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        return placement.Position;
                    }
                }

                return null;
            case WorldSection.Cameras:
                foreach (var camera in definition.Cameras) {
                    if (string.Equals(a: camera.Name, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        return (camera switch {
                            WorldCamera.Fixed fixedCamera => fixedCamera.Position,
                            WorldCamera.Anchored { Anchor: WorldAnchor.Entity entity } anchored => (m_client.Position(index: entity.Index) + anchored.Offset),
                            WorldCamera.Anchored { Anchor: WorldAnchor.EntityLeaf leaf } anchored when WorldAvatarCatalog.TryHumanoidRole(token: leaf.Leaf, role: out var role) =>
                                (m_client.Position(index: leaf.Index) + WorldAvatarCatalog.RoleOffset(avatar: leaf.Index, role: role) + anchored.Offset),
                            _ => (Vector3?)null,
                        });
                    }
                }

                return null;
            case WorldSection.Speakers:
                // Fixed/Bed rows are their authored point; an anchored row resolves its anchor approximately (the
                // anchored-camera precedent: root pose + raw offset — the orbit pivot and self-heal read, not the
                // audio pose, which the director resolves exactly).
                foreach (var speaker in definition.Speakers) {
                    if (string.Equals(a: speaker.Name, b: selection.Id, comparisonType: StringComparison.Ordinal)) {
                        return (speaker switch {
                            WorldSpeaker.Fixed fixedSpeaker => fixedSpeaker.Position,
                            WorldSpeaker.Bed bed => bed.Center,
                            WorldSpeaker.Anchored { Anchor: WorldAnchor.Entity entity } anchored => (m_client.Position(index: entity.Index) + anchored.Offset),
                            WorldSpeaker.Anchored { Anchor: WorldAnchor.EntityLeaf leaf } anchored when WorldAvatarCatalog.TryHumanoidRole(token: leaf.Leaf, role: out var role) =>
                                (m_client.Position(index: leaf.Index) + WorldAvatarCatalog.RoleOffset(avatar: leaf.Index, role: role) + anchored.Offset),
                            WorldSpeaker.Anchored { Anchor: WorldAnchor.Placement placementAnchor } anchored => AnchoredPlacementPosition(definition: definition, placementId: placementAnchor.PlacementId, offset: anchored.Offset),
                            _ => (Vector3?)null,
                        });
                    }
                }

                return null;
            default:
                return null;
        }
    }

    // A placement-anchored speaker's approximate focus: the placement root plus the raw offset (null when the
    // placement row departed — the selection then self-heals like any dangling reference).
    private static Vector3? AnchoredPlacementPosition(WorldDefinition definition, string placementId, Vector3 offset) {
        foreach (var placement in definition.Placements) {
            if (string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                return (placement.Position + offset);
            }
        }

        return null;
    }

    private static EditorSelection ToSelection(in EditorPickTarget target) =>
        new(Section: target.Section, Id: target.Id, Index: target.Index);

    private static bool Matches(in EditorPickTarget target, in EditorSelection selection) =>
        ((target.Section == selection.Section) &&
            (target.Index == selection.Index) &&
            string.Equals(a: target.Id, b: selection.Id, comparisonType: StringComparison.Ordinal));
}
