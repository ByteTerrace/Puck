using System.Text;

namespace Puck.World.Protocol.Tests;

// A small-scope model of the companion custody protocol (docs/plans/companion-redesign.md): one home H holding the
// companions' titles, two custodians A and B, one player P, up to two companions that follow it, and a lossy network.
// It models the protocol's records and messages, never the engine's classes. Every durable fact an authority holds
// is a function of its record list (CustodyModel.Fold, checked against the independent CustodyModel.Shadow); the only
// volatile state is leases, attempts, verification answers, retry counters and fault marks, which a crash clears.
// Every handler appends at most one record, and an append can be refused or uncertain, so the explorer reaches the
// state between any two records. CompanionCustodyModelLawTests explores the model under a fault budget and asserts
// the decision's invariants on every reachable state.

public enum Node : byte { H = 0, A = 1, B = 2 }

public static class Ids {
    public const int Player = 1;
    public const int C1 = 2;
    public const int C2 = 3;
    public const int Stranger = 9;
    public const int BotBase = 10;

    public static bool IsCompanion(int id) => (id is Ids.C1 or Ids.C2);
}

/// <summary>One member of a request or record, named by a request-local id that never changes when a cohort is
/// filtered; a companion's id is its incarnation. Epoch and Hop are its custody ticket; Image its durable state
/// version; Follows the id of the player member it follows; Slot and Gen where it stands or lands.</summary>
public sealed record Member(int Id, int Epoch, string Hop, int Image, int? Follows, int Slot, int Gen) {
    public bool IsPlayer => (Id == Ids.Player);
    public string Canon => $"{Id}@{Epoch}/{Hop}/i{Image}/f{(Follows?.ToString() ?? "-")}/s{Slot}g{Gen}";
}

public enum MsgKind : byte {
    Reserve, Reserved, ReserveRefused, HopRequest, HopAnswer, HopQuery, HopQueryReply, Commit, Committed, Missing, Uncertain,
    Status, StatusReply, CustodyQuery, CustodyReply, HopSettled, HopSettledAck,
}

/// <summary>One message in flight. <paramref name="Lane"/> is the sender's process lifetime when it sent: a lane
/// delivers in order, and a crash starts a new lane whose messages may pass an older lane's still-executing ones.</summary>
public sealed record Msg(Node From, Node To, MsgKind Kind, string Key, Member[] Members, string Arg, bool Dup, int Lane = 0) {
    public string Canon => $"{From}>{To}:{Kind}:{Key}:{string.Join(',', Members.Select(static m => m.Canon))}:{Arg}:{(Dup ? 1 : 0)}:{Lane}";
}

public abstract record Rec {
    public abstract string Canon { get; }
}

/// <summary>A source allocated a key before sending anything under it; a later attempt for a shed image names the
/// departure it carries.</summary>
public sealed record Minted(string Key, Node To, int[] MemberIds, string? Image = null, bool Shed = false) : Rec {
    public override string Canon => $"Minted({Key}>{To}:{string.Join(',', MemberIds)}{((Image is null) ? "" : $":for {Image}")}{(Shed ? ":shed" : "")})";
}
public sealed record Leased(string Key, Node From, Member[] Members) : Rec {
    public override string Canon => $"Leased({Key}<{From}:{string.Join(',', Members.Select(static m => m.Canon))})";
}
/// <summary>A key is dead here. <paramref name="Burned"/> names the slots a commit landed members at before it rolled
/// them back: the generations they took outlive the rollback.</summary>
public sealed record Dead(string Key, int[]? Burned = null) : Rec {
    public override string Canon => $"Dead({Key}{(((Burned?.Length ?? 0) > 0) ? $":b{string.Join(',', Burned!)}" : "")})";
}
public sealed record Dep(string Key, Node To, Member[] Members, bool Shed) : Rec {
    public override string Canon => $"Dep({Key}>{To}:{string.Join(',', Members.Select(static m => m.Canon))}{(Shed ? ":shed" : "")})";
}
public sealed record Arr(string Key, Node From, Member[] Members, int[] Readopted) : Rec {
    public override string Canon => $"Arr({Key}<{From}:{string.Join(',', Members.Select(static m => m.Canon))}:r{string.Join(',', Readopted)})";
}
/// <summary>A departure settled: the landed members arrived, the retired members were destroyed on home's definitive
/// refusal, and every other member is restored to the slot it left.</summary>
public sealed record Settle(string Key, int[] Landed, int[] Retired) : Rec {
    public override string Canon => $"Settle({Key}:l{string.Join(',', Landed)}:r{string.Join(',', Retired)})";
}
public sealed record Retire(int Id, string Cause) : Rec {
    public override string Canon => $"Retire({Id}:{Cause})";
}
/// <summary>Home authorized the hop of each listed companion under one key; each entry is its own title's fact.</summary>
public sealed record Hop(string Key, Node From, Node To, (int Id, int Epoch, int Next)[] Members) : Rec {
    public override string Canon => $"Hop({Key}:{From}>{To}:{string.Join(',', Members.Select(static m => $"{m.Id}:{m.Epoch}>{m.Next}"))})";
}
/// <summary>One companion's entry of a <see cref="Hop"/>, as the reducer indexes it.</summary>
public sealed record HopEntry(string Key, int Id, Node From, Node To, int Epoch, int Next);
public sealed record HopDone(string Key, int Id, bool Arrived) : Rec {
    public override string Canon => $"HopDone({Key}:{Id}:{(Arrived ? 1 : 0)})";
}
public sealed record HopAcked(string Key, int Id) : Rec {
    public override string Canon => $"HopAcked({Key}:{Id})";
}
public sealed record Recall(int Id, int NewEpoch) : Rec {
    public override string Canon => $"Recall({Id}:{NewEpoch})";
}
public sealed record Noticed(int Id, string What) : Rec {
    public override string Canon => $"Noticed({Id}:{What})";
}
public sealed record Install(int Id) : Rec {
    public override string Canon => $"Install({Id})";
}
/// <summary>The companion was found separated during the player's <paramref name="Visit"/>th visit here.</summary>
public sealed record Stranded(int Id, int Visit) : Rec {
    public override string Canon => $"Stranded({Id}:v{Visit})";
}
public sealed record GraceElapsed(int Id, int Visit) : Rec {
    public override string Canon => $"Grace({Id}:v{Visit})";
}
/// <summary>The driver wrote the companion's durable state while it stood here at <paramref name="Epoch"/>.</summary>
public sealed record Write(int Id, int Epoch) : Rec {
    public override string Canon => $"Write({Id}@{Epoch})";
}
public sealed record Stranger(int Slot) : Rec {
    public override string Canon => $"Stranger({Slot})";
}
public sealed record Bot(int Slot) : Rec {
    public override string Canon => $"Bot({Slot})";
}
public sealed record PlayerLeft : Rec {
    public override string Canon => "PlayerLeft";
}
public sealed record PlayerJoined : Rec {
    public override string Canon => "PlayerJoined";
}

public enum CustodyKind : byte { Home, Pending, Away, Released }

public sealed class Body {
    public int Id;
    public int Epoch;
    public string Hop = "";
    public int Image;
    public int Slot;
    public int Gen;
    public int? Follows;
    public int FollowsSlot = -1;
    // The generation the followed member landed at: the binding is live while that slot holds that generation.
    public int FollowsGen = -1;
    public string ArrKey = "";
}

public sealed class Custody {
    public CustodyKind Kind = CustodyKind.Home;
    public Node Custodian = Node.H;
    public int Epoch;
    public string PendingKey = "";
    public int HeldSlot;
    public int KnownImage;
    public int MaxIssued;
    public bool Recalled;
    public int RecallImage;
    public int RecallEpoch;
    // Whether the recall's epoch exceeded every epoch issued before it (later issuance is the recalled lineage's own).
    public bool RecallSuperseded = true;
    public bool Installed;
    // The epoch of every recall home recorded for this title; each one owes its own notice.
    public List<int> Recalls = new();
}

/// <summary>What one authority's durable records say.</summary>
public sealed class View {
    public SortedDictionary<int, Body> Bodies = new();
    public int[] SlotGen = new int[CustodyModel.Capacity];
    public Dictionary<int, Custody> Custody = new();
    public Dictionary<string, Dep> Deps = new(StringComparer.Ordinal);
    public Dictionary<string, Minted> Mints = new(StringComparer.Ordinal);
    public HashSet<string> Settled = new(StringComparer.Ordinal);
    public Dictionary<string, Settle> Settles = new(StringComparer.Ordinal);
    public List<Dep> InDoubt = new();
    public Dictionary<string, string> ImageOfAttempt = new(StringComparer.Ordinal);
    public Dictionary<string, string> LatestAttempt = new(StringComparer.Ordinal);
    public HashSet<string> LeasedKeys = new(StringComparer.Ordinal);
    public HashSet<string> DeadKeys = new(StringComparer.Ordinal);
    public Dictionary<string, Arr> Arrs = new(StringComparer.Ordinal);
    public Dictionary<(string Key, int Id), HopEntry> Hops = new();
    public HashSet<(string Key, int Id)> HopsDone = new();
    public HashSet<(string Key, int Id)> Acked = new();
    // Stranding and its grace window belong to one visit of the player here: a later visit opens a new episode.
    public HashSet<(int Id, int Visit)> StrandedIds = new();
    public HashSet<(int Id, int Visit)> GraceIds = new();
    public HashSet<string> Noticed = new(StringComparer.Ordinal);
    public int Humans;
    public int MintedCount;
    public int ShedCount;
    // How many times the player has been placed here (joined, landed, or restored by a rollback).
    public int PlayerVisits;

    public string ImageOf(string key) => (ImageOfAttempt.TryGetValue(key, out var image) ? image : key);
    public bool Has(int id) => Bodies.ContainsKey(key: id);
    public Body? Body(int id) => (Bodies.TryGetValue(key: id, value: out var body) ? body : null);
    public bool Stranded(int id) => StrandedIds.Contains(item: (id, PlayerVisits));
    public bool GraceElapsed(int id) => GraceIds.Contains(item: (id, PlayerVisits));
    // A binding with no generation is home's placement binding: it follows whoever holds the home seat. A visiting
    // binding names the generation its followed member landed at.
    public bool Follows(int id) => (Body(id: id) is { Follows: { } followed, FollowsSlot: var slot, FollowsGen: var gen } && (slot >= 0) && (Body(id: followed) is { } player) && (player.Slot == slot) && ((gen < 0) || (player.Gen == gen)));
    public IEnumerable<(string Key, int Id, bool Arrived)> UnackedSettles(Node node) {
        foreach (var (key, settle) in Settles) {
            if (!Mints.TryGetValue(key: key, value: out var minted) || (minted.To == Node.H) || (node == Node.H)) {
                continue;
            }
            foreach (var id in minted.MemberIds.Where(Ids.IsCompanion)) {
                if (!Acked.Contains(item: (key, id))) {
                    yield return (key, id, settle.Landed.Contains(value: id));
                }
            }
        }
    }
}

public enum Append : byte { None, Refused, UncertainLost, UncertainLanded }

public sealed record Lease(Node From, Member[] Members, int[] RefusedAtCommit);
public sealed record Outbound(Node To, Member[] Members, string Phase, SortedDictionary<int, int> Granted, bool Forged = false); // Authorizing | Reserving | ShedReserving

public sealed class NodeState {
    public readonly Node Id;
    public readonly bool AcceptsRole;
    public List<Rec> Records = new();
    public SortedDictionary<string, Lease> Leases = new(StringComparer.Ordinal);
    public SortedDictionary<string, Outbound> Outbound = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Retries = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Verified = new(StringComparer.Ordinal); // "K:I" -> next epoch, or -1 when home denied
    public SortedDictionary<string, Msg> Held = new(StringComparer.Ordinal);    // reservations waiting on verification
    public SortedDictionary<string, Msg> HeldCommits = new(StringComparer.Ordinal); // commits waiting on verification
    public SortedDictionary<int, int> Sweeps = new();
    // Companions home has definitively refused (Stale or Released) while they still stand here; each retires in a step
    // of its own. Volatile: a crash forgets the answer, and the next contact with home gives it again.
    public SortedDictionary<int, string> RefusedByHome = new();
    public bool Latched;
    public string LatchedOn = "";
    // The index of the uncertain append: records from here on are suspended until recovery reads the log.
    public int SuspendedFrom = int.MaxValue;
    public Append NextAppend;
    // The connection generation to each peer: a crash starts a new one to every peer, a reconnect to one peer only. A
    // request from an older generation may still be executing at the receiver while the new generation's proceed.
    public int[] Connections = new int[3];
    public int Lifetime => Connections.Sum();

    /// <summary>Gets the records this authority can act on now: everything before an uncertain append it is latched on.</summary>
    public IReadOnlyList<Rec> Effective => (Latched && (SuspendedFrom < Records.Count)) ? Records.GetRange(0, SuspendedFrom) : Records;

    /// <summary>Whether this authority cannot yet call its answer about <paramref name="key"/> durable: it is latched on
    /// an uncertain append that concerns the key.</summary>
    public bool UncertainAbout(string key) => Latched && ((LatchedOn.Length == 0) || LatchedOn.Contains($"({key}", StringComparison.Ordinal));

    public NodeState(Node id, bool acceptsRole) {
        Id = id;
        AcceptsRole = acceptsRole;
    }
    public NodeState Clone() => new(id: Id, acceptsRole: AcceptsRole) {
        Records = new List<Rec>(collection: Records),
        Leases = new SortedDictionary<string, Lease>(dictionary: Leases, comparer: StringComparer.Ordinal),
        Outbound = new SortedDictionary<string, Outbound>(dictionary: Outbound.ToDictionary(static p => p.Key, static p => p.Value with { Granted = new SortedDictionary<int, int>(dictionary: p.Value.Granted) }), comparer: StringComparer.Ordinal),
        Retries = new SortedDictionary<string, int>(dictionary: Retries, comparer: StringComparer.Ordinal),
        Verified = new SortedDictionary<string, int>(dictionary: Verified, comparer: StringComparer.Ordinal),
        Held = new SortedDictionary<string, Msg>(dictionary: Held, comparer: StringComparer.Ordinal),
        Sweeps = new SortedDictionary<int, int>(dictionary: Sweeps),
        RefusedByHome = new SortedDictionary<int, string>(dictionary: RefusedByHome),
        Latched = Latched,
        LatchedOn = LatchedOn,
        SuspendedFrom = SuspendedFrom,
        NextAppend = NextAppend,
        Connections = (int[])Connections.Clone(),
        HeldCommits = new SortedDictionary<string, Msg>(dictionary: HeldCommits, comparer: StringComparer.Ordinal),
    };
    public void Crash() {
        for (var i = 0; i < 3; i++) {
            Connections[i]++;
        }
        HeldCommits.Clear();
        Leases.Clear();
        Outbound.Clear();
        Retries.Clear();
        Verified.Clear();
        Held.Clear();
        RefusedByHome.Clear();
        Latched = false;
        LatchedOn = "";
        SuspendedFrom = int.MaxValue;
        NextAppend = Append.None;
    }
    public void Canon(StringBuilder sb) {
        sb.Append(Id).Append('{').Append(string.Join(',', Connections)).Append(Latched ? 'L' : '-').Append(LatchedOn).Append((int)NextAppend).Append('|');
        foreach (var (key, held) in HeldCommits) {
            sb.Append(key).Append('=').Append(held.Canon).Append(';');
        }
        sb.Append('|');
        foreach (var (id, cause) in RefusedByHome) {
            sb.Append(id).Append(':').Append(cause).Append(';');
        }
        sb.Append('|');
        foreach (var rec in Records) {
            sb.Append(rec.Canon).Append(';');
        }
        sb.Append('|');
        foreach (var (key, lease) in Leases) {
            sb.Append(key).Append('<').Append(lease.From).Append(':').Append(string.Join(',', lease.Members.Select(static m => m.Canon))).Append(":x").Append(string.Join(',', lease.RefusedAtCommit)).Append(';');
        }
        sb.Append('|');
        foreach (var (key, outbound) in Outbound) {
            sb.Append(key).Append('>').Append(outbound.To).Append(':').Append(outbound.Phase).Append(':').Append(string.Join(',', outbound.Members.Select(static m => m.Canon))).Append(":g").Append(string.Join(',', outbound.Granted.Select(static g => $"{g.Key}={g.Value}"))).Append(';');
        }
        sb.Append('|');
        foreach (var (key, count) in Retries) {
            sb.Append(key).Append('=').Append(count).Append(';');
        }
        sb.Append('|');
        foreach (var (key, next) in Verified) {
            sb.Append(key).Append('=').Append(next).Append(';');
        }
        sb.Append('|');
        foreach (var (key, held) in Held) {
            sb.Append(key).Append('=').Append(held.Canon).Append(';');
        }
        sb.Append('|');
        foreach (var (id, count) in Sweeps) {
            sb.Append(id).Append('=').Append(count).Append(';');
        }
        sb.Append('}');
    }
}

