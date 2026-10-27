using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Fields;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick : IFieldLatticeHost {
    private FieldLattice? m_steppingRegion;

    /// <summary>Describes the field lattice for a console read-back.</summary>
    /// <returns>One line, or a none line when the world declares no <c>fields</c> section.</returns>
    internal string DescribeFields() => ((Host.Population.FieldRegions.Count == 0) ? "[world.fields: none]"
        : $"[world.fields: {string.Join(separator: "; ", values: Host.Population.FieldRegions.Select(selector: region => $"{region.Name}: {region.Lattice.Describe()}"))}]");

    /// <summary>Clears departed exposure outputs and advances each physical region on its own cadence.</summary>
    public void StepFields(ulong tick) {
        var regions = Host.Population.FieldRegions;

        // A shared exposure row belongs to the regions containing the body. Clear it only after the body leaves
        // every contributing region, including on a tick when their reaction cadences are idle. No retained
        // contribution cache is needed, so checkpoints keep the same authoritative inputs.
        for (var index = 0; (index < regions.Count); index++) {
            var reactions = regions[index].Lattice.Input.Reactions;

            for (var reaction = 0; (reaction < reactions.Count); reaction++) {
                if (reactions[reaction] is not FieldReactionInput.Expose expose) { continue; }
                for (var body = 0; (body < Host.Population.Capacity); body++) {
                    if ((Host.Body(index: body) is not { } active) || HasExposureRegion(regions, expose.Row, active.FixedPosition)) { continue; }
                    WriteTagCell(catalog: Host.Document.Definition.StateCatalog, row: expose.Row, body: body, value: 0L, tick: tick);
                }
            }
        }
        try {
            for (var index = 0; (index < regions.Count); index++) {
                m_steppingRegion = regions[index].Lattice;
                m_steppingRegion.Step(bodyCount: Host.Population.Capacity, host: this, tick: tick);
            }
        } finally { m_steppingRegion = null; }
    }

    private static bool HasExposureRegion(IReadOnlyList<WorldFieldRegion> regions, StateHandle row, in FixedVector3 position) {
        for (var index = 0; (index < regions.Count); index++) {
            var lattice = regions[index].Lattice;

            if (!lattice.TryBodyCellOf(cell: out _, position: in position)) { continue; }
            var reactions = lattice.Input.Reactions;

            for (var reaction = 0; (reaction < reactions.Count); reaction++) {
                if ((reactions[reaction] is FieldReactionInput.Expose expose) && (expose.Row == row)) { return true; }
            }
        }
        return false;
    }

    /// <summary>Samples the containing region's motion input before body integration.</summary>
    public void SampleTravelResistance(ulong tick) {
        if (Host.Population.FieldRegions.Count == 0) { return; }
        for (var index = 0; (index < Host.Population.Capacity); index++) {
            if (Host.Body(index: index) is not { } body) { continue; }
            var resistance = FixedQ4816.Zero;

            var regions = Host.Population.FieldRegions;

            for (var regionIndex = 0; (regionIndex < regions.Count); regionIndex++) {
                var region = regions[regionIndex];

                if (!region.Lattice.TryCellOf(body.FixedPosition, out _)) { continue; }
                if (region.TravelResistance is { } scalar) {
                    resistance = ((scalar.Row is { } row) ? ReadScalarSlot(row: row, tick: tick) : FixedQ4816.FromDouble(value: (scalar.Literal ?? 0f)));
                }
                break;
            }
            body.SetTravelResistance(resistance: resistance);
        }
    }

    FixedVector3? IFieldLatticeHost.BodyPosition(int body) {
        if (Host.Body(index: body) is not { } active) { return null; }
        var position = active.FixedPosition;

        return (((m_steppingRegion is null) || m_steppingRegion.TryBodyCellOf(cell: out _, position: in position)) ? position : null);
    }
    long IFieldLatticeHost.ReadTag(StateHandle row, int body, ulong tick) => ReadTagCell(
        body: body,
        catalog: Host.Document.Definition.StateCatalog,
        row: row,
        tick: tick
    );
    void IFieldLatticeHost.WriteTag(StateHandle row, int body, long value, ulong tick) => WriteTagCell(
        body: body,
        catalog: Host.Document.Definition.StateCatalog,
        row: row,
        tick: tick,
        value: value
    );
    FixedQ4816 IFieldLatticeHost.ReadScalar(StateHandle row, ulong tick) => ReadScalarSlot(
        catalog: Host.Document.Definition.StateCatalog,
        row: row,
        tick: tick
    );
    void IFieldLatticeHost.AddScalar(StateHandle row, FixedQ4816 amount, ulong tick) => AddScalarSlot(
        catalog: Host.Document.Definition.StateCatalog,
        row: row,
        amount: amount,
        tick: tick
    );

    // A reaction scalar's row read: the named row's SLOT cell as Q48.16 raw bits (0 when the row or its slot cell
    // does not exist yet — the slot is minted by its first write).
    private FixedQ4816 ReadScalarSlot(StateCatalog catalog, StateHandle row, ulong tick) => ((WorldStateReader.TryReadHandle(arena: Host.Arena,
        catalog: catalog,
        definition: Host.Document.Definition,
        handle: row,
        key: WorldStateRow.SlotKey,
        rawValue: out var raw,
        row: out _,
        text: out _,
        tick: tick,
        engineTick: CompletedEngineTicks
    ) && (raw is { } value))
        ? FixedQ4816.FromRawBits(value: value)
        : FixedQ4816.Zero
    );
    // A clamped-add write to a scalar slot row: precomputes the already-in-envelope sum itself (ClampToEnvelope is
    // documented for reads, not writes -- an explicit write outside a row's envelope is refused, never silently
    // clamped) so the mutation below can never be refused for a value this method already brought into range.
    private void AddScalarSlot(StateCatalog catalog, StateHandle row, FixedQ4816 amount, ulong tick) {
        if (amount == FixedQ4816.Zero) {
            return;
        }

        if (!WorldStateReader.TryReadHandle(arena: Host.Arena,
            catalog: catalog,
            definition: Host.Document.Definition,
            handle: row,
            key: WorldStateRow.SlotKey,
            rawValue: out var raw,
            row: out var declared,
            text: out _,
            tick: tick,
            engineTick: CompletedEngineTicks
        )) {
            return;
        }

        var current = (raw ?? 0L);
        var sum = (((Int128)current) + amount.Value);
        var clamped = declared.ClampToEnvelope(value: FixedSaturate.ToInt64(value: sum));

        if (clamped == current) {
            return;
        }

        _ = Host.TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.World,
                Row: declared.Name,
                Key: WorldStateRow.SlotKey,
                Value: clamped,
                Kind: WorldDocumentWriteKind.Set
            ),
            tick: tick,
            engineTick: CompletedEngineTicks,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );
        Host.Document.DeliverPending();
    }
    // Placement response traits are authored outside the field reaction program and therefore name their row rather
    // than carrying a compiled handle; resolves through the document catalog's name -> handle dictionary and then the
    // same handle-based read the typed overload above uses, so this seam costs a dictionary lookup, not a row scan.
    private FixedQ4816 ReadScalarSlot(string row, ulong tick) {
        var catalog = Host.Document.Definition.StateCatalog;

        return (catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row
        )
            ? ReadScalarSlot(
                catalog: catalog,
                row: handle,
                tick: tick
            )
            : FixedQ4816.Zero
        );
    }
    private long ReadTagCell(StateCatalog catalog, StateHandle row, int body, ulong tick) => ((WorldStateReader.TryReadHandle(arena: Host.Arena,
        catalog: catalog,
        definition: Host.Document.Definition,
        handle: row,
        key: IndexKeyCache.Get(index: body),
        tick: tick,
        engineTick: CompletedEngineTicks,
        row: out _,
        rawValue: out var raw,
        text: out _
    ) && (raw is { } value))
        ? value
        : 0L
    );
    private void WriteTagCell(StateCatalog catalog, StateHandle row, int body, long value, ulong tick) {
        if (ReadTagCell(
            body: body,
            catalog: catalog,
            row: row,
            tick: tick
        ) == value) {
            return;
        }

        _ = Host.TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.World,
                Row: catalog[row].Name,
                Key: IndexKeyCache.Get(index: body),
                Value: value,
                Kind: WorldDocumentWriteKind.Set
            ),
            tick: tick,
            engineTick: CompletedEngineTicks,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );
        Host.Document.DeliverPending();
    }
}
