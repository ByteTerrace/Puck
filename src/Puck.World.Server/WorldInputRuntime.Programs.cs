using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

// A WORLD'S OWN PRODUCER IS ADMITTED BY WHAT CHANGED. A body whose source names a producer of its kit hands the input
// runtime one observation a tick (AdmitProgramSnapshot). Admitting it asks who may drive the body, which mapping its
// modes select and what the image adds to the owner's history, and for a body nobody else drives the answers are the
// same tick after tick. So the runtime keeps, for each such owner, the facts its last admission read and what it
// answered (ProgramStanding), and reads those facts again on the next observation: when every one stands, the
// permission and selection steps are skipped, and when the image stands too the admission is skipped whole.
//
// The invariant: a skipped step would have changed nothing and answered what the record holds. What enforces it: the
// record is trusted only after ProgramFactsStand has read every input of the skipped steps afresh, by value or by
// reference, on the call that uses it; nothing invalidates it from afar, so no writer has to remember to. The two
// inputs that are the runtime's own (which owners route a body, and an owner's seat states) clear the record at the
// one place each is written (IndexBodyOwner and RemoveOwner; TrySetSeatState). Where it stops: a skipped admission
// leaves the origin's observation tick and the owner's step counter at the tick of its last full admission; nothing
// reads either for a world's own producer.
public sealed partial class WorldInputRuntime {
    // What a program owner's last full admission read and answered. Reference members are compared by reference: each
    // is replaced, never rewritten, when what it stands for changes.
    private sealed class ProgramStanding {
        public bool Valid;
        public WorldBody? Body;
        public string? Source;
        public WorldChannelTable? Channels;
        public WorldInputCatalog? Catalog;
        public WorldDefinition? Definition;
        public WorldInputProducer? Producer;
        public uint Revision;
        public ulong Epoch;
        public long Kit;
        public long[] Selectors = [];
        public string Engagement = string.Empty;
        // Whether the admitted observation is one the same observation would repeat: no relative channel moved, the
        // producer holds nothing pending and the tick was not an epoch's first.
        public bool Repeats;
        public PlayerIntent Image;
        public PlayerIntent Accepted;
        public PlayerIntent Projection;
    }

    // The owner each body's own producer admits through, by body index: the (World, body, producer) entry of
    // m_controllers, kept so a tick's admission hashes no key.
    private Owner?[] m_programOwners = [];

    // Reads the body's selection registers into the buffer: the kit assignment's selector, then each body-state
    // selector family's counter, in family order. Returns the number written. The buffer holds one more than the
    // selection plan's families, so a caller ensures the plan before it sizes one.
    private int ReadSelectors(int body, Span<long> into) {
        var lane = m_host.Population.ActionStateLane;
        var count = 0;

        if (m_kitSelectSlot >= 0) { into[count++] = lane.Read(ordinal: body, slot: m_kitSelectSlot); }
        foreach (var slot in m_selectorSlots) {
            if (slot >= 0) { into[count++] = lane.Read(ordinal: body, slot: slot); }
        }
        return count;
    }

    // Whether everything a permission refresh reads for a program owner stands as its last admission read it: the
    // body, its source and kit, the document, the drive gate and the owner's session.
    private bool ProgramPermissionStands(Owner owner, ProgramStanding standing, WorldBody? target) => (
        standing.Valid && (target is not null) && ReferenceEquals(objA: target, objB: standing.Body) &&
        ReferenceEquals(objA: target.Source.ProducerName, objB: standing.Source) &&
        ReferenceEquals(objA: m_host.Population.InputCatalog, objB: standing.Catalog) &&
        ReferenceEquals(objA: m_host.Document.Definition, objB: standing.Definition) &&
        (m_host.Population.KitSignature(index: owner.Body) == standing.Kit) &&
        !m_host.GrantTable.TryGetDriveGate(bodyIndex: owner.Body, gateRow: out _) &&
        m_host.GrantTable.IsLiveSession(principal: owner.Principal)
    );