[Flags]
public enum Fault {
    None = 0,
    Loss = 1,
    Dup = 2,
    Crash = 4,
    Partition = 8,
    Deadline = 16,
    Install = 32,
    Recall = 64,
    Disconnect = 128,
    Forge = 256,
    Log = 512,
    Admission = 1024,
    Write = 2048,
    Census = 4096,
    Reconnect = 8192,
    All = 16383,
}

public sealed class Budget {
    public int Drops, Dups, Crashes, Partitions, Deadlines, Installs, Recalls, Disconnects, Reconnects, Forged, Logs, Admissions, Writes, Censuses, Relinks;
    public int Faults;
    public Fault Allowed = Fault.All;
    // How many faults of one class a trace may carry (partitions and deadlines allow one more).
    public int Limit = 1;

    public bool Allows(Fault fault) => ((Allowed & fault) != 0);
    // Whether something may have been lost, so the protocol's retries have work to do.
    public bool Lossy => ((Drops + Crashes + Partitions + Deadlines + Logs + Relinks) > 0);
    public Budget Clone() => (Budget)MemberwiseClone();
    public string Canon => $"{Drops}{Dups}{Crashes}{Partitions}{Deadlines}{Installs}{Recalls}{Disconnects}{Reconnects}{Forged}{Logs}{Admissions}{Writes}{Censuses}{Relinks}{Faults}";
}

public sealed class ModelState {
    public NodeState[] Nodes;
    public List<Msg> Network = new();
    public bool[,] LinkUp = new bool[3, 3];
    public Node? PlayerAt = Node.H;
    public int Itinerary;
    public Node[] Route = [];
    public int Companions;
    // Slots a custodian offers (home has CustodyModel.Capacity): a cohort larger than this exhausts the destination.
    public int CustodianSlots = CustodyModel.Capacity - 1;
    public Budget Budget = new();
    public List<string> Trace = new();
    public bool Converging;

    public ModelState(bool aAccepts, bool bAccepts, Node[] route, int companions) {
        Route = route;
        Companions = companions;
        Nodes = [new NodeState(id: Node.H, acceptsRole: true), new NodeState(id: Node.A, acceptsRole: aAccepts), new NodeState(id: Node.B, acceptsRole: bAccepts)];
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                LinkUp[i, j] = true;
            }
        }
    }
    private ModelState(NodeState[] nodes) {
        Nodes = nodes;
    }
    public ModelState Clone() => new(nodes: [Nodes[0].Clone(), Nodes[1].Clone(), Nodes[2].Clone()]) {
        Network = new List<Msg>(collection: Network),
        LinkUp = (bool[,])LinkUp.Clone(),
        PlayerAt = PlayerAt,
        Itinerary = Itinerary,
        Route = Route,
        Companions = Companions,
        CustodianSlots = CustodianSlots,
        Converging = Converging,
        Budget = Budget.Clone(),
        Trace = new List<string>(collection: Trace),
    };
    public NodeState At(Node node) => Nodes[(int)node];
    public IEnumerable<int> CompanionIds => Enumerable.Range(Ids.C1, Companions);
    public string Canon() {
        var sb = new StringBuilder();

        sb.Append(PlayerAt?.ToString() ?? "-").Append(Itinerary).Append(Budget.Canon).Append('/');
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                sb.Append(LinkUp[i, j] ? '1' : '0');
            }
        }
        sb.Append('/');
        // Lanes are independent of one another, so they sort; within a lane the order decides the successors, so it
        // is kept.
        foreach (var lane in Network.GroupBy(static m => (m.From, m.To, m.Lane)).OrderBy(static g => $"{g.Key.From}>{g.Key.To}:{g.Key.Lane}", StringComparer.Ordinal)) {
            sb.Append('[');
            foreach (var msg in lane) {
                sb.Append(msg.Canon).Append(';');
            }
            sb.Append(']');
        }
        sb.Append('/');
        foreach (var node in Nodes) {
            node.Canon(sb: sb);
        }

        return sb.ToString();
    }
}

public sealed class ModelViolation(string message) : Exception(message) { }

/// <summary>The protocol: the reducer over records, the message handlers, and the enabled moves of a state.</summary>
public static class CustodyModel {
    public const int Capacity = 4;
    public const int HumanCap = 1;
    public const int SweepLimit = 1;
    public const int RetryLimit = 1;

    // ---- the reducer ----------------------------------------------------------------------------------------------

    public static View Fold(ModelState s, Node at) => Fold(node: s.At(node: at), companions: s.Companions);

    public static View Fold(NodeState node, int companions) {
        var view = new View();
        var superseded = new HashSet<(string, int)>();

        if (node.Id == Node.H) {
            view.Bodies[Ids.Player] = new Body { Id = Ids.Player, Slot = 0 };
            for (var id = Ids.C1; id < (Ids.C1 + companions); id++) {
                view.Bodies[id] = new Body { Id = id, Slot = (id - 1), Follows = Ids.Player, FollowsSlot = 0, FollowsGen = -1 };
                view.Custody[id] = new Custody { HeldSlot = (id - 1) };
            }
        }
        foreach (var rec in node.Effective) {
            switch (rec) {
                case PlayerJoined:
                    view.Bodies[Ids.Player] = new Body { Id = Ids.Player, Slot = 0, Gen = ++view.SlotGen[0] };
                    view.PlayerVisits++;
                    break;
                case PlayerLeft:
                    _ = view.Bodies.Remove(key: Ids.Player);
                    break;
                case Stranger stranger:
                    view.Bodies[Ids.Stranger] = new Body { Id = Ids.Stranger, Slot = stranger.Slot, Gen = ++view.SlotGen[stranger.Slot] };
                    break;
                case Bot bot:
                    view.Bodies[Ids.BotBase + bot.Slot] = new Body { Id = (Ids.BotBase + bot.Slot), Slot = bot.Slot, Gen = ++view.SlotGen[bot.Slot] };
                    break;
                case Minted minted:
                    view.Mints[minted.Key] = minted;
                    view.MintedCount++;
                    if (minted.Image is { } image) {
                        view.ImageOfAttempt[minted.Key] = image;
                        view.LatestAttempt[image] = minted.Key;
                    }
                    break;
                case Leased leased:
                    _ = view.LeasedKeys.Add(item: leased.Key);
                    break;
                case Dead dead:
                    _ = view.DeadKeys.Add(item: dead.Key);
                    foreach (var slot in (dead.Burned ?? [])) {
                        view.SlotGen[slot]++;
                    }
                    break;
                case Arr arr:
                    view.Arrs[arr.Key] = arr;
                    if (arr.Members.Any(static m => m.IsPlayer)) {
                        view.PlayerVisits++;
                    }
                    foreach (var member in arr.Members) {
                        view.SlotGen[member.Slot] = member.Gen;
                        var followed = ((member.Follows is { } f) ? arr.Members.FirstOrDefault(m => m.Id == f) : null);

                        view.Bodies[member.Id] = new Body {
                            Id = member.Id, Epoch = member.Epoch, Hop = member.Hop, Image = member.Image, Slot = member.Slot, Gen = member.Gen, ArrKey = arr.Key,
                            Follows = member.Follows,
                            FollowsSlot = (followed?.Slot ?? -1),
                            FollowsGen = (followed?.Gen ?? -1),
                        };
                        if ((node.Id == Node.H) && arr.Readopted.Contains(value: member.Id) && view.Custody.TryGetValue(key: member.Id, value: out var custody)) {
                            custody.Kind = CustodyKind.Home;
                            custody.Custodian = Node.H;
                            custody.Epoch = member.Epoch;
                            custody.PendingKey = "";
                            custody.KnownImage = member.Image;
                            custody.MaxIssued = Math.Max(custody.MaxIssued, member.Epoch);
                            view.Bodies[member.Id].Follows = Ids.Player;
                            view.Bodies[member.Id].FollowsSlot = 0;
                            view.Bodies[member.Id].FollowsGen = -1;
                        }
                    }
                    break;
                case Dep dep:
                    view.Deps[dep.Key] = dep;
                    if (dep.Shed) {
                        view.ShedCount++;
                    }
                    foreach (var member in dep.Members) {
                        if ((node.Id == Node.H) && view.Custody.TryGetValue(key: member.Id, value: out var custody)) {
                            custody.KnownImage = member.Image;
                        }
                        _ = view.Bodies.Remove(key: member.Id);
                    }
                    break;
                case Settle settle:
                    _ = view.Settled.Add(item: settle.Key);
                    view.Settles[settle.Key] = settle;
                    if (view.Deps.TryGetValue(key: settle.Key, value: out var restored)) {
                        foreach (var member in restored.Members) {
                            if (settle.Landed.Contains(value: member.Id) || settle.Retired.Contains(value: member.Id)) {
                                continue;
                            }
                            // A restored companion abroad follows only a player restored by the same departure; at home it
                            // takes its placement's binding again.
                            var followed = ((member.Follows is { } f) ? restored.Members.FirstOrDefault(m => m.Id == f) : null);
                            var placement = ((node.Id == Node.H) && Ids.IsCompanion(id: member.Id));

                            view.Bodies[member.Id] = new Body {
                                Id = member.Id, Epoch = member.Epoch, Hop = member.Hop, Image = member.Image, Slot = member.Slot, Gen = member.Gen,
                                Follows = (placement ? Ids.Player : member.Follows), FollowsSlot = (placement ? 0 : (followed?.Slot ?? -1)), FollowsGen = (placement ? -1 : (followed?.Gen ?? -1)),
                                ArrKey = ((node.Id == Node.H) ? "" : "restored:" + settle.Key),
                            };
                            if (member.IsPlayer) {
                                view.PlayerVisits++;
                            }
                        }
                    }
                    break;
                case Retire retire:
                    _ = view.Bodies.Remove(key: retire.Id);
                    break;
                case Hop hop:
                    foreach (var (id, epoch, next) in hop.Members) {
                        view.Hops[(hop.Key, id)] = new HopEntry(Key: hop.Key, Id: id, From: hop.From, To: hop.To, Epoch: epoch, Next: next);
                        // A hop whose settlement arrived before it was recorded is cancelled on arrival.
                        if (view.Custody.TryGetValue(key: id, value: out var hopping) && !view.HopsDone.Contains(item: (hop.Key, id))) {
                            hopping.Kind = CustodyKind.Pending;
                            hopping.PendingKey = hop.Key;
                            hopping.MaxIssued = Math.Max(hopping.MaxIssued, next);
                        }
                    }
                    break;
                case HopDone done:
                    _ = view.HopsDone.Add(item: (done.Key, done.Id));
                    // A release waits on the hop that was pending when it was installed; that hop's resolution ends the title.
                    if (view.Custody.TryGetValue(key: done.Id, value: out var ending) && ending.Installed) {
                        if ((ending.Kind == CustodyKind.Pending) && (ending.PendingKey == done.Key)) {
                            ending.Kind = CustodyKind.Released;
                            ending.PendingKey = "";
                        }
                    } else if (view.Hops.TryGetValue(key: (done.Key, done.Id), value: out var settledHop) && view.Custody.TryGetValue(key: done.Id, value: out var title) && !superseded.Contains(item: (done.Key, done.Id))) {
                        if (done.Arrived) {
                            title.Kind = CustodyKind.Away;
                            title.Custodian = settledHop.To;
                            title.Epoch = settledHop.Next;
                        } else {
                            title.Kind = ((settledHop.From == Node.H) ? CustodyKind.Home : CustodyKind.Away);
                            title.Custodian = settledHop.From;
                            title.Epoch = settledHop.Epoch;
                        }
                        title.PendingKey = "";
                    }
                    break;
                case HopAcked acked:
                    _ = view.Acked.Add(item: (acked.Key, acked.Id));
                    break;
                case Recall recall:
                    if (view.Custody.TryGetValue(key: recall.Id, value: out var recalled)) {
                        foreach (var pending in view.Hops.Keys.Where(k => (k.Id == recall.Id) && !view.HopsDone.Contains(item: k))) {
                            _ = superseded.Add(item: pending);
                        }
                        recalled.RecallSuperseded = (recall.NewEpoch > recalled.MaxIssued);
                        recalled.Kind = CustodyKind.Home;
                        recalled.Custodian = Node.H;
                        recalled.Epoch = recall.NewEpoch;
                        recalled.MaxIssued = Math.Max(recalled.MaxIssued, recall.NewEpoch);
                        recalled.PendingKey = "";
                        recalled.Recalled = true;
                        recalled.RecallImage = recalled.KnownImage;
                        recalled.RecallEpoch = recall.NewEpoch;
                        recalled.Recalls.Add(item: recall.NewEpoch);
                        view.Bodies[recall.Id] = new Body { Id = recall.Id, Epoch = recall.NewEpoch, Image = recalled.KnownImage, Slot = recalled.HeldSlot, Gen = ++view.SlotGen[recalled.HeldSlot], Follows = Ids.Player, FollowsSlot = 0, FollowsGen = -1 };
                    }
                    break;
                case Noticed noticed:
                    _ = view.Noticed.Add(item: $"{noticed.Id}:{noticed.What}");
                    break;
                case Install install:
                    if (view.Custody.TryGetValue(key: install.Id, value: out var released)) {
                        released.Installed = true;
                        // A pending hop's destination may hold home's confirmation for its commit: the title stays
                        // pending until that hop resolves, and is released then.
                        if (released.Kind != CustodyKind.Pending) {
                            released.Kind = CustodyKind.Released;
                            released.PendingKey = "";
                        }
                        _ = view.Bodies.Remove(key: install.Id);
                    }
                    break;
                case Stranded stranded:
                    _ = view.StrandedIds.Add(item: (stranded.Id, stranded.Visit));
                    break;
                case GraceElapsed grace:
                    _ = view.GraceIds.Add(item: (grace.Id, grace.Visit));
                    break;
                case Write write:
                    if (view.Body(id: write.Id) is { } written) {
                        written.Image++;
                    }
                    break;
            }
        }
        foreach (var dep in view.Deps.Values) {
            if (!view.Settled.Contains(item: dep.Key)) {
                view.InDoubt.Add(item: dep);
            }
        }
        view.Humans = view.Bodies.Values.Count(static b => (b.Id is Ids.Player or Ids.Stranger));
        return view;
    }

