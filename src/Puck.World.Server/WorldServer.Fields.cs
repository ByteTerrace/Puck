using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Describes the field lattice for a console read-back.</summary>
    /// <returns>One line, or a none line when the world declares no <c>fields</c> section.</returns>
    public string DescribeFields() => ((m_population.Fields is { } lattice)
        ? $"[world.fields: {lattice.Describe()}]"
        : "[world.fields: none]"
    );
    // Runs after the rules so a tag a rule wrote this tick is what an emit reads, and before the snapshot so the
    // step's cell writes ride this tick's delivery.
    private void StepFields(ulong tick) {
        if (m_population.Fields is not { } lattice) {
            return;
        }

        lattice.Step(
            bodyCount: m_population.Capacity,
            bodyPosition: index => Body(index: index)?.FixedPosition,
            readTag: (row, body) => ReadTagCell(
                body: body,
                row: row,
                tick: tick
            ),
            tick: tick,
            writeTag: (row, body, value) => WriteTagCell(
                body: body,
                row: row,
                tick: tick,
                value: value
            )
        );
    }
    private long ReadTagCell(string row, int body, ulong tick) => ((WorldStateReader.TryRead(
        definition: m_definition,
        rowName: row,
        key: body.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        tick: tick,
        row: out _,
        rawValue: out var raw,
        text: out _
    ) && (raw is { } value))
        ? value
        : 0L
    );
    private void WriteTagCell(string row, int body, long value, ulong tick) {
        if (ReadTagCell(
            body: body,
            row: row,
            tick: tick
        ) == value) {
            return;
        }

        _ = TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.World,
                Row: row,
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
