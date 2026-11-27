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
        var selected = SelectRow(WorldBodyStateReads.PlanFor(definition: definition).KitSelect, definition.Assignment.Select, true, bodyIndex);

        return m_kitRows[((selected >= 0) ? selected : (UsesSeatKit(index: bodyIndex) ? m_seatKit : m_entries[bodyIndex].KitIndex))].Name;
    }
    /// <summary>Resolves a replacement definition's kit against staged named register values before installing it.</summary>
    public string PreviewKitName(int bodyIndex, WorldDefinition definition, Func<string, FixedQ4816?> counter) {
        if ((definition.Assignment.Select is { } selector) && (counter(selector.BodyState) is { } value) &&
            ((value.Value & ((1L << FixedQ4816.FractionBitCount) - 1)) == 0)) {
            foreach (var row in selector.Cases) {
                if (row.Value == (value.Value >> FixedQ4816.FractionBitCount)) { return row.Row; }
            }
        }
        if ((bodyIndex < definition.Population.LocalSeats) || ((((uint)bodyIndex) < ((uint)m_entries.Length)) && m_entries[bodyIndex].IsRemoteHuman)) { return definition.DefaultSeatKit; }
        var rows = ResolveRows(assignment: definition.Assignment, resolve: name => checked((byte)definition.Kits.ToList().FindIndex(match: kit => (kit.Name == name))));

        return definition.Kits[SelectRow(bodyIndex, definition.Assignment, rows, definition.Kits.Count)].Name;
    }
    /// <summary>Rebinds a checkpoint's bank to the exact captured mapping without creating a transition.</summary>
    public void RestoreInputMapping(int bodyIndex, WorldInputOwner owner) {
        if (m_entries[bodyIndex].Body is not { } body) { return; }
        body.RestoreInputMapping(owner.Mapping, m_inputKits[owner.Mapping.Id][ResolveKitIndex(index: bodyIndex)], owner.Epoch);
    }
    /// <summary>Installs an owner's admitted mapping on the body's consumers using precompiled kit bindings. The
    /// steering image kept for the body between producer runs crosses the barrier with the body's own images
    /// (<see cref="WorldBody.InstallInputMapping"/>).</summary>
    public void InstallInputMapping(int bodyIndex, WorldInputMapping previous, WorldInputOwner owner, bool reset, bool recompiled = false) {
        if (m_entries[bodyIndex] is not { Active: true, Body: { } body } entry) { return; }
        body.InstallInputMapping(InputCatalog, previous, owner.Mapping, m_inputKits[owner.Mapping.Id][ResolveKitIndex(index: bodyIndex)], owner.Image, owner.Epoch, reset, owner.LastSwitch.Decisions.Span, ref entry.AutonomyState.SteeringIntent, recompiled);
    }
    // The steering image kept for a body between producer runs, addressed under the body's installed mapping: what a
    // kit step saves before it can install another mapping, and puts back beside the mapping when its sweep is refused
    // (WorldInputRuntime.CompleteKitStep).
    internal Protocol.PlayerIntent SteeringImage(int bodyIndex) => m_entries[bodyIndex].AutonomyState.SteeringIntent;
    internal void RestoreSteeringImage(int bodyIndex, in Protocol.PlayerIntent image) => m_entries[bodyIndex].AutonomyState.SteeringIntent = image;

    // Whether the last selection pass had a selector to resolve, so the pass after a document drops both selectors
    // still clears what they selected, and a document without one costs nothing per tick.
    private bool m_selecting;
    // What the last resync ran against: the document, the population's revision and the register binding. While all
    // three stand, a body's scale, kit and look can only have moved with a touch of its own registers.
    private WorldDefinition? m_bodyCellDefinition;
    private int m_bodyCellRevision = -1;
    private int m_bodyCellBinding = -1;
    // The bodies whose scale, kit or look register was touched since the last resync, in a scope or out of one and
    // by a rewind too, and the drain's scratch.
    private readonly ArenaLaneTouches m_bodyCellTouches = new();
    private int[] m_bodyCellTouched = [];
    private bool m_bodyCellsWatched;

    /// <summary>Resyncs what a body takes from its own registers: its live <see cref="WorldBody.Scale"/> from the
    /// register <c>bodies.scale</c> names, and its kit and look from the assignments' selectors. Only the bodies whose
    /// registers were touched since the last call are resynced, whether the write is committed, open or rewound
    /// (<see cref="ArenaLaneTouches"/>); every body is when the document, the population's revision or the register
    /// binding moved. Called from the same <c>WorldServer.Install</c> choke points <c>WorldGrants.SyncState</c> is,
    /// at every state transaction's commit, once a tick after the world rules, and when a crossing lands a body.</summary>
    /// <param name="definition">The live document.</param>
    /// <returns><see langword="true"/> when a register was touched (even with the value it held), or the document, the
    /// population's revision or the binding moved since the last call, so a resync ran; <see langword="false"/> when
    /// nothing it reads could have moved and it did nothing.</returns>
    public bool SyncBodyCells(WorldDefinition definition) {
        var plan = WorldBodyStateReads.PlanFor(definition: definition);
        var rebound = (!ReferenceEquals(objA: m_bodyCellDefinition, objB: definition) || (m_bodyCellBinding != m_actionStateLane.Binding));

        if (rebound) {
            m_bodyCellsWatched = m_actionStateLane.WatchTouches(slots: [plan.Scale, plan.KitSelect, plan.LookSelect], watch: m_bodyCellTouches);
            m_bodyCellDefinition = definition;
            m_bodyCellBinding = m_actionStateLane.Binding;
        }
        if (!rebound && (m_bodyCellRevision == m_revision) && (!m_bodyCellsWatched || m_bodyCellTouches.IsEmpty)) { return false; }

        var touched = m_bodyCellTouches.Drain(ordinals: ref m_bodyCellTouched, whole: out var whole);

        if (rebound || (m_bodyCellRevision != m_revision) || (m_bodyCellsWatched && whole)) {
            SyncBodyScale(definition: definition);
            SyncBodySelections(definition: definition);
        } else {
            var selecting = ((plan.KitSelect >= 0) || (plan.LookSelect >= 0));
            var moved = false;

            for (var at = 0; (at < touched); at++) {
                var index = m_bodyCellTouched[at];

                if (((uint)index) >= ((uint)m_entries.Length)) { continue; }
                if (plan.Scale >= 0) { SyncBodyScaleAt(holdGrowthByClearance: true, index: index, slot: plan.Scale); }
                if (selecting) { moved |= SyncBodySelectionAt(definition: definition, index: index, plan: plan); }
            }
            if (moved) { m_revision++; }
        }
        m_bodyCellRevision = m_revision;

        return true;
    }

    /// <summary>Reconciles a refused kit step after its body registers have rewound, including the publication revision.</summary>
    internal void RestoreInputEffectSelections(WorldDefinition definition, int revision) {
        SyncBodyCells(definition: definition);
        m_revision = revision;
        m_bodyCellRevision = revision;
    }

    /// <summary>Resyncs every body's kit and look from the kit and look assignments' selectors
    /// (<see cref="WorldRowAssignment.Select"/>), which read each body's own <c>state.body</c> register. A body whose kit
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

        var plan = WorldBodyStateReads.PlanFor(definition: definition);
        var moved = false;

        for (var index = 0; (index < m_entries.Length); index++) {
            moved |= SyncBodySelectionAt(definition: definition, index: index, plan: plan);
        }

        if (moved) {
            m_revision++;
        }
    }

    // Resolves one body's selected kit and look from its own registers and, when its effective kit moved, recompiles
    // it in place. Returns whether its effective kit or look moved.
    private bool SyncBodySelectionAt(int index, WorldDefinition definition, BodyRegisterPlan plan) {
        var kit = ResolveKitIndex(index: index);
        var look = LookIndex(index: index);

        SelectBody(definition: definition, index: index, plan: plan);

        var kitIndex = ResolveKitIndex(index: index);

        if ((kitIndex != kit) && (m_entries[index] is { Active: true, Body: { } body })) {
            // A new collider steps the body out of what it embeds in, which leaves out the rows its passes open.
            using (HoldPasses(index: index)) {
                RecompileBodyKit(
                    body: body,
                    kitIndex: kitIndex
                );
            }
        }

        return ((kitIndex != kit) || (look != LookIndex(index: index)));
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
    // register, recompiling nothing: the tables a rebuild made are read by what it builds next.
    private void ResolveSelections(WorldDefinition definition) {
        var plan = WorldBodyStateReads.PlanFor(definition: definition);

        for (var index = 0; (index < m_entries.Length); index++) {
            SelectBody(definition: definition, index: index, plan: plan);
        }
    }
    // Writes the rows one body's registers select: a body whose register holds a value a case names takes that case's
    // row, and every other body, and a value no case names, none (-1). The validator admits only a state.body Counter
    // and a case value is a whole number, so a register holding a fraction selects nothing.
    private void SelectBody(int index, WorldDefinition definition, BodyRegisterPlan plan) {
        var entry = m_entries[index];

        entry.SelectedKit = SelectRow(bodyIndex: index, kits: true, select: definition.Assignment.Select, slot: plan.KitSelect);
        entry.SelectedLook = SelectRow(bodyIndex: index, kits: false, select: definition.LookAssignment.Select, slot: plan.LookSelect);
    }
    private int SelectRow(int slot, WorldRowSelector? select, bool kits, int bodyIndex) {
        if (
            (select is null) ||
            !m_entries[bodyIndex].Active ||
            (slot < 0)
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
