using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A session sink that keeps what a client would hold: the delivered definition and the cells the snapshots
/// carried, read back through the client's own state view.</summary>
internal sealed class SessionReplica : IClientSink {
    private WorldDefinition m_definition = WorldProjection.Undisclosed;
    public WorldDocumentStateView State { get; }
    public List<FieldCellDelta> Deltas { get; } = [];
    public List<long> Versions { get; } = [];
    public int Definitions { get; private set; }
    public int States { get; private set; }
    public int Snapshots { get; private set; }
    public WorldDefinition Definition => m_definition;
    public SessionReplica() => State = new(() => m_definition);
    public long Read(string row, string key) {
        Assert.True(State.TryResolveRow(row, out var ordinal));
        Assert.True(State.TryRead(ordinal, key, false, 0, 0, out var sample));
        return sample.Value.Raw;
    }
    public long ReadSlot(string row) => Read(row: row, key: WorldStateRow.SlotKey.Value);
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        Definitions++;
        Hold(definition, version);
    }
    public void DeliverPresentation(WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        Definitions++;
        Assert.True(rows.TryApply(m_definition, out var changed));
        Hold(changed, version);
    }
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        States++;
        Hold(definition, version);
    }
    private void Hold(WorldDefinition definition, WorldDocumentVersion version) {
        m_definition = definition;
        Versions.Add(version.Sequence);
    }
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) { }
    public void CloseSpan(ulong stream) { }
    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        Snapshots++;
        Deltas.AddRange(snapshot.FieldCells.ToArray());
        Versions.Add(snapshot.Revision);
        State.ApplyFieldCells(snapshot.FieldCells.Span, new int[StateCapacity.MaxRows]);
    }
    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverSessionLever(WorldSessionLever lever) { }
}
