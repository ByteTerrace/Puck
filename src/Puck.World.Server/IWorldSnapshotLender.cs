using Puck.World.Protocol;

namespace Puck.World.Server;

// The storage one built snapshot lends its subscribers: its entries, the action-state register file their State slices
// point into, and its cell writes.
internal sealed class WorldSnapshotStorage {
    public EntitySnapshot[] Entries { get; set; } = [];
    public long[] State { get; set; } = [];
    public FieldCellDelta[] Cells { get; set; } = [];
}

// The owner of the storage a built snapshot lends, which hands that storage to the outbox for as long as a recorded
// delivery holds it, instead of the outbox copying it. The owner builds its next snapshot into other storage, and the
// outbox returns the surrendered storage once the last record holding it is replayed.
//
// Invariant: storage the owner surrendered is written by no one until it is returned. Enforced by the owner forgetting
// the storage in Surrender and taking storage for its next snapshot only from what Return handed back or from new
// arrays; the outbox's loans are the only callers, and a loan reads the storage and never writes it.
internal interface IWorldSnapshotLender {
    // Whether the entries live in the storage the owner still holds: those of the snapshot it built last.
    bool Lends(ReadOnlyMemory<EntitySnapshot> entries);
    // Gives up the storage the owner's last snapshot lends; the owner builds its next snapshot elsewhere.
    WorldSnapshotStorage Surrender();
    // Hands surrendered storage back for the owner to build a later snapshot into. Safe from any thread.
    void Return(WorldSnapshotStorage storage);
}
