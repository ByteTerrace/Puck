using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>A detachable sink test double that counts what it is handed and ends its own subscription after a given
/// number of definitions or snapshots, counting every delivery that still reaches it afterwards.</summary>
internal sealed class EndingSink(int endAfterDefinitions, int endAfterSnapshots) : IWorldDetachableSink {
    public const string Reason = "test.ended";

    public int Definitions { get; private set; }
    public int DeliveriesAfterEnd { get; private set; }
    public string? DetachReason { get; private set; }
    public int Snapshots { get; private set; }

    private void Note() {
        if (DetachReason is not null) {
            DeliveriesAfterEnd++;
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) {
    }
    public void DeliverComposition(WorldComposition composition) {
    }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        Note();

        if (++Definitions == endAfterDefinitions) {
            DetachReason = Reason;
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) {
    }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Note();

        if (++Snapshots == endAfterSnapshots) {
            DetachReason = Reason;
        }
    }
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => Note();
}