    // Whether every input of an admission's permission and selection steps stands: the permission facts, the body's
    // generation and channel table, the owner's epoch and producer, the selection registers and the engagement. The
    // record is cleared when a co-owner joins or leaves the body, so the routing principal is the world's own.
    private bool ProgramFactsStand(Owner owner, ProgramStanding standing, WorldBody target) {
        var body = owner.Body;

        if (!ProgramPermissionStands(owner: owner, standing: standing, target: target) ||
            !ReferenceEquals(objA: target.Channels, objB: standing.Channels) ||
            (m_host.Population.Generation(index: body) != owner.Generation) ||
            (owner.Input.Revision != standing.Revision) || (owner.Input.Epoch != standing.Epoch) ||
            (owner.Producer is null) || !ReferenceEquals(objA: owner.Producer, objB: standing.Producer) ||
            m_host.Population.IsHumanOccupied(bodyIndex: body)) { return false; }
        EnsureSelectionPlan();
        Span<long> selectors = stackalloc long[(m_selectorSlots.Length + 1)];
        var count = ReadSelectors(body: body, into: selectors);

        return ((count == standing.Selectors.Length) && selectors[..count].SequenceEqual(other: standing.Selectors) &&
            (Engagement(body: body, principal: Principal.World) == standing.Engagement));
    }

    // The body's program owner while every fact of its last admission stands, or null.
    private Owner? StandingProgramOwner(int body, WorldBody target) {
        if ((((uint)body) >= ((uint)m_programOwners.Length)) || (m_programOwners[body] is not { Removed: false, Program: { Valid: true } standing } owner)) { return null; }
        if (ProgramFactsStand(owner: owner, standing: standing, target: target)) { return owner; }
        standing.Valid = false;
        return null;
    }

    // Records what a full admission read and answered, so the next observation can stand on it. A body another owner
    // routes, or one a person occupies, keeps no record: its routing principal and seat states are another owner's.
    private void RecordProgramStanding(Owner owner, WorldBody target, string source, in PlayerIntent image, in PlayerIntent accepted, in PlayerIntent projection, ulong tick) {
        var body = owner.Body;
        var standing = (owner.Program ??= new ProgramStanding());

        standing.Valid = false;
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners) || (owners.Count != 1) || (owner.Producer is not { } producer) ||
            m_host.Population.IsHumanOccupied(bodyIndex: body)) { return; }
        if (m_programOwners.Length != m_host.Population.Capacity) { Array.Resize(array: ref m_programOwners, newSize: m_host.Population.Capacity); }
        m_programOwners[body] = owner;
        EnsureSelectionPlan();
        Span<long> selectors = stackalloc long[(m_selectorSlots.Length + 1)];
        var count = ReadSelectors(body: body, into: selectors);

        if (standing.Selectors.Length != count) { standing.Selectors = new long[count]; }
        selectors[..count].CopyTo(destination: standing.Selectors);
        standing.Body = target;
        standing.Source = source;
        standing.Channels = target.Channels;
        standing.Catalog = m_host.Population.InputCatalog;
        standing.Definition = m_host.Document.Definition;
        standing.Producer = producer;
        standing.Revision = owner.Input.Revision;
        standing.Epoch = owner.Input.Epoch;
        standing.Kit = m_host.Population.KitSignature(index: body);
        standing.Engagement = Engagement(body: body, principal: Principal.World);
        standing.Repeats = (!owner.SnapshotRelative && producer.IsQuiescent && ((owner.Input.Epoch <= 1) || (owner.Input.LastSwitch.Tick != tick)));
        standing.Image = image;
        standing.Accepted = accepted;
        standing.Projection = projection;
        standing.Valid = true;
    }

    // Forgets what the owners routing a body stand on: the set of owners is an input of each one's routing principal.
    private void ForgetProgramStanding(List<Owner> owners) {
        foreach (var owner in owners) {
            if (owner.Program is { } standing) { standing.Valid = false; }
        }
    }
}
