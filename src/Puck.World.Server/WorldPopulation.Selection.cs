using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Reads a counter from the open authoritative state transaction for input selector preflight.</summary>
    public bool TryCandidateInputState(int bodyIndex, WorldDefinition definition, string name, out FixedQ4816 value) {
        value = default;
        if (!WorldBodyStateReads.TryResolveSlot(definition: definition, kind: out var kind, name: name, slot: out var slot) || (kind != ActionStateKind.Counter)) { return false; }
        value = FixedQ4816.FromRawBits(value: m_actionStateLane.Read(ordinal: bodyIndex, slot: slot));
        return true;
    }
    /// <summary>Reads the kit selected by the open arena transaction without changing the installed body kit.</summary>
    public string CandidateKitName(int bodyIndex, WorldDefinition definition) {
        var selected = SelectRow(definition, definition.Assignment.Select, true, bodyIndex);

        return m_kitRows[((selected >= 0) ? selected : ((bodyIndex < LocalSeatCount) ? m_seatKit : m_entries[bodyIndex].KitIndex))].Name;
    }
    /// <summary>Resolves a replacement definition's kit against staged named register values before installing it.</summary>
    public string PreviewKitName(int bodyIndex, WorldDefinition definition, Func<string, FixedQ4816?> counter) {
        if ((definition.Assignment.Select is { } selector) && (counter(selector.BodyState) is { } value) &&
            ((value.Value & ((1L << FixedQ4816.FractionBitCount) - 1)) == 0)) {
            foreach (var row in selector.Cases) {
                if (row.Value == (value.Value >> FixedQ4816.FractionBitCount)) { return row.Row; }
            }
        }
        if (bodyIndex < definition.Population.LocalSeats) { return definition.DefaultSeatKit; }
        var rows = ResolveRows(assignment: definition.Assignment, resolve: name => checked((byte)definition.Kits.ToList().FindIndex(match: kit => (kit.Name == name))));

        return definition.Kits[SelectRow(bodyIndex, definition.Assignment, rows, definition.Kits.Count)].Name;
    }
    /// <summary>Rebinds a checkpoint's bank to the exact captured mapping without creating a transition.</summary>
    public void RestoreInputMapping(int bodyIndex, WorldInputOwner owner) {
        if (m_entries[bodyIndex].Body is not { } body) { return; }
        body.RestoreInputMapping(owner.Mapping, m_inputKits[owner.Mapping.Id][ResolveKitIndex(index: bodyIndex)], owner.Epoch);
    }
    /// <summary>Installs an owner's admitted mapping on the body's consumers using precompiled kit bindings.</summary>
    public void InstallInputMapping(int bodyIndex, WorldInputMapping previous, WorldInputOwner owner, bool reset, bool recompiled = false) {
        if (m_entries[bodyIndex] is not { Active: true, Body: { } body }) { return; }
        body.InstallInputMapping(InputCatalog, previous, owner.Mapping, m_inputKits[owner.Mapping.Id][ResolveKitIndex(index: bodyIndex)], owner.Image, owner.Epoch, reset, owner.LastSwitch.Decisions.Span, recompiled);
    }

    // The effective kit and look each body held before a resync, so only a body whose own selection moved recompiles,
    // and the selections a resync resolves before they are compared against the entries'.
    private byte[]? m_priorKits;
    private byte[]? m_priorLooks;
    private int[]? m_selectedKits;
    private int[]? m_selectedLooks;
    // Whether the last selection pass had a selector to resolve, so the pass after a document drops both selectors
    // still clears what they selected, and a document without one costs nothing per tick.
    private bool m_selecting;
    private WorldDefinition? m_bodyCellDefinition;
    private int m_bodyCellRevision = -1;
    private (ulong Scale, ulong Kit, ulong Look) m_bodyCellGenerations;

    /// <summary>Resyncs what every body takes from its own registers: its live <see cref="WorldBody.Scale"/> from the
    /// register <c>bodies.scale</c> names (<see cref="SyncBodyScale"/>), and its kit and look from the assignments'
    /// selectors (<see cref="SyncBodySelections"/>). Called from the same <c>WorldServer.Install</c> choke points
    /// <c>WorldGrants.SyncState</c> is, once a tick after the world rules, and when a crossing lands a body.</summary>
    /// <param name="definition">The live document.</param>
    public void SyncBodyCells(WorldDefinition definition) {
        var generations = (RegisterGeneration(definition: definition, name: definition.Population.Scale),
            RegisterGeneration(definition: definition, name: definition.Assignment.Select?.BodyState),
            RegisterGeneration(definition: definition, name: definition.LookAssignment.Select?.BodyState));

        if (ReferenceEquals(objA: m_bodyCellDefinition, objB: definition) && (m_bodyCellRevision == m_revision) &&
            (m_bodyCellGenerations == generations)) { return; }
        SyncBodyScale(definition: definition);
        SyncBodySelections(definition: definition);
        m_bodyCellDefinition = definition;
        m_bodyCellRevision = m_revision;
        m_bodyCellGenerations = generations;
    }

    /// <summary>Reconciles a refused kit step after its body registers have rewound, including the publication revision.</summary>
    internal void RestoreInputEffectSelections(WorldDefinition definition, int revision) {
        SyncBodyCells(definition: definition);
        m_revision = revision;
    }

    private ulong RegisterGeneration(WorldDefinition definition, string? name) => (((name is not null) &&
        WorldBodyStateReads.TryResolveSlot(definition: definition, kind: out _, name: name, slot: out var slot))
        ? m_actionStateLane.Generation(slot: slot) : 0);

    /// <summary>Resyncs every body's kit and look from the kit and look assignments' selectors
    /// (<see cref="WorldRowAssignment.Select"/>), which read each body's own <c>state.body</c> register. Called once a
    /// tick after the world rules, the last writer of a register in a tick; from every choke point
    /// <see cref="SyncBodyCells"/> is; and when a crossing lands a body with the registers it carried. A body whose kit
    /// moves recompiles it in place, keeping its pose, velocity and action state; a moved kit or look bumps
    /// <see cref="Revision"/>, so the client rebuilds its program and re-latches that body's look.</summary>
    /// <param name="definition">The live document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncBodySelections(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var selecting = (
            (definition.Assignment.Select is not null) ||
            (definition.LookAssignment.Select is not null)
        );

        if (!selecting && !m_selecting) {
            return;
        }

        m_selecting = selecting;

        var kits = (m_priorKits ??= new byte[m_entries.Length]);
        var looks = (m_priorLooks ??= new byte[m_entries.Length]);

        for (var index = 0; (index < m_entries.Length); index++) {
            kits[index] = ResolveKitIndex(index: index);
            looks[index] = LookIndex(index: index);
        }

        if (!ResolveSelections(definition: definition)) {
            return;
        }

        var moved = false;

        for (var index = 0; (index < m_entries.Length); index++) {
            var kitIndex = ResolveKitIndex(index: index);

            moved |= (looks[index] != LookIndex(index: index));

            if (kitIndex == kits[index]) {
                continue;
            }

            moved = true;

            if (m_entries[index] is { Active: true, Body: { } body }) {
                RecompileBodyKit(
                    body: body,
                    kitIndex: kitIndex
                );
            }
        }

        if (moved) {
            m_revision++;
        }
    }

    // Swaps one live body's compiled feel to a kit row in place: pose, velocity, tape, source and the registers the
    // arena's slot lanes hold survive.
    private void RecompileBodyKit(WorldBody body, byte kitIndex) {
        // A definition replacement installs its owner barrier immediately after the kit rebuild. Its old
        // mapping id may no longer exist; no simulation consumer runs in this preparation interval.
        var mapping = ((body.InputMappingId < m_inputKits.Length) ? body.InputMappingId : InputCatalog.DefaultMapping.Id);
        var kit = m_inputKits[mapping][kitIndex];

        body.RecompileKit(
            tuning: kit.Tuning,
            actions: kit.Actions,
            actionThresholds: kit.ActionThresholds,
            actionShapes: kit.ActionShapes,
            channels: kit.Channels,
            roleMask: kit.RoleMask,
            roleOrdinals: kit.RoleOrdinals,
            actionState: kit.ActionState,
            program: kit.BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            collider: kit.Collider,
            rigid: kit.Rigid,
            carry: kit.Carry,
            tether: kit.Tether,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            holds: kit.Holds
        );
    }
    // Resolves every entry's selected kit and look from the assignments' selectors against each active body's own
    // register: a body whose register holds a value a case names takes that case's row, and every other body none
    // (-1). Returns whether any entry's selection moved.
    private bool ResolveSelections(WorldDefinition definition) {
        var kits = (m_selectedKits ??= new int[m_entries.Length]);
        var looks = (m_selectedLooks ??= new int[m_entries.Length]);

        SelectRows(
            definition: definition,
            kits: true,
            select: definition.Assignment.Select,
            selected: kits
        );
        SelectRows(
            definition: definition,
            kits: false,
            select: definition.LookAssignment.Select,
            selected: looks
        );

        var moved = false;

        for (var index = 0; (index < m_entries.Length); index++) {
            var entry = m_entries[index];

            moved |= ((entry.SelectedKit != kits[index]) || (entry.SelectedLook != looks[index]));
            entry.SelectedKit = kits[index];
            entry.SelectedLook = looks[index];
        }

        return moved;
    }
    // Writes the row each active body's register selects, -1 for every other body and for a value no case names. The
    // validator admits only a state.body Counter and a case value is a whole number, so a register holding a fraction
    // selects nothing.
    private void SelectRows(WorldDefinition definition, WorldRowSelector? select, bool kits, int[] selected) {
        for (var bodyIndex = 0; (bodyIndex < selected.Length); bodyIndex++) {
            selected[bodyIndex] = SelectRow(bodyIndex: bodyIndex, definition: definition, kits: kits, select: select);
        }
    }
    private int SelectRow(WorldDefinition definition, WorldRowSelector? select, bool kits, int bodyIndex) {
        if (
            (select is null) ||
            !m_entries[bodyIndex].Active ||
            !WorldBodyStateReads.TryResolveSlot(
            definition: definition,
            kind: out var kind,
            name: select.BodyState,
            slot: out var slot
        ) ||
            (kind != ActionStateKind.Counter)
        ) {
            return -1;
        }

        const long Fraction = ((1L << FixedQ4816.FractionBitCount) - 1L);

        var raw = m_actionStateLane.Read(
            ordinal: bodyIndex,
            slot: slot
        );

        if ((raw & Fraction) != 0L) {
            return -1;
        }

        var value = (raw >> FixedQ4816.FractionBitCount);

        var cases = select.Cases;

        for (var caseIndex = 0; (caseIndex < cases.Count); caseIndex++) {
            var selectCase = cases[caseIndex];

            if (selectCase.Value != value) {
                continue;
            }

            return ((kits
                ? ResolveKitOrNull(name: selectCase.Row)
                : ResolveLookOrNull(name: selectCase.Row)
            ) ?? -1);

        }
        return -1;
    }
}
