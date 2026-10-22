using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // The effective kit and look each body held before a resync, so only a body whose own selection moved recompiles,
    // and the selections a resync resolves before they are compared against the entries'.
    private byte[]? m_priorKits;
    private byte[]? m_priorLooks;
    private int[]? m_selectedKits;
    private int[]? m_selectedLooks;
    // Whether the last selection pass had a selector to resolve, so the pass after a document drops both selectors
    // still clears what they selected, and a document without one costs nothing per tick.
    private bool m_selecting;

    /// <summary>Resyncs what every body takes from its own registers: its live <see cref="WorldBody.Scale"/> from the
    /// register <c>bodies.scale</c> names (<see cref="SyncBodyScale"/>), and its kit and look from the assignments'
    /// selectors (<see cref="SyncBodySelections"/>). Called from the same <c>WorldServer.Install</c> choke points
    /// <c>WorldGrants.SyncState</c> is, once a tick after the world rules, and when a crossing lands a body.</summary>
    /// <param name="definition">The live document.</param>
    public void SyncBodyCells(WorldDefinition definition) {
        SyncBodyScale(definition: definition);
        SyncBodySelections(definition: definition);
    }
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
        var kit = m_kits[kitIndex];

        body.RecompileKit(
            tuning: kit.Tuning,
            actions: kit.Actions,
            actionThresholds: kit.ActionThresholds,
            actionShapes: kit.ActionShapes,
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
        Array.Fill(
            array: selected,
            value: -1
        );

        if (
            (select is null) ||
            !WorldBodyStateReads.TryResolveSlot(
            definition: definition,
            kind: out var kind,
            name: select.BodyState,
            slot: out var slot
        ) ||
            (kind != ActionStateKind.Counter)
        ) {
            return;
        }

        const long Fraction = ((1L << FixedQ4816.FractionBitCount) - 1L);

        for (var bodyIndex = 0; (bodyIndex < selected.Length); bodyIndex++) {
            if (!m_entries[bodyIndex].Active) {
                continue;
            }

            var raw = m_actionStateLane.Read(
                ordinal: bodyIndex,
                slot: slot
            );

            if ((raw & Fraction) != 0L) {
                continue;
            }

            var value = (raw >> FixedQ4816.FractionBitCount);

            foreach (var selectCase in select.Cases) {
                if (selectCase.Value != value) {
                    continue;
                }

                selected[bodyIndex] = ((kits
                    ? ResolveKitOrNull(name: selectCase.Row)
                    : ResolveLookOrNull(name: selectCase.Row)
                ) ?? -1);

                break;
            }
        }
    }
}