    /// <summary>One body as the shadow derivation sees it: epoch, slot and generation, image, and the recorded binding
    /// with its followed generation.</summary>
    public readonly record struct ShadowBody(int Epoch, int Slot, int Gen, int Image, int Follows, int FollowsSlot, int FollowsGen) {
        public override string ToString() => $"@{Epoch}/s{Slot}g{Gen}/i{Image}/f{Follows}@{FollowsSlot}g{FollowsGen}";
    }

    /// <summary>An independent derivation of the facts by scanning each node's records backwards: which ids are
    /// embodied, at which epoch, slot and image, following whom at which slot, and each title's custody at home. The
    /// law compares it with the reducer.</summary>
    public static (SortedDictionary<int, ShadowBody> Embodied, SortedDictionary<int, string> Custody) Shadow(NodeState node, int companions) {
        var embodied = new SortedDictionary<int, ShadowBody>();
        var custody = new SortedDictionary<int, string>();
        var records = node.Effective;
        var candidates = new List<int> { Ids.Player, Ids.Stranger };

        for (var id = Ids.C1; id < (Ids.C1 + companions); id++) {
            candidates.Add(item: id);
        }
        for (var slot = 0; slot < Capacity; slot++) {
            candidates.Add(item: Ids.BotBase + slot);
        }
        int WritesAfter(int id, int index) => records.Skip(index + 1).Count(r => (r is Write w) && (w.Id == id));
        // The generation a slot holds after the placements recorded up to and including index: arrivals carry theirs,
        // every other placement advances it by one.
        int SlotGenAt(int slot, int index) {
            var gen = 0;

            for (var j = 0; j <= index; j++) {
                switch (records[j]) {
                    case Arr arr when arr.Members.FirstOrDefault(m => m.Slot == slot) is { } landed:
                        gen = landed.Gen;
                        break;
                    case PlayerJoined when (slot == 0):
                    case Stranger stranger when (stranger.Slot == slot):
                    case Bot bot when (bot.Slot == slot):
                        gen++;
                        break;
                    case Recall recall when ((recall.Id - 1) == slot):
                        gen++;
                        break;
                    case Dead dead when (dead.Burned ?? []).Contains(value: slot):
                        gen++;
                        break;
                }
            }
            return gen;
        }
        int KnownImageBefore(int id, int index) {
            for (var j = index - 1; j >= 0; j--) {
                switch (records[j]) {
                    case Dep dep when dep.Members.FirstOrDefault(m => m.Id == id) is { } left:
                        return left.Image;
                    case Arr arr when arr.Readopted.Contains(value: id):
                        return arr.Members.First(m => m.Id == id).Image;
                }
            }
            return 0;
        }
        foreach (var id in candidates) {
            var decided = false;

            for (var i = records.Count - 1; (i >= 0) && !decided; i--) {
                switch (records[i]) {
                    case PlayerJoined when (id == Ids.Player):
                        embodied[id] = new ShadowBody(Epoch: 0, Slot: 0, Gen: SlotGenAt(slot: 0, index: i), Image: 0, Follows: -1, FollowsSlot: -1, FollowsGen: -1);
                        decided = true;
                        break;
                    case PlayerLeft when (id == Ids.Player):
                        decided = true;
                        break;
                    case Stranger stranger when (id == Ids.Stranger):
                        embodied[id] = new ShadowBody(Epoch: 0, Slot: stranger.Slot, Gen: SlotGenAt(slot: stranger.Slot, index: i), Image: 0, Follows: -1, FollowsSlot: -1, FollowsGen: -1);
                        decided = true;
                        break;
                    case Bot bot when (id == (Ids.BotBase + bot.Slot)):
                        embodied[id] = new ShadowBody(Epoch: 0, Slot: bot.Slot, Gen: SlotGenAt(slot: bot.Slot, index: i), Image: 0, Follows: -1, FollowsSlot: -1, FollowsGen: -1);
                        decided = true;
                        break;
                    case Arr arr when arr.Members.FirstOrDefault(m => m.Id == id) is { } landed: {
                        var readopted = (node.Id == Node.H) && arr.Readopted.Contains(value: id);
                        var followed = ((landed.Follows is { } f) ? arr.Members.FirstOrDefault(m => m.Id == f) : null);
                        var follows = (readopted ? Ids.Player : (landed.Follows ?? -1));
                        var followsSlot = (readopted ? 0 : (followed?.Slot ?? -1));
                        var followsGen = (readopted ? -1 : (followed?.Gen ?? -1));

                        embodied[id] = new ShadowBody(Epoch: landed.Epoch, Slot: landed.Slot, Gen: landed.Gen, Image: landed.Image + WritesAfter(id: id, index: i), Follows: follows, FollowsSlot: followsSlot, FollowsGen: followsGen);
                        decided = true;
                        break;
                    }
                    case Settle settle: {
                        var dep = records.OfType<Dep>().FirstOrDefault(d => d.Key == settle.Key);

                        // A settlement embodies only what it restores; a landed or retired member's absence dates from
                        // the departure itself, and anything that re-embodied the id since then is found first.
                        if ((dep is not null) && (dep.Members.FirstOrDefault(m => m.Id == id) is { } restored) && !settle.Landed.Contains(value: id) && !settle.Retired.Contains(value: id)) {
                            var followed = ((restored.Follows is { } f) ? dep.Members.FirstOrDefault(m => m.Id == f) : null);

                            embodied[id] = (((node.Id == Node.H) && Ids.IsCompanion(id: id))
                                ? new ShadowBody(Epoch: restored.Epoch, Slot: restored.Slot, Gen: restored.Gen, Image: restored.Image + WritesAfter(id: id, index: i), Follows: Ids.Player, FollowsSlot: 0, FollowsGen: -1)
                                : new ShadowBody(Epoch: restored.Epoch, Slot: restored.Slot, Gen: restored.Gen, Image: restored.Image + WritesAfter(id: id, index: i), Follows: restored.Follows ?? -1, FollowsSlot: (followed?.Slot ?? -1), FollowsGen: (followed?.Gen ?? -1)));
                            decided = true;
                        }
                        break;
                    }
                    case Dep dep when dep.Members.Any(m => m.Id == id):
                        decided = true;
                        break;
                    case Retire retire when (retire.Id == id):
                    case Install install when (install.Id == id):
                        decided = true;
                        break;
                    case Recall recall when (recall.Id == id):
                        embodied[id] = new ShadowBody(Epoch: recall.NewEpoch, Slot: id - 1, Gen: SlotGenAt(slot: id - 1, index: i), Image: KnownImageBefore(id: id, index: i) + WritesAfter(id: id, index: i), Follows: Ids.Player, FollowsSlot: 0, FollowsGen: -1);
                        decided = true;
                        break;
                }
            }
            if (!decided && (node.Id == Node.H) && ((id == Ids.Player) || (Ids.IsCompanion(id: id) && (id < (Ids.C1 + companions))))) {
                embodied[id] = ((id == Ids.Player)
                    ? new ShadowBody(Epoch: 0, Slot: 0, Gen: 0, Image: 0, Follows: -1, FollowsSlot: -1, FollowsGen: -1)
                    : new ShadowBody(Epoch: 0, Slot: id - 1, Gen: 0, Image: WritesAfter(id: id, index: -1), Follows: Ids.Player, FollowsSlot: 0, FollowsGen: -1));
            }
        }
        if (node.Id != Node.H) {
            return (embodied, custody);
        }
        for (var id = Ids.C1; id < (Ids.C1 + companions); id++) {
            var installAt = records.ToList().FindIndex(r => (r is Install install) && (install.Id == id));

            if (installAt < 0) {
                custody[id] = CustodyBefore(id: id, count: records.Count);
                continue;
            }
            // A release installed while a hop was pending ends the title once that hop resolves.
            var before = CustodyBefore(id: id, count: installAt);
            var pendingKey = (before.StartsWith("Pending:", StringComparison.Ordinal) ? before["Pending:".Length..] : null);

            custody[id] = (((pendingKey is not null) && !records.Skip(installAt + 1).Any(r => (r is HopDone d) && (d.Id == id) && (d.Key == pendingKey))) ? before : "Released");
        }
        return (embodied, custody);

        string CustodyBefore(int id, int count) {
            for (var i = count - 1; i >= 0; i--) {
                switch (records[i]) {
                    case Arr arr when arr.Readopted.Contains(value: id):
                        return $"Home:H@{arr.Members.First(m => m.Id == id).Epoch}";
                    case Recall recall when (recall.Id == id):
                        return $"Home:H@{recall.NewEpoch}";
                    case HopDone done when (done.Id == id): {
                        var hopAt = records.Take(i).ToList().FindIndex(r => (r is Hop h) && (h.Key == done.Key) && h.Members.Any(m => m.Id == id));

                        // A settlement recorded before its hop cancels that hop; it moves no custody itself.
                        if (hopAt < 0) {
                            break;
                        }
                        var recalledBetween = records.Skip(hopAt).Take(i - hopAt).Any(r => (r is Recall rc) && (rc.Id == id));

                        if (recalledBetween) {
                            break;
                        }
                        var hop = (Hop)records[hopAt];
                        var entry = hop.Members.First(m => m.Id == id);

                        return (done.Arrived ? $"Away:{hop.To}@{entry.Next}" : ((hop.From == Node.H) ? $"Home:H@{entry.Epoch}" : $"Away:{hop.From}@{entry.Epoch}"));
                    }
                    case Hop hop when hop.Members.Any(m => m.Id == id):
                        // Cancelled before it was recorded: no custody effect.
                        if (records.Take(i).Any(r => (r is HopDone d) && (d.Key == hop.Key) && (d.Id == id))) {
                            break;
                        }
                        return $"Pending:{hop.Key}";
                }
            }
            return "Home:H@0";
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static void Send(ModelState s, Node from, Node to, MsgKind kind, string key, Member[] members, string arg = "") {
        s.Network.Add(item: new Msg(From: from, To: to, Kind: kind, Key: key, Members: members, Arg: arg, Dup: false, Lane: s.At(node: from).Connections[(int)to]));
    }
    /// <summary>The messages deliverable now: the head of every ordered lane whose link is up.</summary>
    public static IEnumerable<Msg> LaneHeads(ModelState s) {
        var heads = new HashSet<(Node, Node, int)>();

        foreach (var msg in s.Network) {
            if (heads.Add(item: (msg.From, msg.To, msg.Lane)) && s.LinkUp[(int)msg.From, (int)msg.To]) {
                yield return msg;
            }
        }
    }
    /// <summary>Appends one durable record, or fails: refused (nothing written), or uncertain (written or not, and the
    /// authority latches and writes nothing more until it recovers).</summary>
    public static bool TryRecord(NodeState node, Rec rec) {
        if (node.Latched) {
            return false;
        }
        switch (node.NextAppend) {
            case Append.Refused:
                node.NextAppend = Append.None;
                return false;
            case Append.UncertainLost:
                node.NextAppend = Append.None;
                node.Latched = true;
                node.LatchedOn = rec.Canon;
                return false;
            case Append.UncertainLanded:
                node.NextAppend = Append.None;
                node.Latched = true;
                node.LatchedOn = rec.Canon;
                // Landed but suspended: the authority acts on it only once recovery has read the log.
                node.SuspendedFrom = node.Records.Count;
                node.Records.Add(item: rec);
                return false;
            default:
                node.Records.Add(item: rec);
                return true;
        }
    }
    private static string Mint(View view, Node at) => $"{at}{view.MintedCount + 1}";
    private static Member Carry(Body body, int? follows) => new(Id: body.Id, Epoch: body.Epoch, Hop: body.Hop, Image: body.Image, Follows: follows, Slot: body.Slot, Gen: body.Gen);
    // A request counts as in flight only on the sender's current connection to its receiver: one sent on a connection
    // since replaced may still execute there, but nothing will answer it on this one, so the sender sends again.
    private static bool InFlight(ModelState s, Node from, string key, MsgKind kind, string arg = "") => s.Network.Any(m => (m.From == from) && (m.Key == key) && (m.Kind == kind) && (m.Lane == s.At(node: from).Connections[(int)m.To]) && ((arg.Length == 0) || m.Arg.StartsWith(arg, StringComparison.Ordinal)));
    // A destination asks home once for its reservation (phase R) and again for its commit (phase C); an answer counts
    // only for the request of its own phase that is waiting for it, so neither a late nor a duplicated answer to the
    // reservation's question can stand for the commit's.
    private static string Verification(string key, int id, char phase) => $"{key}:{id}:{phase}";
    private static bool Reachable(ModelState s, Node a, Node b) => (a == b) || (s.LinkUp[(int)a, (int)b] && s.LinkUp[(int)b, (int)a]);
    // Custodians offer fewer slots than home, so a cohort can exhaust a destination's companion capacity.
    private static int FreeSlot(ModelState s, View view, NodeState node, HashSet<int> taken) {
        for (var slot = 0; slot < ((node.Id == Node.H) ? Capacity : s.CustodianSlots); slot++) {
            // A slot a departure in doubt left is held for the body that may be restored to it.
            if (view.Bodies.Values.Any(b => b.Slot == slot) || taken.Contains(item: slot) || node.Leases.Values.Any(l => l.Members.Any(m => m.Slot == slot)) || view.InDoubt.Any(d => d.Members.Any(m => m.Slot == slot))) {
                continue;
            }
            if ((node.Id == Node.H) && ((slot == 0) || view.Custody.Values.Any(c => (c.HeldSlot == slot) && (c.Kind is CustodyKind.Pending or CustodyKind.Away)))) {
                continue;
            }
            return slot;
        }
        return -1;
    }

    // ---- handlers -------------------------------------------------------------------------------------------------

    public static void Deliver(ModelState s, Msg msg) {
        var to = s.At(node: msg.To);
        var view = Fold(s: s, at: msg.To);

        switch (msg.Kind) {
            case MsgKind.Reserve: OnReserve(s: s, to: to, view: view, msg: msg); break;
            case MsgKind.Reserved: OnReserved(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.ReserveRefused: OnReserveRefused(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.HopRequest: OnHopRequest(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopAnswer: OnHopAnswer(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.HopQuery: OnHopQuery(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopQueryReply: OnHopQueryReply(s: s, at: to, msg: msg); break;
            case MsgKind.Commit: OnCommit(s: s, to: to, view: view, msg: msg); break;
            case MsgKind.Committed: OnCommitted(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.Missing: OnMissing(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.Uncertain: break;
            case MsgKind.Status: OnStatus(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.StatusReply: OnStatusReply(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.CustodyQuery: OnCustodyQuery(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.CustodyReply: OnCustodyReply(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopSettled: OnHopSettled(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopSettledAck:
                if (!view.Acked.Contains(item: (msg.Key, int.Parse(s: msg.Arg)))) {
                    _ = TryRecord(node: to, rec: new HopAcked(Key: msg.Key, Id: int.Parse(s: msg.Arg)));
                }
                break;
        }
    }

    // The destination decides each member on its own. A player is admitted under its own door and cap; a companion for a
    // declared role when no body or live lease here names its incarnation and home confirms the hop it rides; at home a
    // companion is a return, verified against the title and bound to its held slot.
    private static void OnReserve(ModelState s, NodeState to, View view, Msg msg) {
        if (to.Leases.TryGetValue(key: msg.Key, value: out var existing)) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: existing.Members, arg: "");
            return;
        }
        if (view.Arrs.TryGetValue(key: msg.Key, value: out var landed)) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: landed.Members, arg: "");
            return;
        }
        // A key is leased once: a lease this authority lost or a commit it refused ends the key.
        if (view.DeadKeys.Contains(item: msg.Key) || view.LeasedKeys.Contains(item: msg.Key)) {
            if (!view.DeadKeys.Contains(item: msg.Key)) {
                _ = TryRecord(node: to, rec: new Dead(Key: msg.Key));
            }
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Dead");
            return;
        }
        if (to.Latched) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Unavailable");
            return;
        }

        var accepted = new List<Member>();
        var refusals = new List<string>();
        var taken = new HashSet<int>();
        var player = msg.Members.FirstOrDefault(static m => m.IsPlayer);

        if (player is not null) {
            if ((view.Humans + 1) > HumanCap) {
                Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Player");
                return;
            }
            var slot = ((to.Id == Node.H) ? 0 : FreeSlot(s: s, view: view, node: to, taken: taken));

            if ((slot < 0) || ((to.Id == Node.H) && view.Bodies.Values.Any(static b => b.Slot == 0))) {
                Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Player");
                return;
            }
            _ = taken.Add(item: slot);
            accepted.Add(item: player with { Slot = slot });
        }
        foreach (var member in msg.Members.Where(static m => !m.IsPlayer)) {
            string? refusal = null;
            var slot = -1;

            if (to.Id == Node.H) {
                refusal = VerifyReturn(s: s, home: to, view: view, from: msg.From, member: member);
                slot = (view.Custody.TryGetValue(key: member.Id, value: out var custody) ? custody.HeldSlot : -1);
                if ((refusal is null) && to.Leases.Values.Any(l => l.Members.Any(m => m.Id == member.Id))) {
                    refusal = "Duplicate";
                }
            } else if (!to.AcceptsRole) {
                refusal = "Role";
            } else if (view.Has(id: member.Id) || to.Leases.Values.Any(l => l.Members.Any(m => m.Id == member.Id))) {
                refusal = "Duplicate";
            } else {
                // Home's authorization of this hop, verified with home itself, never taken from the sender; one query
                // per key asks about every companion the reservation carries.
                if (!to.Verified.TryGetValue(key: Verification(key: msg.Key, id: member.Id, phase: 'R'), value: out var next)) {
                    if (!InFlight(s: s, from: to.Id, key: msg.Key, kind: MsgKind.HopQuery, arg: "R|")) {
                        Send(s: s, from: to.Id, to: Node.H, kind: MsgKind.HopQuery, key: msg.Key, members: [], arg: "R|" + string.Join(',', msg.Members.Where(static m => !m.IsPlayer).Select(static m => m.Id)));
                    }
                    to.Held[msg.Key] = msg;
                    return;
                }
                // The ticket must be the grant's: a companion lands at the epoch home issued, never at one the sender names.
                if ((next < 0) || (member.Epoch != (next - 1))) {
                    refusal = "Unverified";
                } else {
                    slot = FreeSlot(s: s, view: view, node: to, taken: taken);
                    if (slot < 0) {
                        refusal = "Full";
                    }
                }
            }
            if (refusal is null) {
                _ = taken.Add(item: slot);
                accepted.Add(item: member with { Slot = slot });
            } else {
                refusals.Add(item: $"{member.Id}:{refusal}");
            }
        }
        _ = to.Held.Remove(key: msg.Key);
        if (accepted.Count == 0) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: ((refusals.Count > 0) ? refusals[0].Split(':')[1] : "Empty"));
            return;
        }
        if (!TryRecord(node: to, rec: new Leased(Key: msg.Key, From: msg.From, Members: accepted.ToArray()))) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Unavailable");
            return;
        }
        to.Leases[msg.Key] = new Lease(From: msg.From, Members: accepted.ToArray(), RefusedAtCommit: []);
        // The reservation's answer is spent: the commit asks home again, so a companion lands only under the grant as it
        // stands at the commit.
        foreach (var spent in to.Verified.Keys.Where(k => k.StartsWith(msg.Key + ":", StringComparison.Ordinal)).ToArray()) {
            _ = to.Verified.Remove(key: spent);
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: accepted.ToArray(), arg: string.Join(';', refusals));
    }
    // Home's one verification door for a returning companion: the exact custodian at the exact epoch. A pending hop is
    // resolved, never answered "no row".
    private static string? VerifyReturn(ModelState s, NodeState home, View view, Node from, Member member) {
        if (!view.Custody.TryGetValue(key: member.Id, value: out var custody)) {
            return "NotMyTitle";
        }
        switch (custody.Kind) {
            case CustodyKind.Released:
                return "Released";
            case CustodyKind.Pending: {
                // Only a party to the pending hop makes home resolve it; anyone else is simply not the custodian.
                var hop = view.Hops[(custody.PendingKey, member.Id)];

                if ((from != hop.From) && (from != hop.To)) {
                    return "Stale";
                }
                ResolvePending(s: s, view: view, id: member.Id);
                return "Pending";
            }
            case CustodyKind.Home:
                return "Stale";
            default:
                if ((custody.Custodian != from) || (custody.Epoch != member.Epoch)) {
                    return "Stale";
                }
                if (view.Has(id: member.Id)) {
                    throw new ModelViolation(message: $"home embodies {member.Id} while its custody record says away");
                }
                return null;
        }
    }
    // Home asks the pending hop's destination what it holds for the incarnation, as the title authority (section 5.3).
    private static void ResolvePending(ModelState s, View view, int id) {
        var custody = view.Custody[id];
        var hop = view.Hops[(custody.PendingKey, id)];

        if (!InFlight(s: s, from: Node.H, key: hop.Key, kind: MsgKind.CustodyQuery, arg: id.ToString())) {
            Send(s: s, from: Node.H, to: hop.To, kind: MsgKind.CustodyQuery, key: hop.Key, members: [], arg: id.ToString());
        }
    }
    private static bool BodiesPresent(View view, Member[] members) => members.All(m => view.Has(id: m.Id));
    private static void OnReserved(ModelState s, NodeState at, View view, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound)) {
            return;
        }
        var acceptedIds = msg.Members.Select(static m => m.Id).ToHashSet();
        var refused = outbound.Members.Where(m => !acceptedIds.Contains(item: m.Id)).ToArray();
        var refusalText = msg.Arg;

        if (outbound.Phase == "ShedReserving") {
            if (refused.Length > 0) {
                return;
            }
            _ = at.Outbound.Remove(key: msg.Key);
            Send(s: s, from: at.Id, to: outbound.To, kind: MsgKind.Commit, key: msg.Key, members: outbound.Members);
            return;
        }
        if (outbound.Phase != "Reserving") {
            return;
        }
        // A forger commits whatever it reserved, with no body and no record of its own.
        if (outbound.Forged) {
            _ = at.Outbound.Remove(key: msg.Key);
            if (acceptedIds.Count > 0) {
                Send(s: s, from: at.Id, to: outbound.To, kind: MsgKind.Commit, key: msg.Key, members: outbound.Members.Where(m => acceptedIds.Contains(item: m.Id)).ToArray());
            }
            return;
        }
        // Another lease still names a refused incarnation at the destination: it expires, and this request asks again.
        if (refused.Any(m => refusalText.Contains($"{m.Id}:Duplicate", StringComparison.Ordinal))) {
            return;
        }
        // An attempt whose bodies no longer stand here (a body already moved under another attempt) is stale.
        if (!BodiesPresent(view: view, members: outbound.Members.Where(m => acceptedIds.Contains(item: m.Id)).ToArray())) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        if (acceptedIds.Count == 0) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        // Depart with the members the destination leased, each carrying the ticket home granted for this hop.
        var members = outbound.Members.Where(m => acceptedIds.Contains(item: m.Id)).Select(m => Carry(body: view.Body(id: m.Id)!, follows: m.Follows) with {
            Epoch = view.Body(id: m.Id)!.Epoch,
            Hop = ((Ids.IsCompanion(id: m.Id) && (outbound.To != Node.H)) ? msg.Key : view.Body(id: m.Id)!.Hop),
        }).ToArray();

        if (!TryRecord(node: at, rec: new Dep(Key: msg.Key, To: outbound.To, Members: members, Shed: false))) {
            // The departure is not durable: nothing leaves, and the destination's lease expires.
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        _ = at.Outbound.Remove(key: msg.Key);
        Send(s: s, from: at.Id, to: outbound.To, kind: MsgKind.Commit, key: msg.Key, members: members);
    }
    private static void OnReserveRefused(ModelState s, NodeState at, View view, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound)) {
            return;
        }
        // A forger's refused attempt teaches it nothing about its genuine copy.
        if (outbound.Forged) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        // Pending, Duplicate and Unavailable are not refusals: the same request asks again.
        if (msg.Arg is "Pending" or "Duplicate" or "Unavailable") {
            return;
        }
        _ = at.Outbound.Remove(key: msg.Key);
        if (msg.Arg == "Dead") {
            if (outbound.Phase == "ShedReserving") {
                ReattemptShed(s: s, at: at, view: view, image: view.ImageOf(key: msg.Key));
            }
            return;
        }
        if (outbound.To != Node.H) {
            // The player was refused (cap or capacity): nothing departs; the attempt is abandoned (section 5.1).
            return;
        }
        if (msg.Arg is "Stale" or "Released") {
            // Home's definitive answer about its own title ends this copy's custody: a shed image retires on record now,
            // an embodied copy in a retire step of its own.
            var id = outbound.Members[0].Id;

            if (outbound.Phase == "ShedReserving") {
                var image = view.ImageOf(key: msg.Key);

                if (view.InDoubt.Any(d => d.Key == image)) {
                    _ = TryRecord(node: at, rec: new Settle(Key: image, Landed: [], Retired: [id]));
                }
            } else if (view.Has(id: id)) {
                at.RefusedByHome[id] = msg.Arg;
            }
        }
    }
    // A shed image whose attempt died asks home again under a fresh, durably allocated key that names the departure
    // it carries.
    private static void ReattemptShed(ModelState s, NodeState at, View view, string image) {
        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        var key = Mint(view: view, at: at.Id);

        if (!TryRecord(node: at, rec: new Minted(Key: key, To: Node.H, MemberIds: dep.Members.Select(static m => m.Id).ToArray(), Image: image))) {
            return;
        }
        at.Outbound[key] = new Outbound(To: Node.H, Members: dep.Members, Phase: "ShedReserving", Granted: new());
        Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: dep.Members);
    }
    // One request per key asks home about every companion in the cohort; home answers each on its own title and
    // records one Hop naming the ones it grants (each entry its own title's fact).
    private static void OnHopRequest(ModelState s, NodeState home, View view, Msg msg) {
        var parts = msg.Arg.Split(',');
        var from = Enum.Parse<Node>(value: parts[0]);
        var to = Enum.Parse<Node>(value: parts[1]);
        var answers = new List<string>();
        var grantable = new List<(int Id, int Epoch, int Next)>();

        foreach (var member in msg.Members) {
            // A key whose settlement home already holds is spent for this companion, whatever its authorization.
            if (view.HopsDone.Contains(item: (msg.Key, member.Id))) {
                answers.Add(item: $"{member.Id}=Dead");
                continue;
            }
            if (view.Hops.TryGetValue(key: (msg.Key, member.Id), value: out var existing)) {
                answers.Add(item: $"{member.Id}={(home.UncertainAbout(key: msg.Key) ? "Unavailable" : (view.Custody[member.Id].Installed ? "Released" : existing.Next.ToString()))}");
                continue;
            }
            if (!view.Custody.TryGetValue(key: member.Id, value: out var custody)) {
                answers.Add(item: $"{member.Id}=NotMyTitle");
                continue;
            }
            var refusal = custody.Kind switch {
                CustodyKind.Released => "Released",
                CustodyKind.Pending => (((view.Hops[(custody.PendingKey, member.Id)].From == msg.From) || (view.Hops[(custody.PendingKey, member.Id)].To == msg.From)) ? "Pending" : "Stale"),
                CustodyKind.Home => "Stale",
                _ => (((custody.Custodian == from) && (custody.Epoch == member.Epoch) && (from == msg.From)) ? null : "Stale"),
            };

            if (refusal == "Pending") {
                ResolvePending(s: s, view: view, id: member.Id);
            }
            if (refusal is null) {
                grantable.Add(item: (member.Id, member.Epoch, member.Epoch + 1));
            } else {
                answers.Add(item: $"{member.Id}={refusal}");
            }
        }
        if (grantable.Count > 0) {
            if (TryRecord(node: home, rec: new Hop(Key: msg.Key, From: from, To: to, Members: [.. grantable]))) {
                answers.AddRange(collection: grantable.Select(static g => $"{g.Id}={g.Next}"));
            } else {
                answers.AddRange(collection: grantable.Select(static g => $"{g.Id}=Unavailable"));
            }
        }
        Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopAnswer, key: msg.Key, members: [], arg: string.Join(';', answers.Order(comparer: StringComparer.Ordinal)));
    }
    private static void OnHopAnswer(ModelState s, NodeState at, View view, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound) || (outbound.Phase != "Authorizing")) {
            return;
        }
        foreach (var answer in msg.Arg.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = answer.Split('=');
            var id = int.Parse(s: parts[0]);

            if (int.TryParse(s: parts[1], result: out var next)) {
                outbound.Granted[id] = next;
            } else if (parts[1] is "Pending" or "Unavailable") {
                continue;
            } else {
                outbound.Granted[id] = -1;
                if ((parts[1] is "Stale" or "Released") && view.Has(id: id)) {
                    at.RefusedByHome[id] = parts[1];
                }
            }
        }
        ReserveWhenAuthorized(s: s, at: at, key: msg.Key);
    }
    // Once home has answered for every companion, the cohort reserves at the destination with the granted ones.
    private static void ReserveWhenAuthorized(ModelState s, NodeState at, string key) {
        var outbound = at.Outbound[key];

        if (outbound.Members.Where(static m => !m.IsPlayer).Any(m => !outbound.Granted.ContainsKey(key: m.Id))) {
            return;
        }
        var members = outbound.Members.Where(m => m.IsPlayer || (outbound.Granted[m.Id] >= 0)).ToArray();

        if (members.Length == 0) {
            _ = at.Outbound.Remove(key: key);
            return;
        }
        at.Outbound[key] = outbound with { Members = members, Phase = "Reserving" };
        Send(s: s, from: at.Id, to: outbound.To, kind: MsgKind.Reserve, key: key, members: members);
    }
    // A destination's verification establishes a currently valid grant: the hop is this title's pending hop now, not
    // merely a record once written, and home can call it durable.
    private static void OnHopQuery(ModelState s, NodeState home, View view, Msg msg) {
        var phase = msg.Arg[..2];
        var answers = msg.Arg[2..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Select(id => {
            // A released title grants nothing more, though its pending hop stays open until it resolves.
            var granted = view.Hops.TryGetValue(key: (msg.Key, id), value: out var hop) &&
                (hop.To == msg.From) &&
                !view.HopsDone.Contains(item: (msg.Key, id)) &&
                view.Custody.TryGetValue(key: id, value: out var custody) &&
                (custody.Kind == CustodyKind.Pending) &&
                (custody.PendingKey == msg.Key) &&
                !custody.Installed &&
                !home.UncertainAbout(key: msg.Key);

            return $"{id}={(granted ? hop!.Next : -1)}";
        });

        Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopQueryReply, key: msg.Key, members: [], arg: phase + string.Join(';', answers));
    }
    private static void OnHopQueryReply(ModelState s, NodeState at, Msg msg) {
        var phase = msg.Arg[0];
        var waiting = ((phase == 'R') ? at.Held : at.HeldCommits);

        // An answer no request of its phase waits for is spent: it was given for a question already decided.
        if (!waiting.TryGetValue(key: msg.Key, value: out var held)) {
            return;
        }
        foreach (var answer in msg.Arg[2..].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = answer.Split('=');

            at.Verified[Verification(key: msg.Key, id: int.Parse(s: parts[0]), phase: phase)] = int.Parse(s: parts[1]);
        }
        _ = waiting.Remove(key: msg.Key);
        if (phase == 'R') {
            OnReserve(s: s, to: at, view: Fold(s: s, at: at.Id), msg: held);
        } else {
            OnCommit(s: s, to: at, view: Fold(s: s, at: at.Id), msg: held);
        }
    }
    // The commit lands the leased members that the destination still admits, at the generation their slots take, with a
    // companion's epoch advanced by one and its follows bound to the player member landed in this same request. At home
    // custody is verified again at the commit.
    private static void OnCommit(ModelState s, NodeState to, View view, Msg msg) {
        if (view.Arrs.TryGetValue(key: msg.Key, value: out var landedBefore) && !to.UncertainAbout(key: msg.Key)) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Committed, key: msg.Key, members: [], arg: string.Join(',', landedBefore.Members.Select(static m => m.Id)));
            return;
        }
        if (to.Latched) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Uncertain, key: msg.Key, members: []);
            return;
        }
        if (!to.Leases.TryGetValue(key: msg.Key, value: out var lease)) {
            _ = TryRecord(node: to, rec: new Dead(Key: msg.Key));
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Missing, key: msg.Key, members: []);
            return;
        }
        // A custodian lands a companion only under home's grant as it stands at the commit: the reservation's answer is
        // spent, so the commit is held until home answers again.
        if (to.Id != Node.H) {
            var unverified = lease.Members.Where(m => !m.IsPlayer && !lease.RefusedAtCommit.Contains(value: m.Id) && !to.Verified.ContainsKey(key: Verification(key: msg.Key, id: m.Id, phase: 'C'))).Select(static m => m.Id).ToArray();

            if (unverified.Length > 0) {
                if (!InFlight(s: s, from: to.Id, key: msg.Key, kind: MsgKind.HopQuery, arg: "C|")) {
                    Send(s: s, from: to.Id, to: Node.H, kind: MsgKind.HopQuery, key: msg.Key, members: [], arg: "C|" + string.Join(',', unverified));
                }
                to.HeldCommits[msg.Key] = msg;
                return;
            }
        }
        _ = to.HeldCommits.Remove(key: msg.Key);

        // The commit's answers are spent by this decision.
        var answered = to.Verified.Where(p => p.Key.StartsWith(msg.Key + ":", StringComparison.Ordinal) && p.Key.EndsWith(":C", StringComparison.Ordinal)).ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);

        foreach (var spent in answered.Keys) {
            _ = to.Verified.Remove(key: spent);
        }
        var landing = new List<Member>();
        var readopted = new List<int>();

        foreach (var leased in lease.Members) {
            var carried = msg.Members.FirstOrDefault(m => m.Id == leased.Id);

            // A commit lands what was leased: a member whose ticket differs from the verified reservation's is not landed.
            if ((carried is null) || lease.RefusedAtCommit.Contains(value: leased.Id) || (!leased.IsPlayer && (carried.Epoch != leased.Epoch))) {
                continue;
            }
            if (!leased.IsPlayer) {
                if (to.Id == Node.H) {
                    if (VerifyReturn(s: s, home: to, view: view, from: msg.From, member: carried) is not null) {
                        continue;
                    }
                    readopted.Add(item: leased.Id);
                } else if (answered[Verification(key: msg.Key, id: leased.Id, phase: 'C')] != (leased.Epoch + 1)) {
                    // Home's grant is no longer current (settled, superseded or released): the companion is not landed.
                    continue;
                } else if (view.Has(id: leased.Id)) {
                    throw new ModelViolation(message: $"{to.Id} lands a second body for {leased.Id}");
                }
            }
            landing.Add(item: carried with {
                Slot = leased.Slot,
                Gen = (view.SlotGen[leased.Slot] + 1),
                Epoch = (leased.IsPlayer ? carried.Epoch : (leased.Epoch + 1)),
                Hop = (leased.IsPlayer ? "" : ((to.Id == Node.H) ? "" : msg.Key)),
            });
        }
        if (landing.Any(static m => m.IsPlayer) && (view.Humans + 1) > HumanCap) {
            landing.Clear();
        }
        // The followed member binds only when it lands in this same request; the id is kept, the slot is read from it.
        landing = landing.Select(m => ((m.Follows is { } f) && !landing.Any(o => (o.Id == f) && o.IsPlayer)) ? (m with { Follows = null }) : m).ToList();
        if (landing.Count == 0 || (msg.Members.Any(static m => m.IsPlayer) && !landing.Any(static m => m.IsPlayer))) {
            _ = to.Leases.Remove(key: msg.Key);
            _ = TryRecord(node: to, rec: new Dead(Key: msg.Key));
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Missing, key: msg.Key, members: []);
            return;
        }
        if (!TryRecord(node: to, rec: new Arr(Key: msg.Key, From: msg.From, Members: landing.ToArray(), Readopted: readopted.ToArray()))) {
            if (to.Latched) {
                Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Uncertain, key: msg.Key, members: []);
            } else {
                // The members landed and were rolled back with the refused record: their slots keep the generations.
                _ = to.Leases.Remove(key: msg.Key);
                _ = TryRecord(node: to, rec: new Dead(Key: msg.Key, Burned: [.. landing.Select(static m => m.Slot)]));
                Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Missing, key: msg.Key, members: []);
            }
            return;
        }
        _ = to.Leases.Remove(key: msg.Key);
        if (landing.Any(static m => m.IsPlayer)) {
            s.PlayerAt = msg.To;
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Committed, key: msg.Key, members: [], arg: string.Join(',', landing.Select(static m => m.Id)));
    }
    private static void OnCommitted(ModelState s, NodeState at, View view, Msg msg) {
        var image = view.ImageOf(key: msg.Key);

        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        var landed = msg.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(id => dep.Members.Any(m => m.Id == id)).ToArray();
        // Install wins over a departure here too: a companion the destination did not land, whose title home released
        // meanwhile, is retired rather than restored.
        var retired = dep.Members.Where(m => (at.Id == Node.H) && Ids.IsCompanion(id: m.Id) && !landed.Contains(value: m.Id) && view.Custody.TryGetValue(key: m.Id, value: out var c) && c.Installed).Select(static m => m.Id).ToArray();

        if (!TryRecord(node: at, rec: new Settle(Key: image, Landed: landed, Retired: retired))) {
            return;
        }
        _ = at.Outbound.Remove(key: msg.Key);
        if (dep.Members.Any(static m => m.IsPlayer) && !landed.Contains(value: Ids.Player)) {
            s.PlayerAt = at.Id;
        }
        // The settlement notice to home, per companion the key was minted for; it stays an obligation until acknowledged.
        if ((at.Id != Node.H) && (dep.To != Node.H) && view.Mints.TryGetValue(key: image, value: out var minted)) {
            foreach (var id in minted.MemberIds.Where(Ids.IsCompanion)) {
                Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.HopSettled, key: image, members: [], arg: $"{id},{(landed.Contains(value: id) ? 1 : 0)}");
            }
        }
    }
    private static void OnMissing(ModelState s, NodeState at, View view, Msg msg) {
        var image = view.ImageOf(key: msg.Key);

        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        if (view.LatestAttempt.TryGetValue(image, out var latest) && (latest != msg.Key)) {
            return;
        }
        if (dep.Shed) {
            if (!at.Outbound.ContainsKey(key: msg.Key)) {
                ReattemptShed(s: s, at: at, view: view, image: image);
            }
            return;
        }
        // Install wins over a departure: a released title restores no companion body.
        var retired = dep.Members.Where(m => (at.Id == Node.H) && Ids.IsCompanion(id: m.Id) && view.Custody.TryGetValue(key: m.Id, value: out var c) && c.Installed).Select(static m => m.Id).ToArray();

        if (!TryRecord(node: at, rec: new Settle(Key: image, Landed: [], Retired: retired))) {
            return;
        }
        if (dep.Members.Any(static m => m.IsPlayer)) {
            s.PlayerAt = at.Id;
        }
    }
    private static void OnStatus(ModelState s, NodeState at, View view, Msg msg) {
        string reply;

        if (at.UncertainAbout(key: msg.Key)) {
            reply = "Uncertain";
        } else if (view.Arrs.TryGetValue(key: msg.Key, value: out var arr)) {
            reply = "Committed:" + string.Join(',', arr.Members.Select(static m => m.Id));
        } else if (at.Leases.ContainsKey(key: msg.Key)) {
            reply = "Reserved";
        } else if (at.Latched) {
            reply = "Uncertain";
        } else {
            // Answering Missing to a status query ends the key too, so the answer is final.
            if (!view.DeadKeys.Contains(item: msg.Key) && !TryRecord(node: at, rec: new Dead(Key: msg.Key))) {
                reply = "Uncertain";
            } else {
                reply = "Missing";
            }
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.StatusReply, key: msg.Key, members: [], arg: reply);
    }
    private static void OnStatusReply(ModelState s, NodeState at, View view, Msg msg) {
        if (view.InDoubt.FirstOrDefault(d => d.Key == view.ImageOf(key: msg.Key)) is not { } dep) {
            return;
        }
        if (msg.Arg.StartsWith("Committed", StringComparison.Ordinal)) {
            OnCommitted(s: s, at: at, view: view, msg: msg with { Kind = MsgKind.Committed, Arg = msg.Arg.Split(':')[1] });
        } else if (msg.Arg == "Missing") {
            OnMissing(s: s, at: at, view: view, msg: msg with { Kind = MsgKind.Missing });
        } else if ((msg.Arg == "Reserved") && !InFlight(s: s, from: at.Id, key: msg.Key, kind: MsgKind.Commit)) {
            Send(s: s, from: at.Id, to: dep.To, kind: MsgKind.Commit, key: msg.Key, members: dep.Members);
        }
    }
    // The title authority asks a hop's destination what it holds for an incarnation under a hop key. Only the authority
    // that minted the incarnation may ask, and the answer comes from durable records, never from retired outcome rows.
    private static void OnCustodyQuery(ModelState s, NodeState at, View view, Msg msg) {
        var id = int.Parse(s: msg.Arg);
        string reply;

        if (msg.From != Node.H) {
            reply = $"{id},Unauthorized";
        } else if (at.UncertainAbout(key: msg.Key)) {
            reply = $"{id},Uncertain";
        } else if (view.Arrs.TryGetValue(key: msg.Key, value: out var arr)) {
            reply = (arr.Members.Any(m => m.Id == id) ? $"{id},Landed" : $"{id},NotLanded");
        } else if (view.DeadKeys.Contains(item: msg.Key)) {
            reply = $"{id},Dead";
        } else if (at.Leases.ContainsKey(key: msg.Key)) {
            reply = $"{id},Reserved";
        } else if (at.Latched) {
            reply = $"{id},Uncertain";
        } else {
            // A key this destination leased and lost, or never leased at all, dies when the title authority asks: home
            // asks only once the source has given the key up, and the tombstone makes the answer final.
            reply = (TryRecord(node: at, rec: new Dead(Key: msg.Key)) ? $"{id},Dead" : $"{id},Uncertain");
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.CustodyReply, key: msg.Key, members: [], arg: reply);
    }
    private static void OnCustodyReply(ModelState s, NodeState home, View view, Msg msg) {
        var parts = msg.Arg.Split(',');
        var id = int.Parse(s: parts[0]);

        // Home's own hops settle from its own settlement record, never from the destination's answer.
        if (!view.Hops.TryGetValue(key: (msg.Key, id), value: out var hop) || view.HopsDone.Contains(item: (msg.Key, id)) || (hop.To != msg.From) || (hop.From == Node.H)) {
            return;
        }
        var recorded = parts[1] switch {
            "Landed" => TryRecord(node: home, rec: new HopDone(Key: msg.Key, Id: id, Arrived: true)),
            "NotLanded" or "Dead" => TryRecord(node: home, rec: new HopDone(Key: msg.Key, Id: id, Arrived: false)),
            _ => false,
        };

        // The source's settlement notice is answered once the destination has decided.
        if (recorded) {
            Send(s: s, from: Node.H, to: hop.From, kind: MsgKind.HopSettledAck, key: msg.Key, members: [], arg: id.ToString());
        }
    }
    private static void OnHopSettled(ModelState s, NodeState home, View view, Msg msg) {
        var parts = msg.Arg.Split(',');
        var id = int.Parse(s: parts[0]);
        var arrived = (parts[1] == "1");

        // The hop's destination is the only oracle for its settlement: a source's notice makes home ask the destination
        // (section 5.3), and home records HopDone from that authenticated answer alone, so a destination that still
        // holds a verified lease can never see its grant cancelled under it. A notice that arrives before its hop is
        // recorded as a cancellation marker, since no destination can hold a lease on a hop home never granted.
        if (view.HopsDone.Contains(item: (msg.Key, id))) {
            Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopSettledAck, key: msg.Key, members: [], arg: id.ToString());
            return;
        }
        if (view.Hops.TryGetValue(key: (msg.Key, id), value: out var hop)) {
            if ((hop.From == msg.From) && !InFlight(s: s, from: Node.H, key: msg.Key, kind: MsgKind.CustodyQuery, arg: id.ToString())) {
                Send(s: s, from: Node.H, to: hop.To, kind: MsgKind.CustodyQuery, key: msg.Key, members: [], arg: id.ToString());
            }
            return;
        }
        _ = arrived;
        if (TryRecord(node: home, rec: new HopDone(Key: msg.Key, Id: id, Arrived: false))) {
            Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopSettledAck, key: msg.Key, members: [], arg: id.ToString());
        }
    }

    // ---- moves ----------------------------------------------------------------------------------------------------

    /// <summary>Every successor of <paramref name="s"/>, in a fixed order, under the fault budget. Each lane
    /// (sender, receiver) delivers in order, as the engine's request lanes do; loss and duplication may touch any
    /// message.</summary>
    public static IEnumerable<(string Name, ModelState Next)> Successors(ModelState s, int faultBudget) {
        var laneHeads = new HashSet<(Node, Node, int)>();

        for (var i = 0; i < s.Network.Count; i++) {
            var msg = s.Network[i];
            var head = laneHeads.Add(item: (msg.From, msg.To, msg.Lane));

            if (head && s.LinkUp[(int)msg.From, (int)msg.To]) {
                var next = s.Clone();

                next.Network.RemoveAt(index: i);
                Deliver(s: next, msg: msg);
                yield return ($"deliver {msg.Canon}", next);
            }
            if (s.Budget.Faults < faultBudget) {
                if (s.Budget.Allows(fault: Fault.Loss)) {
                    var dropped = s.Clone();

                    dropped.Network.RemoveAt(index: i);
                    dropped.Budget.Drops++;
                    dropped.Budget.Faults++;
                    yield return ($"drop {msg.Canon}", dropped);
                }
                if (!msg.Dup && s.Budget.Allows(fault: Fault.Dup)) {
                    var duplicated = s.Clone();

                    duplicated.Network[i] = msg with { Dup = true };
                    duplicated.Network.Add(item: msg with { Dup = true });
                    duplicated.Budget.Dups++;
                    duplicated.Budget.Faults++;
                    yield return ($"dup {msg.Canon}", duplicated);
                }
            }
        }
        foreach (var move in ProgressActions(s: s)) {
            yield return move;
        }
        if (s.Budget.Faults >= faultBudget) {
            yield break;
        }
        foreach (var move in FaultActions(s: s)) {
            yield return move;
        }
    }
    /// <summary>The protocol's own moves.</summary>
    public static IEnumerable<(string Name, ModelState Next)> ProgressActions(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(s: s, at: (Node)i);
        }
        // The player crosses along the route, its following companions in the same cohort. The key is allocated durably
        // before anything is sent.
        if ((s.PlayerAt is { } at) && (s.Itinerary < s.Route.Length) && (s.Route[s.Itinerary] != at) && views[(int)at].Has(id: Ids.Player) && !s.At(node: at).Outbound.Values.Any(static o => o.Members.Any(static m => m.IsPlayer)) && !views[(int)at].InDoubt.Any(static d => d.Members.Any(static m => m.IsPlayer))) {
            var next = s.Clone();
            var node = next.At(node: at);
            var view = views[(int)at];
            var to = s.Route[s.Itinerary];
            var key = Mint(view: view, at: at);
            var members = new List<Member>();

            // Companions are listed before the player: binding is by member id, never by position.
            foreach (var id in s.CompanionIds) {
                if (view.Follows(id: id)) {
                    members.Add(item: Carry(body: view.Body(id: id)!, follows: Ids.Player));
                }
            }
            members.Add(item: Carry(body: view.Body(id: Ids.Player)!, follows: null));
            next.Itinerary++;
            if (TryRecord(node: node, rec: new Minted(Key: key, To: to, MemberIds: members.Select(static m => m.Id).ToArray()))) {
                var companions = members.Count - 1;

                if ((companions > 0) && (to != Node.H)) {
                    node.Outbound[key] = new Outbound(To: to, Members: members.ToArray(), Phase: "Authorizing", Granted: new());
                    if (at != Node.H) {
                        Send(s: next, from: at, to: Node.H, kind: MsgKind.HopRequest, key: key, members: members.Where(static m => !m.IsPlayer).ToArray(), arg: $"{at},{to}");
                    }
                } else {
                    node.Outbound[key] = new Outbound(To: to, Members: members.ToArray(), Phase: "Reserving", Granted: new());
                    Send(s: next, from: at, to: to, kind: MsgKind.Reserve, key: key, members: members.ToArray());
                }
            }
            yield return ($"cross {at}>{to}", next);
        }
        // Home authorizes its own companions' hops locally: one record names every companion whose title is home.
        foreach (var (key, outbound) in s.Nodes[0].Outbound) {
            if ((outbound.Phase != "Authorizing") || !outbound.Members.Any(m => !m.IsPlayer && !outbound.Granted.ContainsKey(key: m.Id))) {
                continue;
            }
            var next = s.Clone();
            var home = next.Nodes[0];
            var nextOutbound = home.Outbound[key];
            var grantable = outbound.Members.Where(m => !m.IsPlayer && (views[0].Custody[m.Id].Kind == CustodyKind.Home)).Select(m => (m.Id, views[0].Custody[m.Id].Epoch, views[0].Custody[m.Id].Epoch + 1)).ToArray();
            var recorded = (grantable.Length > 0) && TryRecord(node: home, rec: new Hop(Key: key, From: Node.H, To: outbound.To, Members: grantable));

            foreach (var member in outbound.Members.Where(static m => !m.IsPlayer)) {
                var granted = grantable.FirstOrDefault(g => g.Id == member.Id);

                if (granted.Id == member.Id) {
                    if (recorded) {
                        nextOutbound.Granted[member.Id] = granted.Item3;
                    }
                } else {
                    nextOutbound.Granted[member.Id] = -1;
                }
            }
            if (recorded || (grantable.Length == 0)) {
                ReserveWhenAuthorized(s: next, at: home, key: key);
            }
            yield return ($"authorize {key}", next);
        }
        for (var i = 1; i < 3; i++) {
            var node = s.Nodes[i];
            var view = views[i];

            foreach (var id in s.CompanionIds) {
                var body = view.Body(id: id);
                var attempting = node.Outbound.Values.Any(o => o.Members.Any(m => m.Id == id)) || view.InDoubt.Any(d => d.Members.Any(m => m.Id == id));
                var separated = (body is not null) && !view.Follows(id: id);

                // Separation sweep: the companion asks to go home, under a durably allocated key.
                if (separated && !attempting && (node.Sweeps.GetValueOrDefault(key: id) < SweepLimit)) {
                    var next = s.Clone();
                    var nextNode = next.Nodes[i];
                    var key = Mint(view: view, at: node.Id);
                    var members = new[] { Carry(body: body!, follows: null) };

                    nextNode.Sweeps[id] = node.Sweeps.GetValueOrDefault(key: id) + 1;
                    if (TryRecord(node: nextNode, rec: new Minted(Key: key, To: Node.H, MemberIds: [id]))) {
                        nextNode.Outbound[key] = new Outbound(To: Node.H, Members: members, Phase: "Reserving", Granted: new());
                        Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: members);
                    }
                    yield return ($"sweep {node.Id}:{id}", next);
                }
                // Retire: home has definitively refused this copy (Stale or Released); it is destroyed on record, one
                // companion per step.
                if ((body is not null) && node.RefusedByHome.TryGetValue(key: id, value: out var cause)) {
                    var next = s.Clone();

                    _ = next.Nodes[i].RefusedByHome.Remove(key: id);
                    _ = TryRecord(node: next.Nodes[i], rec: new Retire(Id: id, Cause: cause));
                    yield return ($"retire {node.Id}:{id}", next);
                }
                // The grace window opens when the companion is found separated.
                if (separated && !view.Stranded(id: id)) {
                    var next = s.Clone();

                    _ = TryRecord(node: next.Nodes[i], rec: new Stranded(Id: id, Visit: view.PlayerVisits));
                    yield return ($"strand {node.Id}:{id}", next);
                }
                // Shed: the window has passed with the companion still here, whatever attempt is in flight. The stranded
                // custodian first allocates the shed key through the common allocator (one record), then detaches the
                // companion into a durable return record under it and frees the slot (one record).
                var shedReady = separated && view.Stranded(id: id) && view.GraceElapsed(id: id) && !view.InDoubt.Any(d => d.Members.Any(m => m.Id == id));
                var shedKey = view.Mints.Values.FirstOrDefault(m => m.Shed && m.MemberIds.Contains(value: id) && !view.Deps.ContainsKey(key: m.Key) && !view.Settled.Contains(item: m.Key))?.Key;

                if (shedReady && (shedKey is null)) {
                    var next = s.Clone();

                    _ = TryRecord(node: next.Nodes[i], rec: new Minted(Key: Mint(view: view, at: node.Id), To: Node.H, MemberIds: [id], Shed: true));
                    yield return ($"shed-mint {node.Id}:{id}", next);
                }
                if (shedReady && (shedKey is not null)) {
                    var next = s.Clone();
                    var nextNode = next.Nodes[i];
                    var key = shedKey;
                    var members = new[] { Carry(body: body!, follows: null) };

                    if (TryRecord(node: nextNode, rec: new Dep(Key: key, To: Node.H, Members: members, Shed: true))) {
                        foreach (var stale in nextNode.Outbound.Where(o => o.Value.Members.Any(m => m.Id == id)).Select(static o => o.Key).ToArray()) {
                            _ = nextNode.Outbound.Remove(key: stale);
                        }
                        nextNode.Outbound[key] = new Outbound(To: Node.H, Members: members, Phase: "ShedReserving", Granted: new());
                        Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: members);
                    }
                    yield return ($"shed {node.Id}:{id}", next);
                }
            }
        }
        // Home settles its own hops from its own settlement, one record per companion.
        foreach (var (key, settle) in views[0].Settles) {
            if (!views[0].Mints.TryGetValue(key: key, value: out var minted)) {
                continue;
            }
            foreach (var id in minted.MemberIds.Where(Ids.IsCompanion)) {
                if (!views[0].Hops.ContainsKey(key: (key, id)) || views[0].HopsDone.Contains(item: (key, id))) {
                    continue;
                }
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new HopDone(Key: key, Id: id, Arrived: settle.Landed.Contains(value: id)));
                yield return ($"settle hop {key}:{id}", next);
            }
        }
        // An attempt whose key was allocated but that never departed (lost with a crash) is abandoned on record, so its
        // hops settle as not arrived.
        for (var i = 0; i < 3; i++) {
            foreach (var (key, minted) in views[i].Mints) {
                // A shed key waits for its departure while the companion still stands here.
                if ((minted.Image is not null) || views[i].Deps.ContainsKey(key: key) || views[i].Settled.Contains(item: key) || s.Nodes[i].Outbound.ContainsKey(key: key) || (minted.Shed && minted.MemberIds.All(id => views[i].Has(id: id)))) {
                    continue;
                }
                var next = s.Clone();

                if (TryRecord(node: next.Nodes[i], rec: new Settle(Key: key, Landed: [], Retired: []))) {
                    foreach (var (k, id, arrived) in Fold(s: next, at: (Node)i).UnackedSettles(node: (Node)i).Where(u => u.Key == key)) {
                        Send(s: next, from: (Node)i, to: Node.H, kind: MsgKind.HopSettled, key: k, members: [], arg: $"{id},{(arrived ? 1 : 0)}");
                    }
                }
                yield return ($"abandon {(Node)i}:{key}", next);
            }
        }
        // Notices are durable obligations of a recall or a release.
        foreach (var id in s.CompanionIds) {
            var custody = views[0].Custody[id];

            // Each recall owes its own notice.
            if (custody.Recalls.FirstOrDefault(e => !views[0].Noticed.Contains(item: $"{id}:recalled@{e}"), -1) is var unnoticed && (unnoticed >= 0)) {
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new Noticed(Id: id, What: $"recalled@{unnoticed}"));
                yield return ($"notify recall {id}", next);
            }
            if (custody.Installed && !views[0].Noticed.Contains(item: $"{id}:released")) {
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new Noticed(Id: id, What: "released"));
                yield return ($"notify release {id}", next);
            }
        }
        // Retries recover from loss: a retry is a timeout, so none fires before anything was lost (a retry racing its own
        // late reply is the duplication fault). Each re-sends the step a departure or attempt waits on, once.
        var recovering = s.Budget.Lossy || s.Converging;

        for (var i = 0; (i < 3) && recovering; i++) {
            var node = s.Nodes[i];
            var view = views[i];

            foreach (var dep in view.InDoubt) {
                var key = (view.LatestAttempt.TryGetValue(dep.Key, out var latest) ? latest : dep.Key);

                if (node.Retries.GetValueOrDefault(key: key) >= RetryLimit) {
                    continue;
                }
                if (node.Outbound.TryGetValue(key: key, value: out var shedAttempt)) {
                    if (!InFlight(s: s, from: node.Id, key: key, kind: MsgKind.Reserve)) {
                        var next = s.Clone();

                        next.Nodes[i].Retries[key] = node.Retries.GetValueOrDefault(key: key) + 1;
                        Send(s: next, from: node.Id, to: shedAttempt.To, kind: MsgKind.Reserve, key: key, members: shedAttempt.Members);
                        yield return ($"retry reserve {node.Id}:{key}", next);
                    }
                } else if (!InFlight(s: s, from: node.Id, key: key, kind: MsgKind.Status)) {
                    // A status query may race the delayed commit it asks about.
                    var next = s.Clone();

                    next.Nodes[i].Retries[key] = node.Retries.GetValueOrDefault(key: key) + 1;
                    Send(s: next, from: node.Id, to: dep.To, kind: MsgKind.Status, key: key, members: []);
                    yield return ($"retry status {node.Id}:{key}", next);
                }
            }
            foreach (var (key, outbound) in node.Outbound) {
                if (view.InDoubt.Any(d => d.Key == view.ImageOf(key: key)) || (node.Retries.GetValueOrDefault(key: key) >= RetryLimit)) {
                    continue;
                }
                if (outbound.Phase == "Authorizing") {
                    var waiting = outbound.Members.Where(m => !m.IsPlayer && !outbound.Granted.ContainsKey(key: m.Id)).ToArray();

                    if ((node.Id == Node.H) || (waiting.Length == 0) || InFlight(s: s, from: node.Id, key: key, kind: MsgKind.HopRequest)) {
                        continue;
                    }
                    var next = s.Clone();

                    next.Nodes[i].Retries[key] = node.Retries.GetValueOrDefault(key: key) + 1;
                    Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.HopRequest, key: key, members: waiting, arg: $"{node.Id},{outbound.To}");
                    yield return ($"retry hop {node.Id}:{key}", next);
                } else if (!InFlight(s: s, from: node.Id, key: key, kind: MsgKind.Reserve)) {
                    var next = s.Clone();

                    next.Nodes[i].Retries[key] = node.Retries.GetValueOrDefault(key: key) + 1;
                    Send(s: next, from: node.Id, to: outbound.To, kind: MsgKind.Reserve, key: key, members: outbound.Members);
                    yield return ($"retry reserve {node.Id}:{key}", next);
                }
            }
            foreach (var (key, id, arrived) in view.UnackedSettles(node: node.Id)) {
                if (InFlight(s: s, from: node.Id, key: key, kind: MsgKind.HopSettled, arg: id.ToString()) || (node.Retries.GetValueOrDefault(key: $"settle:{key}:{id}") >= RetryLimit)) {
                    continue;
                }
                var next = s.Clone();

                next.Nodes[i].Retries[$"settle:{key}:{id}"] = node.Retries.GetValueOrDefault(key: $"settle:{key}:{id}") + 1;
                Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.HopSettled, key: key, members: [], arg: $"{id},{(arrived ? 1 : 0)}");
                yield return ($"retry settle {node.Id}:{key}:{id}", next);
            }
            // A destination's held reservation asks home again.
            foreach (var (key, held) in node.Held) {
                if (InFlight(s: s, from: node.Id, key: key, kind: MsgKind.HopQuery, arg: "R|") || (node.Retries.GetValueOrDefault(key: $"verify:{key}") >= RetryLimit)) {
                    continue;
                }
                var next = s.Clone();

                next.Nodes[i].Retries[$"verify:{key}"] = node.Retries.GetValueOrDefault(key: $"verify:{key}") + 1;
                Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.HopQuery, key: key, members: [], arg: "R|" + string.Join(',', held.Members.Where(m => !m.IsPlayer && !node.Verified.ContainsKey(key: Verification(key: key, id: m.Id, phase: 'R'))).Select(static m => m.Id)));
                yield return ($"retry verify {node.Id}:{key}", next);
            }
        }
        // Home resolves a pending hop by asking the hop's destination what it holds.
        foreach (var ((key, id), hop) in views[0].Hops) {
            if (!recovering || (hop.From == Node.H) || views[0].HopsDone.Contains(item: (key, id)) || InFlight(s: s, from: Node.H, key: key, kind: MsgKind.CustodyQuery, arg: id.ToString()) || (s.Nodes[0].Retries.GetValueOrDefault(key: $"resolve:{key}:{id}") >= RetryLimit)) {
                continue;
            }
            var next = s.Clone();

            next.Nodes[0].Retries[$"resolve:{key}:{id}"] = s.Nodes[0].Retries.GetValueOrDefault(key: $"resolve:{key}:{id}") + 1;
            Send(s: next, from: Node.H, to: hop.To, kind: MsgKind.CustodyQuery, key: key, members: [], arg: id.ToString());
            yield return ($"resolve {key}:{id}", next);
        }
    }
    /// <summary>Faults, deadlines and the owner's and author's acts.</summary>
    public static IEnumerable<(string Name, ModelState Next)> FaultActions(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(s: s, at: (Node)i);
        }
        var home = views[0];
        var budget = s.Budget;

        if (budget.Allows(fault: Fault.Crash) && (budget.Crashes < budget.Limit)) {
            for (var i = 0; i < 3; i++) {
                var node = s.Nodes[i];

                if ((node.Leases.Count == 0) && (node.Outbound.Count == 0) && (node.Retries.Count == 0) && (node.Verified.Count == 0) && (node.Held.Count == 0) && !node.Latched && (node.NextAppend == Append.None)) {
                    continue;
                }
                var next = s.Clone();

                next.Nodes[i].Crash();
                next.Budget.Crashes++;
                next.Budget.Faults++;
                yield return ($"crash {node.Id}", next);
            }
        }
        if (budget.Allows(fault: Fault.Log) && (budget.Logs < budget.Limit)) {
            for (var i = 0; i < 3; i++) {
                if (s.Nodes[i].Latched || (s.Nodes[i].NextAppend != Append.None)) {
                    continue;
                }
                foreach (var append in new[] { Append.Refused, Append.UncertainLost, Append.UncertainLanded }) {
                    var next = s.Clone();

                    next.Nodes[i].NextAppend = append;
                    next.Budget.Logs++;
                    next.Budget.Faults++;
                    yield return ($"append {append} {(Node)i}", next);
                }
            }
        }
        // A lane reconnects without its process restarting: requests already sent may still execute at the receiver
        // while the new connection's requests proceed.
        if (budget.Allows(fault: Fault.Reconnect) && (budget.Relinks < budget.Limit)) {
            for (var i = 0; i < 3; i++) {
                for (var j = 0; j < 3; j++) {
                    if ((i == j) || !s.Network.Any(m => (m.From == (Node)i) && (m.To == (Node)j))) {
                        continue;
                    }
                    var next = s.Clone();

                    next.Nodes[i].Connections[j]++;
                    next.Budget.Relinks++;
                    next.Budget.Faults++;
                    yield return ($"reconnect {(Node)i}>{(Node)j}", next);
                }
            }
        }
        if (budget.Allows(fault: Fault.Partition) && (budget.Partitions < (budget.Limit + 1))) {
            for (var i = 0; i < 3; i++) {
                for (var j = i + 1; j < 3; j++) {
                    var next = s.Clone();
                    var up = !s.LinkUp[i, j];

                    next.LinkUp[i, j] = up;
                    next.LinkUp[j, i] = up;
                    next.Budget.Partitions++;
                    next.Budget.Faults++;
                    yield return ($"{(up ? "heal" : "partition")} {(Node)i}-{(Node)j}", next);
                }
            }
        }
        if (budget.Allows(fault: Fault.Deadline) && (budget.Deadlines < (budget.Limit + 1))) {
            // Lease deadlines.
            for (var i = 0; i < 3; i++) {
                foreach (var key in s.Nodes[i].Leases.Keys) {
                    var next = s.Clone();

                    _ = next.Nodes[i].Leases.Remove(key: key);
                    next.Budget.Deadlines++;
                    next.Budget.Faults++;
                    yield return ($"expire {(Node)i}:{key}", next);
                }
            }
            // Grace windows.
            for (var i = 1; i < 3; i++) {
                foreach (var id in s.CompanionIds.Where(id => views[i].Stranded(id: id) && !views[i].GraceElapsed(id: id) && views[i].Has(id: id))) {
                    var next = s.Clone();

                    _ = TryRecord(node: next.Nodes[i], rec: new GraceElapsed(Id: id, Visit: views[i].PlayerVisits));
                    next.Budget.Deadlines++;
                    next.Budget.Faults++;
                    yield return ($"elapse {(Node)i}:{id}", next);
                }
            }
            // A source stops waiting for home's hop answer and crosses with what it has.
            for (var i = 1; i < 3; i++) {
                foreach (var (key, outbound) in s.Nodes[i].Outbound) {
                    if (outbound.Phase != "Authorizing") {
                        continue;
                    }
                    var next = s.Clone();
                    var nextOutbound = next.Nodes[i].Outbound[key];

                    foreach (var member in outbound.Members.Where(m => !m.IsPlayer && !outbound.Granted.ContainsKey(key: m.Id))) {
                        nextOutbound.Granted[member.Id] = -1;
                    }
                    ReserveWhenAuthorized(s: next, at: next.Nodes[i], key: key);
                    next.Budget.Deadlines++;
                    next.Budget.Faults++;
                    yield return ($"hop timeout {(Node)i}:{key}", next);
                }
            }
            // A destination stops waiting for home's verification and refuses what it could not verify.
            for (var i = 1; i < 3; i++) {
                foreach (var (key, held) in s.Nodes[i].Held) {
                    var next = s.Clone();
                    var node = next.Nodes[i];

                    foreach (var member in held.Members.Where(m => !m.IsPlayer && !node.Verified.ContainsKey(key: Verification(key: key, id: m.Id, phase: 'R')))) {
                        node.Verified[Verification(key: key, id: member.Id, phase: 'R')] = -1;
                    }
                    _ = node.Held.Remove(key: key);
                    OnReserve(s: next, to: node, view: Fold(s: next, at: (Node)i), msg: held);
                    next.Budget.Deadlines++;
                    next.Budget.Faults++;
                    yield return ($"verify timeout {(Node)i}:{key}", next);
                }
            }
        }
        if (budget.Allows(fault: Fault.Install) && (budget.Installs < budget.Limit)) {
            foreach (var id in s.CompanionIds.Where(id => !home.Custody[id].Installed)) {
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new Install(Id: id));
                next.Budget.Installs++;
                next.Budget.Faults++;
                yield return ($"install {id}", next);
            }
        }
        // Recall: only home, only for a settled title away (a pending hop is resolved first, since its destination may
        // hold a verified lease), never while a return lease is live, only while the owner is reachable and not standing
        // where the title is held.
        if (budget.Allows(fault: Fault.Recall) && (budget.Recalls < budget.Limit) && (s.PlayerAt is { } playerAt) && Reachable(s: s, a: Node.H, b: playerAt)) {
            foreach (var id in s.CompanionIds) {
                var custody = home.Custody[id];

                if ((custody.Kind != CustodyKind.Away) || s.Nodes[0].Leases.Values.Any(l => l.Members.Any(m => m.Id == id)) || (custody.Custodian == playerAt)) {
                    continue;
                }
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new Recall(Id: id, NewEpoch: custody.MaxIssued + 1));
                next.Budget.Recalls++;
                next.Budget.Faults++;
                yield return ($"recall {id}", next);
            }
        }
        if (budget.Allows(fault: Fault.Disconnect) && (budget.Disconnects < budget.Limit) && (s.PlayerAt is { } leaving) && views[(int)leaving].Has(id: Ids.Player)) {
            var next = s.Clone();

            _ = TryRecord(node: next.At(node: leaving), rec: new PlayerLeft());
            next.PlayerAt = null;
            next.Budget.Disconnects++;
            next.Budget.Faults++;
            yield return ($"disconnect at {leaving}", next);
        }
        if (budget.Allows(fault: Fault.Disconnect) && (budget.Reconnects < budget.Limit) && (budget.Disconnects > 0) && (s.PlayerAt is null)) {
            var next = s.Clone();

            _ = TryRecord(node: next.Nodes[0], rec: new PlayerJoined());
            next.PlayerAt = Node.H;
            next.Budget.Reconnects++;
            next.Budget.Faults++;
            yield return ("reconnect", next);
        }
        // A former custodian forges a reservation toward home or the other custodian: a ticket it once held, carrying
        // any epoch it can guess, under a fresh key or under a key it allocated before (a cancelled authorization).
        if (budget.Allows(fault: Fault.Forge) && (budget.Forged < budget.Limit)) {
            for (var i = 1; i < 3; i++) {
                var epochs = s.CompanionIds.ToDictionary(id => id, id => Math.Max(views[0].Custody[id].Epoch, views[0].Custody[id].MaxIssued));

                foreach (var ticket in views[i].Arrs.Values.SelectMany(static a => a.Members).Where(static m => !m.IsPlayer).GroupBy(static m => m.Id).Select(static g => g.Last())) {
                    foreach (var target in new[] { Node.H, (Node)(3 - i) }) {
                        foreach (var key in views[i].Mints.Keys.Where(k => !views[i].Deps.ContainsKey(key: k)).Append($"{(Node)i}forged{ticket.Id}")) {
                            foreach (var epoch in new[] { ticket.Epoch, epochs[ticket.Id] }.Distinct()) {
                                var next = s.Clone();
                                var members = new[] { ticket with { Follows = null, Epoch = epoch } };

                                next.Nodes[i].Outbound[key] = new Outbound(To: target, Members: members, Phase: "Reserving", Granted: new(), Forged: true);
                                Send(s: next, from: (Node)i, to: target, kind: MsgKind.Reserve, key: key, members: members);
                                next.Budget.Forged++;
                                next.Budget.Faults++;
                                yield return ($"forge {(Node)i}>{target}:{ticket.Id} under {key}@{epoch}", next);
                            }
                        }
                    }
                }
            }
        }
        if (budget.Allows(fault: Fault.Admission) && (budget.Admissions < budget.Limit)) {
            // Another human takes the destination's one human seat, so the player's own admission fails.
            for (var i = 1; i < 3; i++) {
                if (views[i].Has(id: Ids.Stranger) || (views[i].Humans >= HumanCap)) {
                    continue;
                }
                var slot = FreeSlot(s: s, view: views[i], node: s.Nodes[i], taken: []);

                if (slot < 0) {
                    continue;
                }
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[i], rec: new Stranger(Slot: slot));
                next.Budget.Admissions++;
                next.Budget.Faults++;
                yield return ($"stranger {(Node)i}", next);
            }
            // A destination refuses one leased companion at the landing itself (partial success).
            for (var i = 1; i < 3; i++) {
                foreach (var (key, lease) in s.Nodes[i].Leases) {
                    foreach (var member in lease.Members.Where(m => !m.IsPlayer && !lease.RefusedAtCommit.Contains(value: m.Id))) {
                        var next = s.Clone();

                        next.Nodes[i].Leases[key] = lease with { RefusedAtCommit = [.. lease.RefusedAtCommit, member.Id] };
                        next.Budget.Admissions++;
                        next.Budget.Faults++;
                        yield return ($"refuse at commit {(Node)i}:{key}:{member.Id}", next);
                    }
                }
            }
        }
        // The driver writes the companion's durable state wherever it is embodied.
        if (budget.Allows(fault: Fault.Write) && (budget.Writes < budget.Limit)) {
            for (var i = 0; i < 3; i++) {
                foreach (var id in s.CompanionIds.Where(id => views[i].Has(id: id))) {
                    var next = s.Clone();

                    _ = TryRecord(node: next.Nodes[i], rec: new Write(Id: id, Epoch: views[i].Body(id: id)!.Epoch));
                    next.Budget.Writes++;
                    next.Budget.Faults++;
                    yield return ($"write {(Node)i}:{id}", next);
                }
            }
        }
        // Home's census fills a free slot that no title holds.
        if (budget.Allows(fault: Fault.Census) && (budget.Censuses < budget.Limit)) {
            var slot = FreeSlot(s: s, view: home, node: s.Nodes[0], taken: []);

            if (slot >= 0) {
                var next = s.Clone();

                _ = TryRecord(node: next.Nodes[0], rec: new Bot(Slot: slot));
                next.Budget.Censuses++;
                next.Budget.Faults++;
                yield return ($"census {slot}", next);
            }
        }
    }

    // ---- invariants -----------------------------------------------------------------------------------------------

    /// <summary>The decision's invariants on one state; throws <see cref="ModelViolation"/> naming the first broken.</summary>
    public static void CheckInvariants(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(s: s, at: (Node)i);
        }
        var home = views[0];

        // Replay equals live: an independent derivation from the records agrees with the reducer on every body (epoch,
        // slot, image, recorded binding) and every title.
        for (var i = 0; i < 3; i++) {
            var (embodied, custody) = Shadow(node: s.Nodes[i], companions: s.Companions);
            var folded = views[i].Bodies.Values.ToDictionary(static b => b.Id, b => new ShadowBody(Epoch: (Ids.IsCompanion(id: b.Id) ? b.Epoch : 0), Slot: b.Slot, Gen: b.Gen, Image: (Ids.IsCompanion(id: b.Id) ? b.Image : 0), Follows: (Ids.IsCompanion(id: b.Id) ? (b.Follows ?? -1) : -1), FollowsSlot: (Ids.IsCompanion(id: b.Id) ? b.FollowsSlot : -1), FollowsGen: (Ids.IsCompanion(id: b.Id) ? b.FollowsGen : -1)));

            if (!embodied.OrderBy(static p => p.Key).SequenceEqual(second: folded.OrderBy(static p => p.Key))) {
                throw new ModelViolation(message: $"{(Node)i}: the shadow fold embodies [{string.Join(',', embodied.Select(static p => $"{p.Key}{p.Value}"))}] but the reducer [{string.Join(',', folded.OrderBy(static p => p.Key).Select(static p => $"{p.Key}{p.Value}"))}]");
            }
            if (i == 0) {
                foreach (var id in s.CompanionIds) {
                    var c = home.Custody[id];
                    var text = c.Kind switch {
                        CustodyKind.Released => "Released",
                        CustodyKind.Pending => $"Pending:{c.PendingKey}",
                        _ => $"{c.Kind}:{c.Custodian}@{c.Epoch}",
                    };

                    if (custody[id] != text) {
                        throw new ModelViolation(message: $"custody of {id}: shadow {custody[id]} but reducer {text}");
                    }
                }
            }
        }
        foreach (var id in s.CompanionIds) {
            var custody = home.Custody[id];
            var copies = Enumerable.Range(0, 3).Where(i => views[i].Has(id: id)).Select(i => ((Node)i, views[i].Body(id: id)!)).ToArray();

            // At most one embodiment at the current epoch: a copy below the title's epoch is stale and is fenced from
            // every commit (checked below), retiring when it next speaks to home.
            // A forging custodian that lands the companion elsewhere while keeping a body of its own leaves that body at
            // the hop's old epoch, so with a forger in play the current epoch is the highest home has issued; the
            // puppet is then the stale copy the convergence rule retires or tolerates while it follows.
            var current = ((s.Budget.Forged > 0) ? Math.Max(custody.Epoch, custody.MaxIssued) : custody.Epoch);

            if (copies.Count(c => c.Item2.Epoch >= current) > 1) {
                throw new ModelViolation(message: $"{id} embodied at {copies.Count(c => c.Item2.Epoch >= current)} authorities at the current epoch");
            }
            // Home embodies it only when its title says home, or while its own hop is authorized but not yet departed;
            // the held slot is free of every other body while the title is away or pending.
            // Home's own hop stays Pending from its Hop record until its HopDone record: in between the body may still
            // stand here (not yet departed) or stand here again (the departure rolled back and restored it); only a
            // departure in doubt, or one that landed it, means the body is elsewhere.
            var ownHopOpen = (custody.Kind == CustodyKind.Pending) && home.Hops.TryGetValue(key: (custody.PendingKey, id), value: out var ownHop) && (ownHop.From == Node.H);
            var ownDeparted = home.Deps.TryGetValue(key: custody.PendingKey, value: out var ownDep) && ownDep.Members.Any(m => m.Id == id) && (!home.Settles.TryGetValue(key: custody.PendingKey, value: out var ownSettle) || ownSettle.Landed.Contains(value: id) || ownSettle.Retired.Contains(value: id));

            if (home.Has(id: id) && (custody.Kind != CustodyKind.Home) && !(ownHopOpen && !ownDeparted)) {
                throw new ModelViolation(message: $"home embodies {id} while custody is {custody.Kind}");
            }
            if (!home.Has(id: id) && (custody.Kind == CustodyKind.Home) && !home.InDoubt.Any(d => d.Members.Any(m => m.Id == id))) {
                throw new ModelViolation(message: $"custody of {id} says home while home holds neither body nor departure");
            }
            var departedInDoubt = home.InDoubt.Any(d => d.Members.Any(m => m.Id == id));

            if ((custody.Kind is CustodyKind.Pending or CustodyKind.Away || departedInDoubt) && home.Bodies.Values.Any(b => (b.Slot == custody.HeldSlot) && (b.Id != id))) {
                throw new ModelViolation(message: $"the held slot {custody.HeldSlot} of {id} is occupied by {home.Bodies.Values.First(b => b.Slot == custody.HeldSlot).Id}");
            }
            // No lost title: an embodied copy abroad is the one the title names, or the title's own pending hop names it.
            foreach (var (at, body) in copies.Where(static c => c.Item1 != Node.H)) {
                var ok = custody.Kind switch {
                    CustodyKind.Away => ((custody.Custodian == at) && (custody.Epoch == body.Epoch)) || (custody.Epoch > body.Epoch),
                    CustodyKind.Pending => home.Hops.TryGetValue(key: (custody.PendingKey, id), value: out var h) && ((((h.From == at) && (h.Epoch == body.Epoch)) || ((h.To == at) && (h.Next == body.Epoch))) || (h.Epoch > body.Epoch)),
                    CustodyKind.Home => (custody.Epoch > body.Epoch),
                    _ => true,
                };

                if (!ok) {
                    throw new ModelViolation(message: $"{at} embodies {id}@{body.Epoch} while home says {custody.Kind} {custody.Custodian}@{custody.Epoch}");
                }
            }
            // Fencing: every landing of a companion at a custodian rides a hop home granted to that custodian at that
            // epoch, and every re-adoption at home lands one epoch above the custody it left.
            for (var i = 1; i < 3; i++) {
                foreach (var arr in views[i].Arrs.Values) {
                    if (arr.Members.FirstOrDefault(m => m.Id == id) is not { } landed) {
                        continue;
                    }
                    if (!home.Hops.TryGetValue(key: (arr.Key, id), value: out var hop) || (hop.To != (Node)i) || (hop.Next != landed.Epoch)) {
                        throw new ModelViolation(message: $"{(Node)i} landed {id}@{landed.Epoch} under {arr.Key} without home's hop record granting it");
                    }
                }
            }
            foreach (var arr in home.Arrs.Values.Where(a => a.Readopted.Contains(value: id))) {
                var landed = arr.Members.First(m => m.Id == id);

                if (!home.Hops.Values.Any(h => (h.Id == id) && (h.To == arr.From) && (h.Next == (landed.Epoch - 1)))) {
                    throw new ModelViolation(message: $"home re-adopted {id}@{landed.Epoch} from {arr.From} without a hop that placed it there at {landed.Epoch - 1}");
                }
            }
            // No silent re-issue is checked at convergence (the notice is a durable obligation); a recall's epoch
            // exceeds every epoch issued before that recall.
            if (custody.Recalled && !custody.RecallSuperseded) {
                throw new ModelViolation(message: $"recall of {id} did not supersede every epoch issued before it");
            }
            // Durable state never regresses below what home last committed; a recall restarts the lineage at the image
            // it restored, and only copies of that lineage (at or above the recall's epoch) are measured against it.
            foreach (var (at, body) in copies) {
                var floor = ((custody.Recalled && (body.Epoch >= custody.RecallEpoch)) ? custody.RecallImage : (custody.Recalled ? 0 : custody.KnownImage));

                if (body.Image < floor) {
                    throw new ModelViolation(message: $"{at} embodies {id} with image {body.Image} below the committed {floor}");
                }
            }
        }
        for (var i = 0; i < 3; i++) {
            var view = views[i];

            // Observation: a companion's recorded binding names a player member and the slot that member landed at;
            // the binding is live exactly when that player stands at that slot now.
            foreach (var id in s.CompanionIds.Where(id => view.Has(id: id))) {
                var body = view.Body(id: id)!;
                // Read from the recorded binding: the followed member id, the slot it landed at and its generation there.
                var live = (body.Follows is { } f) && (body.FollowsSlot >= 0) && (view.Body(id: f) is { } player) && (player.Slot == body.FollowsSlot) && ((body.FollowsGen < 0) || (player.Gen == body.FollowsGen)) && (f == Ids.Player);

                if (view.Follows(id: id) != live) {
                    throw new ModelViolation(message: $"{(Node)i}: {id}'s binding (follows {body.Follows} at slot {body.FollowsSlot}) disagrees with the live follow");
                }
                // The placement binding, which follows the home seat at any generation, is home's alone.
                if ((i != 0) && (body.FollowsSlot >= 0) && (body.FollowsGen < 0)) {
                    throw new ModelViolation(message: $"{(Node)i}: {id} carries home's placement binding abroad");
                }
                if ((body.Follows is { } followed) && (followed != Ids.Player)) {
                    throw new ModelViolation(message: $"{(Node)i}: {id} is bound to {followed}, which is not a player");
                }
            }
            // Admission accounting: humans within the cap, companions never counted.
            if (view.Humans > HumanCap) {
                throw new ModelViolation(message: $"{(Node)i} admits {view.Humans} humans over its cap");
            }
            // Slots: no two bodies share one.
            if (view.Bodies.Values.Select(static b => b.Slot).Distinct().Count() != view.Bodies.Count) {
                throw new ModelViolation(message: $"{(Node)i}: two bodies share a slot");
            }
            // A dead key never lands; a key is leased once.
            foreach (var key in view.DeadKeys) {
                if (view.Arrs.ContainsKey(key: key)) {
                    throw new ModelViolation(message: $"{(Node)i} landed the dead key {key}");
                }
            }
            if (s.Nodes[i].Records.OfType<Leased>().GroupBy(static l => l.Key).Any(static g => g.Count() > 1)) {
                throw new ModelViolation(message: $"{(Node)i} leased a key twice");
            }
            if (s.Nodes[i].Records.OfType<Minted>().GroupBy(static m => m.Key).Any(static g => g.Count() > 1)) {
                throw new ModelViolation(message: $"{(Node)i} minted a key twice");
            }
        }
    }

    /// <summary>The append boundary: one step appends at most one record at each authority, so recovery can find the
    /// state between any two records.</summary>
    public static void CheckStep(ModelState before, ModelState after, string name) {
        for (var i = 0; i < 3; i++) {
            var appended = after.Nodes[i].Records.Count - before.Nodes[i].Records.Count;

            if (appended > 1) {
                throw new ModelViolation(message: $"'{name}' appended {appended} records at {(Node)i} in one step");
            }
        }
    }

    /// <summary>Fair completion: heals every link, recovers every latched authority, delivers every message and runs
    /// every protocol move with no further faults, lets deadlines pass when nothing else moves, then requires every
    /// title and body to agree and every obligation to be settled.</summary>
    public static ModelState Converge(ModelState s, int maxSteps = 800) {
        var state = s.Clone();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        state.Converging = true;
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                state.LinkUp[i, j] = true;
            }
            if (state.Nodes[i].Latched || (state.Nodes[i].NextAppend != Append.None)) {
                state.Nodes[i].Crash();
            }
            state.Nodes[i].Retries.Clear();
            state.Nodes[i].Sweeps.Clear();
        }
        for (var step = 0; step < maxSteps; step++) {
            CheckInvariants(s: state);
            // A state seen before means the protocol cycles without a new record: only a passing deadline may break it.
            if (!seen.Add(item: state.Canon())) {
                if (!PassDeadlines(state: state)) {
                    throw new ModelViolation(message: "convergence cycles without progress");
                }
                seen.Clear();
                continue;
            }
            if (state.Network.Count > 0) {
                // The head of the first lane in canonical order.
                var msg = LaneHeads(s: state).OrderBy(static m => m.Canon, StringComparer.Ordinal).First();
                var before = state.Clone();

                _ = state.Network.Remove(item: msg);
                Deliver(s: state, msg: msg);
                CheckStep(before: before, after: state, name: $"deliver {msg.Canon}");
                continue;
            }
            var moved = false;

            foreach (var (name, next) in ProgressActions(s: state)) {
                if (name.StartsWith("cross", StringComparison.Ordinal)) {
                    continue;
                }
                CheckStep(before: state, after: next, name: name);
                state = next;
                moved = true;
                break;
            }
            if (!moved && !PassDeadlines(state: state)) {
                break;
            }
            for (var i = 0; i < 3; i++) {
                state.Nodes[i].Retries.Clear();
            }
        }
        if (state.Network.Count > 0) {
            throw new ModelViolation(message: "convergence did not quiesce");
        }
        CheckConverged(s: state);
        return state;
    }
    // Lease deadlines pass, grace windows elapse, and held verifications and hop waits time out.
    private static bool PassDeadlines(ModelState state) {
        var passed = false;

        for (var i = 0; i < 3; i++) {
            var node = state.Nodes[i];

            if (node.Leases.Count > 0) {
                node.Leases.Clear();
                passed = true;
            }
            var view = Fold(s: state, at: (Node)i);

            foreach (var id in state.CompanionIds.Where(id => view.Stranded(id: id) && !view.GraceElapsed(id: id) && view.Has(id: id)).ToArray()) {
                _ = TryRecord(node: node, rec: new GraceElapsed(Id: id, Visit: view.PlayerVisits));
                passed = true;
            }
            foreach (var (key, outbound) in node.Outbound.ToArray()) {
                if (outbound.Phase != "Authorizing") {
                    continue;
                }
                foreach (var member in outbound.Members.Where(m => !m.IsPlayer && !outbound.Granted.ContainsKey(key: m.Id))) {
                    outbound.Granted[member.Id] = -1;
                    passed = true;
                }
                ReserveWhenAuthorized(s: state, at: node, key: key);
            }
            foreach (var (key, held) in node.Held.ToArray()) {
                foreach (var member in held.Members.Where(m => !m.IsPlayer && !node.Verified.ContainsKey(key: Verification(key: key, id: m.Id, phase: 'R')))) {
                    node.Verified[Verification(key: key, id: member.Id, phase: 'R')] = -1;
                }
                _ = node.Held.Remove(key: key);
                OnReserve(s: state, to: node, view: Fold(s: state, at: (Node)i), msg: held);
                passed = true;
            }
        }
        return passed;
    }
    public static void CheckConverged(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(s: s, at: (Node)i);
            if (views[i].InDoubt.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps an in-doubt departure after convergence: {views[i].InDoubt[0].Canon}");
            }
            if (s.Nodes[i].Outbound.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps an attempt after convergence: {s.Nodes[i].Outbound.First().Key}");
            }
            if (s.Nodes[i].Leases.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps a lease after convergence");
            }
            if (s.Nodes[i].Held.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps a held reservation after convergence");
            }
        }
        var home = views[0];

        if (home.Hops.Keys.Any(k => !home.HopsDone.Contains(item: k))) {
            throw new ModelViolation(message: "home keeps a pending hop after convergence");
        }
        // Every write to the title's current lineage: all writes, or after a recall those at or above its epoch plus
        // the image the recall restored.
        var expectedImage = new Dictionary<int, int>();

        foreach (var id in s.CompanionIds) {
            var custody = home.Custody[id];

            expectedImage[id] = (custody.Recalled
                ? (custody.RecallImage + s.Nodes.Sum(n => n.Records.OfType<Write>().Count(w => (w.Id == id) && (w.Epoch >= custody.RecallEpoch))))
                : s.Nodes.Sum(n => n.Records.OfType<Write>().Count(w => w.Id == id)));
        }
        foreach (var id in s.CompanionIds) {
            var custody = home.Custody[id];
            var embodied = Enumerable.Range(0, 3).Where(i => views[i].Has(id: id)).ToArray();

            if (custody.Recalls.Any(e => !home.Noticed.Contains(item: $"{id}:recalled@{e}"))) {
                throw new ModelViolation(message: $"recall of {id} without a notice");
            }
            if (custody.Installed && !home.Noticed.Contains(item: $"{id}:released")) {
                throw new ModelViolation(message: $"release of {id} without a notice");
            }
            if (custody.Kind == CustodyKind.Released) {
                // A copy abroad learns of the release on its next contact with home; until then it follows its player.
                foreach (var index in embodied) {
                    if ((index == 0) || !views[index].Follows(id: id)) {
                        throw new ModelViolation(message: $"released title {id} still embodied at {(Node)index}");
                    }
                }
                continue;
            }
            // A copy below the title's epoch is a stale puppet (a custodian that returned the companion while keeping
            // a body of its own, or one a recall superseded): it is refused on any contact and retires then, so at
            // convergence it may remain only where it still follows its player and never speaks to home.
            foreach (var index in embodied.Where(index => (index != 0) && (views[index].Body(id: id)!.Epoch < custody.Epoch))) {
                if (!views[index].Follows(id: id)) {
                    throw new ModelViolation(message: $"stale copy of {id} stays separated at {(Node)index}");
                }
            }
            embodied = embodied.Where(index => (index == 0) || (views[index].Body(id: id)!.Epoch >= custody.Epoch)).ToArray();
            if (embodied.Length != 1) {
                throw new ModelViolation(message: $"title {id} {custody.Kind} {custody.Custodian}@{custody.Epoch} but embodied at {embodied.Length} authorities (lost title)");
            }
            var at = (Node)embodied[0];
            var body = views[(int)at].Body(id: id)!;

            if ((at == Node.H) ? (custody.Kind != CustodyKind.Home) || (custody.Epoch != body.Epoch) : ((custody.Kind != CustodyKind.Away) || (custody.Custodian != at) || (custody.Epoch != body.Epoch))) {
                throw new ModelViolation(message: $"{id} at {at}@{body.Epoch} but title says {custody.Kind} {custody.Custodian}@{custody.Epoch}");
            }
            if ((at != Node.H) && !views[(int)at].Follows(id: id)) {
                throw new ModelViolation(message: $"{id} stays separated at {at}");
            }
            // Durable state: every write to the current lineage reached the embodied copy.
            if (body.Image != expectedImage[id]) {
                throw new ModelViolation(message: $"{id} carries image {body.Image} where {expectedImage[id]} is expected");
            }
        }
    }
}
