using System.Globalization;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // The effective kit and look each body held before a resync, so only a body whose own selection moved recompiles,
    // and the selections a resync resolves before they are compared against the entries'.
    private byte[]? m_priorKits;
    private byte[]? m_priorLooks;
    private int[]? m_selectedKits;
    private int[]? m_selectedLooks;

    /// <summary>Resyncs what every body takes from its own state cells: its live <see cref="WorldBody.Scale"/> from
    /// <c>bodies.scaleRow</c>, and its kit and look from the kit and look assignments' selectors
    /// (<see cref="WorldRowAssignment.Select"/>). Called from the same <c>WorldServer.Install</c>/
    /// <c>InstallRuntimeStateValue</c> choke points <c>WorldGrants.SyncState</c> is, and from the end-of-tick
    /// publication when a row one of them reads moved, never per tick. A body whose kit moves recompiles it in place,
    /// keeping its pose, velocity and action state; a moved kit or look bumps <see cref="Revision"/>, so the client
    /// rebuilds its program and re-latches that body's look.</summary>
    /// <param name="definition">The live document.</param>
    public void SyncBodyCells(WorldDefinition definition) {
        SyncBodyScale(definition: definition);

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

    // Swaps one live body's compiled feel to a kit row in place: pose, velocity, tape and source survive.
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
    // Resolves every entry's selected kit and look from the assignments' selectors against the definition's stored
    // cells: a body whose cell holds a value a case names takes that case's row, and every other body none (-1).
    // Returns whether any entry's selection moved.
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
    // Writes the row each body's cell of one selector's state row selects, -1 for every body without a cell or whose
    // value no case names. The validator admits only a keyed Int or Bool row of body-index keys read at its stored value,
    // so the tick-0 read is each cell's truth.
    private void SelectRows(WorldDefinition definition, WorldRowSelector? select, bool kits, int[] selected) {
        Array.Fill(
            array: selected,
            value: -1
        );

        if (
            (select is null) ||
            (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: select.State
        ) is not { } row)
        ) {
            return;
        }

        foreach (var cell in (row.Cells ?? [])) {
            if (
                !int.TryParse(
                s: cell.Key,
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var bodyIndex
            ) ||
                (((uint)bodyIndex) >= ((uint)selected.Length)) ||
                !WorldStateReader.TryRead(
                definition: definition,
                rowName: row.Name,
                key: cell.Key.Value,
                tick: 0UL,
                engineTick: 0UL,
                row: out _,
                rawValue: out var raw,
                text: out _
            ) ||
                (raw is not { } value)
            ) {
                continue;
            }

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
