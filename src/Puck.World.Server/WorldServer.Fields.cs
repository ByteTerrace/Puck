using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer : IWorldFieldLatticeHost {
    /// <summary>Describes the field lattice for a console read-back.</summary>
    /// <returns>One line, or a none line when the world declares no <c>fields</c> section.</returns>
    public string DescribeFields() => ((m_population.Fields is { } lattice)
        ? $"[world.fields: {lattice.Describe()}]"
        : "[world.fields: none]"
    );
    // Runs after the rules so a tag a rule wrote this tick is what an emit reads, and before the snapshot so the
    // step's cell writes ride this tick's delivery. `this` is the host — the lattice reaches bodies and state-row
    // cells through the interface below, never a per-tick delegate.
    private void StepFields(ulong tick) {
        if (m_population.Fields is not { } lattice) {
            return;
        }

        lattice.Step(
            bodyCount: m_population.Capacity,
            host: this,
            tick: tick
        );
    }
    FixedVector3? IWorldFieldLatticeHost.BodyPosition(int body) => Body(index: body)?.FixedPosition;
    long IWorldFieldLatticeHost.ReadTag(WorldStateHandle row, int body, ulong tick) => ReadTagCell(
        body: body,
        catalog: m_population.Fields!.Program.StateCatalog,
        row: row,
        tick: tick
    );
    void IWorldFieldLatticeHost.WriteTag(WorldStateHandle row, int body, long value, ulong tick) => WriteTagCell(
        body: body,
        catalog: m_population.Fields!.Program.StateCatalog,
        row: row,
        tick: tick,
        value: value
    );
    FixedQ4816 IWorldFieldLatticeHost.ReadScalar(WorldStateHandle row, ulong tick) => ReadScalarSlot(
        catalog: m_population.Fields!.Program.StateCatalog,
        row: row,
        tick: tick
    );
    void IWorldFieldLatticeHost.AddScalar(WorldStateHandle row, FixedQ4816 amount, ulong tick) => AddScalarSlot(
        catalog: m_population.Fields!.Program.StateCatalog,
        row: row,
        amount: amount,
        tick: tick
    );
    // A reaction scalar's row read: the named row's SLOT cell as Q48.16 raw bits (0 when the row or its slot cell
    // does not exist yet — the slot is minted by its first write).
    private FixedQ4816 ReadScalarSlot(WorldStateCatalog catalog, WorldStateHandle row, ulong tick) => ((WorldStateReader.TryReadHandle(
        catalog: catalog,
        definition: m_definition,
        handle: row,
        key: WorldStateRow.SlotKey,
        tick: tick,
        row: out _,
        rawValue: out var raw,
        text: out _
    ) && (raw is { } value))
        ? FixedQ4816.FromRawBits(value: value)
        : FixedQ4816.Zero
    );
    // A clamped-add write to a scalar slot row: precomputes the already-in-envelope sum itself (ClampToEnvelope is
    // documented for reads, not writes -- an explicit write outside a row's envelope is refused, never silently
    // clamped) so the mutation below can never be refused for a value this method already brought into range.
    private void AddScalarSlot(WorldStateCatalog catalog, WorldStateHandle row, FixedQ4816 amount, ulong tick) {
        if (amount == FixedQ4816.Zero) {
            return;
        }

        if (!WorldStateReader.TryReadHandle(
            catalog: catalog,
            definition: m_definition,
            handle: row,
            key: WorldStateRow.SlotKey,
            tick: tick,
            row: out var declared,
            rawValue: out var raw,
            text: out _
        )) {
            return;
        }

        var current = (raw ?? 0L);
        var sum = (((Int128)current) + amount.Value);
        var clamped = declared.ClampToEnvelope(value: FixedSaturate.ToInt64(value: sum));

        if (clamped == current) {
            return;
        }

        _ = TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.World,
                Row: declared.Name,
                Key: WorldStateRow.SlotKey,
                Value: clamped,
                Kind: WorldDocumentWriteKind.Set
            ),
            tick: tick,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );
        m_output.DeliverDefinition(definition: m_definition);
    }
    // Placement response traits are authored outside the field reaction program and therefore retain their named
    // scalar seam; reaction execution itself always uses the typed overload above.
    private FixedQ4816 ReadScalarSlot(string row, ulong tick) => ((WorldStateReader.TryRead(
        definition: m_definition,
        rowName: row,
        key: WorldStateRow.SlotKey,
        tick: tick,
        row: out _,
        rawValue: out var raw,
        text: out _
    ) && (raw is { } value))
        ? FixedQ4816.FromRawBits(value: value)
        : FixedQ4816.Zero
    );
    private long ReadTagCell(WorldStateCatalog catalog, WorldStateHandle row, int body, ulong tick) => ((WorldStateReader.TryReadHandle(
        catalog: catalog,
        definition: m_definition,
        handle: row,
        key: body.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        tick: tick,
        row: out _,
        rawValue: out var raw,
        text: out _
    ) && (raw is { } value))
        ? value
        : 0L
    );
    private void WriteTagCell(WorldStateCatalog catalog, WorldStateHandle row, int body, long value, ulong tick) {
        if (ReadTagCell(
            body: body,
            catalog: catalog,
            row: row,
            tick: tick
        ) == value) {
            return;
        }

        _ = TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.World,
                Row: catalog[row].Name,
                Key: body.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                Value: value,
                Kind: WorldDocumentWriteKind.Set
            ),
            tick: tick,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );
        m_output.DeliverDefinition(definition: m_definition);
    }
}
