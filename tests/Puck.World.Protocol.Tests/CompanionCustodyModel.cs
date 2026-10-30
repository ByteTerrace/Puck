using System.Text;

namespace Puck.World.Protocol.Tests;

// A small-scope model of the companion custody protocol (docs/plans/companion-redesign.md): one home H holding the
// companion's title, two custodians A and B, one player P the companion follows, and a lossy network. It models the
// protocol's records and messages, never the engine's classes. Every custody fact an authority holds is a function
// of its durable record list (CustodyModel.Fold); the only volatile state is leases, pre-departure attempts and
// retry counters, which a crash clears. The law in CompanionCustodyModelLawTests explores it exhaustively under a
// fault budget and asserts the decision's invariants on every reachable state.

public enum Node : byte { H = 0, A = 1, B = 2 }

public enum MemberKind : byte { Player = 0, Companion = 1 }

/// <summary>One member of a reservation or commit, named by a request-local id that never changes when the cohort is
/// filtered. A companion carries the custody ticket home recorded for the hop it rides (epoch and hop key).</summary>
public sealed record Member(int Id, MemberKind Kind, int? Follows, int Epoch, string Hop) {
    public const int PlayerId = 1;
    public const int CompanionId = 2;

    public string Canon => $"{Id}{(Kind == MemberKind.Player ? "P" : "C")}{(Follows?.ToString() ?? "-")}@{Epoch}/{Hop}";
}

public enum MsgKind : byte {
    Reserve, Reserved, ReserveRefused, HopRequest, HopGranted, HopRefused, Commit, Committed, Missing, Status, StatusReply, HopSettled, HopSettledAck,
}

public sealed record Msg(Node From, Node To, MsgKind Kind, string Key, Member[] Members, string Arg, bool Dup) {
    public string Canon => $"{From}>{To}:{Kind}:{Key}:{string.Join(',', Members.Select(static m => m.Canon))}:{Arg}:{(Dup ? 1 : 0)}";
}

public abstract record Rec {
    public abstract string Canon { get; }
}

/// <summary>A source detached members toward <paramref name="To"/>; in doubt until a Settle names the key. A shed
/// departure detached a stranded companion into this durable return record before any reservation.</summary>
public sealed record Dep(string Key, Node To, Member[] Members, bool Shed) : Rec {
    public override string Canon => $"Dep({Key}>{To}:{string.Join(',', Members.Select(static m => m.Canon))}{(Shed ? ":shed" : "")})";
}
/// <summary>A destination landed members; at home with the companion, Readopted says the ticket matched custody.</summary>
public sealed record Arr(string Key, Node From, Member[] Members, bool Readopted) : Rec {
    public override string Canon => $"Arr({Key}<{From}:{string.Join(',', Members.Select(static m => m.Canon))}{(Readopted ? ":readopt" : "")})";
}
/// <summary>A source settled a departure: arrived, stayed (members restored) or retired (the image destroyed on home's
/// definitive refusal).</summary>
public sealed record Settle(string Key, bool Arrived, bool Retired) : Rec {
    public override string Canon => $"Settle({Key}:{(Arrived ? "arrived" : (Retired ? "retired" : "stayed"))})";
}
/// <summary>A custodian destroyed its embodied copy on home's definitive refusal (Stale or Released).</summary>
public sealed record Retire(string Cause) : Rec {
    public override string Canon => $"Retire({Cause})";
}
/// <summary>Home authorized a custody hop of the companion from one custodian to another.</summary>
public sealed record Hop(string Key, Node From, Node To, int Epoch, int Next) : Rec {
    public override string Canon => $"Hop({Key}:{From}>{To}:{Epoch}>{Next})";
}
/// <summary>Home settled a hop: the companion arrived at the hop's destination, or did not.</summary>
public sealed record HopDone(string Key, bool CompanionArrived) : Rec {
    public override string Canon => $"HopDone({Key}:{(CompanionArrived ? 1 : 0)})";
}
/// <summary>A custodian received home's acknowledgement of its hop settlement notice.</summary>
public sealed record HopAcked(string Key) : Rec {
    public override string Canon => $"HopAcked({Key})";
}
/// <summary>Home recalled the title under a new epoch, re-embodying its last committed image.</summary>
public sealed record Recall(int NewEpoch) : Rec {
    public override string Canon => $"Recall({NewEpoch})";
}
/// <summary>Home's author removed the companion's placement (a taped definition install): the title is released.</summary>
public sealed record Install : Rec {
    public override string Canon => "Install";
}
/// <summary>A custodian found the companion separated with no way home and opened its grace window.</summary>
public sealed record Stranded : Rec {
    public override string Canon => "Stranded";
}
/// <summary>The custodian's grace window elapsed (its own ticks).</summary>
public sealed record GraceElapsed : Rec {
    public override string Canon => "GraceElapsed";
}
/// <summary>A destination leased this key; it never leases it again.</summary>
public sealed record Leased(string Key) : Rec {
    public override string Canon => $"Leased({Key})";
}
/// <summary>A destination answered Missing for this key (a commit or status with no live lease and no arrival); every
/// later reservation or commit under it is refused, so Missing is final.</summary>
public sealed record Dead(string Key) : Rec {
    public override string Canon => $"Dead({Key})";
}
/// <summary>A custodian's later reservation attempt for a shed image, under a fresh key, naming the departure it carries.</summary>
public sealed record Attempt(string Key, string Image) : Rec {
    public override string Canon => $"Attempt({Key}>{Image})";
}
public sealed record PlayerLeft : Rec {
    public override string Canon => "PlayerLeft";
}
public sealed record PlayerJoined : Rec {
    public override string Canon => "PlayerJoined";
}
public sealed record Notice(string Text) : Rec {
    public override string Canon => $"Notice({Text})";
}

public enum CustodyKind : byte { Home, Pending, Away, Released }

/// <summary>What one authority's durable records say (the reducer live execution, recovery and replay share).</summary>
public sealed class View {
    public bool CompanionBody;
    public int CompanionEpoch;
    public string CompanionHop = "";
    public bool CompanionFollows;
    public bool PlayerBody;
    public string? PlayerArrivalKey;
    public string? CompanionArrivalKey;
    public bool Stranded;
    public bool GraceElapsed;
    public List<Dep> InDoubt = new();
    public List<Hop> PendingHops = new();
    public List<Settle> UnackedSettles = new();
    public Dictionary<string, string> ImageOfAttempt = new(StringComparer.Ordinal);
    public Dictionary<string, string> LatestAttempt = new(StringComparer.Ordinal);
    public HashSet<string> LeasedKeys = new(StringComparer.Ordinal);
    public HashSet<string> DeadKeys = new(StringComparer.Ordinal);

    public string ImageOf(string key) => (ImageOfAttempt.TryGetValue(key, out var image) ? image : key);
    // Home only.
    public CustodyKind Custody = CustodyKind.Home;
    public Node Custodian = Node.H;
    public int Epoch;
    public string PendingHopKey = "";
    public int MaxEpoch;
    public bool Recalled;
}

public sealed record Lease(Node From, Member[] Members);
public sealed record Outbound(Node To, Member[] Members, string Phase); // Reserving | AwaitHop | ShedReserving

public sealed class NodeState {
    public readonly Node Id;
    public readonly bool AcceptsRole;
    public List<Rec> Records = new();
    public SortedDictionary<string, Lease> Leases = new(StringComparer.Ordinal);
    public SortedDictionary<string, Outbound> Outbound = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Retries = new(StringComparer.Ordinal);
    public int KeyCounter;
    public int Sweeps;

    public NodeState(Node id, bool acceptsRole) {
        Id = id;
        AcceptsRole = acceptsRole;
    }
    public NodeState Clone() {
        var copy = new NodeState(id: Id, acceptsRole: AcceptsRole) {
            Records = new List<Rec>(collection: Records),
            Leases = new SortedDictionary<string, Lease>(dictionary: Leases, comparer: StringComparer.Ordinal),
            Outbound = new SortedDictionary<string, Outbound>(dictionary: Outbound, comparer: StringComparer.Ordinal),
            Retries = new SortedDictionary<string, int>(dictionary: Retries, comparer: StringComparer.Ordinal),
            KeyCounter = KeyCounter,
            Sweeps = Sweeps,
        };

        return copy;
    }
    public void Canon(StringBuilder sb) {
        sb.Append(Id).Append('{').Append(KeyCounter).Append(',').Append(Sweeps).Append('|');
        foreach (var rec in Records) {
            sb.Append(rec.Canon).Append(';');
        }
        sb.Append('|');
        foreach (var (key, lease) in Leases) {
            sb.Append(key).Append('<').Append(lease.From).Append(':').Append(string.Join(',', lease.Members.Select(static m => m.Canon))).Append(';');
        }
        sb.Append('|');
        foreach (var (key, outbound) in Outbound) {
            sb.Append(key).Append('>').Append(outbound.To).Append(':').Append(outbound.Phase).Append(':').Append(string.Join(',', outbound.Members.Select(static m => m.Canon))).Append(';');
        }
        sb.Append('|');
        foreach (var (key, count) in Retries) {
            sb.Append(key).Append('=').Append(count).Append(';');
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
    Expiry = 16,
    Install = 32,
    Recall = 64,
    Disconnect = 128,
    Timeout = 256,
    Forge = 512,
    All = 1023,
}

public sealed class Budget {
    public int Drops, Dups, Crashes, Partitions, LeaseExpiries, Forged, Installs, Recalls, Disconnects, Reconnects, HopTimeouts;
    public int Faults;
    public Fault Allowed = Fault.All;

    public bool Allows(Fault fault) => ((Allowed & fault) != 0);
    // Whether something may have been lost, so the protocol's retries have work to do.
    public bool Lossy => ((Drops + Crashes + Partitions + LeaseExpiries + HopTimeouts) > 0);
    public Budget Clone() => (Budget)MemberwiseClone();
    public string Canon => $"{Drops}{Dups}{Crashes}{Partitions}{LeaseExpiries}{Forged}{Installs}{Recalls}{Disconnects}{Reconnects}{HopTimeouts}{Faults}";
}

public sealed class ModelState {
    public NodeState[] Nodes;
    public List<Msg> Network = new();
    public bool[,] LinkUp = new bool[3, 3];
    public Node? PlayerAt = Node.H;
    public int Itinerary;
    public Node[] Route = [];
    public Budget Budget = new();
    public List<string> Trace = new();
    // Set by Converge: every recovery move is enabled whatever the fault budget says.
    public bool Converging;

    public ModelState(bool aAccepts, bool bAccepts, Node[] route) {
        Route = route;
        Nodes = [new NodeState(id: Node.H, acceptsRole: true), new NodeState(id: Node.A, acceptsRole: aAccepts), new NodeState(id: Node.B, acceptsRole: bAccepts)];
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                LinkUp[i, j] = true;
            }
        }
        Nodes[0].Records.Add(item: new PlayerJoined());
    }
    private ModelState(NodeState[] nodes) {
        Nodes = nodes;
    }
    public ModelState Clone() {
        var copy = new ModelState(nodes: [Nodes[0].Clone(), Nodes[1].Clone(), Nodes[2].Clone()]) {
            Network = new List<Msg>(collection: Network),
            LinkUp = (bool[,])LinkUp.Clone(),
            PlayerAt = PlayerAt,
            Itinerary = Itinerary,
            Route = Route,
            Converging = Converging,
            Budget = Budget.Clone(),
            Trace = new List<string>(collection: Trace),
        };

        return copy;
    }
    public NodeState At(Node node) => Nodes[(int)node];
    public string Canon() {
        var sb = new StringBuilder();

        sb.Append(PlayerAt?.ToString() ?? "-").Append(Itinerary).Append(Budget.Canon).Append('/');
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                sb.Append(LinkUp[i, j] ? '1' : '0');
            }
        }
        sb.Append('/');
        foreach (var msg in Network.Select(static m => m.Canon).Order(comparer: StringComparer.Ordinal)) {
            sb.Append(msg).Append(';');
        }
        sb.Append('/');
        foreach (var node in Nodes) {
            node.Canon(sb: sb);
        }

        return sb.ToString();
    }
}

public sealed class ModelViolation(string message) : Exception(message) { }

/// <summary>The protocol: pure folds over records, message handlers, and the enabled actions of a state.</summary>
public static class CustodyModel {
    public const int SweepLimit = 1;
    public const int RetryLimit = 1;

    // ---- the reducer ----------------------------------------------------------------------------------------------

    public static View Fold(NodeState node) {
        var view = new View();
        var hops = new Dictionary<string, Hop>(comparer: StringComparer.Ordinal);
        var done = new HashSet<string>(comparer: StringComparer.Ordinal);
        var settled = new HashSet<string>(comparer: StringComparer.Ordinal);
        var acked = new HashSet<string>(comparer: StringComparer.Ordinal);
        var deps = new Dictionary<string, Dep>(comparer: StringComparer.Ordinal);
        // Hops a recall overtook: their settlement no longer moves custody.
        var superseded = new HashSet<string>(comparer: StringComparer.Ordinal);
        var released = false;

        if (node.Id == Node.H) {
            view.CompanionBody = true;
        }
        foreach (var rec in node.Records) {
            switch (rec) {
                case PlayerJoined:
                    view.PlayerBody = true;
                    view.PlayerArrivalKey = null;
                    break;
                case PlayerLeft:
                    view.PlayerBody = false;
                    break;
                case Arr arr:
                    foreach (var member in arr.Members) {
                        if (member.Kind == MemberKind.Player) {
                            view.PlayerBody = true;
                            view.PlayerArrivalKey = arr.Key;
                        } else {
                            view.CompanionBody = true;
                            view.CompanionEpoch = member.Epoch;
                            view.CompanionHop = member.Hop;
                            view.CompanionArrivalKey = arr.Key;
                            view.Stranded = false;
                            view.GraceElapsed = false;
                            if ((node.Id == Node.H) && arr.Readopted) {
                                view.Custody = CustodyKind.Home;
                                view.Custodian = Node.H;
                                view.Epoch = member.Epoch;
                                view.PendingHopKey = "";
                            }
                        }
                    }
                    break;
                case Dep dep:
                    deps[dep.Key] = dep;
                    foreach (var member in dep.Members) {
                        if (member.Kind == MemberKind.Player) {
                            view.PlayerBody = false;
                        } else {
                            view.CompanionBody = false;
                        }
                    }
                    break;
                case Settle settle:
                    _ = settled.Add(item: settle.Key);
                    if (!settle.Arrived && !settle.Retired && deps.TryGetValue(key: settle.Key, value: out var restored)) {
                        foreach (var member in restored.Members) {
                            if (member.Kind == MemberKind.Player) {
                                view.PlayerBody = true;
                            } else {
                                view.CompanionBody = true;
                            }
                        }
                    }
                    break;
                case Retire:
                    view.CompanionBody = false;
                    break;
                case Hop hop:
                    hops[hop.Key] = hop;
                    view.Custody = CustodyKind.Pending;
                    view.PendingHopKey = hop.Key;
                    view.MaxEpoch = Math.Max(view.MaxEpoch, hop.Next);
                    break;
                case HopDone hopDone:
                    _ = done.Add(item: hopDone.Key);
                    if (hops.TryGetValue(key: hopDone.Key, value: out var settledHop) && !released && !superseded.Contains(item: hopDone.Key)) {
                        if (hopDone.CompanionArrived) {
                            view.Custody = ((settledHop.To == Node.H) ? CustodyKind.Home : CustodyKind.Away);
                            view.Custodian = settledHop.To;
                            view.Epoch = settledHop.Next;
                        } else {
                            view.Custody = ((settledHop.From == Node.H) ? CustodyKind.Home : CustodyKind.Away);
                            view.Custodian = settledHop.From;
                            view.Epoch = settledHop.Epoch;
                        }
                        view.PendingHopKey = "";
                    }
                    break;
                case HopAcked hopAcked:
                    _ = acked.Add(item: hopAcked.Key);
                    break;
                case Recall recall:
                    foreach (var key in hops.Keys) {
                        if (!done.Contains(item: key)) {
                            _ = superseded.Add(item: key);
                        }
                    }
                    view.Custody = CustodyKind.Home;
                    view.Custodian = Node.H;
                    view.Epoch = recall.NewEpoch;
                    view.MaxEpoch = Math.Max(view.MaxEpoch, recall.NewEpoch);
                    view.PendingHopKey = "";
                    view.CompanionBody = true;
                    view.CompanionEpoch = recall.NewEpoch;
                    view.Recalled = true;
                    break;
                case Install:
                    released = true;
                    view.Custody = CustodyKind.Released;
                    view.PendingHopKey = "";
                    view.CompanionBody = false;
                    break;
                case Stranded:
                    view.Stranded = true;
                    break;
                case GraceElapsed:
                    view.GraceElapsed = true;
                    break;
                case Leased leased:
                    _ = view.LeasedKeys.Add(item: leased.Key);
                    break;
                case Dead dead:
                    _ = view.DeadKeys.Add(item: dead.Key);
                    break;
                case Attempt attempt:
                    view.ImageOfAttempt[attempt.Key] = attempt.Image;
                    view.LatestAttempt[attempt.Image] = attempt.Key;
                    break;
            }
        }
        if (released) {
            view.Custody = CustodyKind.Released;
        }
        foreach (var dep in deps.Values) {
            if (!settled.Contains(item: dep.Key)) {
                view.InDoubt.Add(item: dep);
            }
        }
        foreach (var hop in hops.Values) {
            if (!done.Contains(item: hop.Key)) {
                view.PendingHops.Add(item: hop);
            }
        }
        foreach (var rec in node.Records) {
            if ((rec is Settle settle) && (node.Id != Node.H) && !acked.Contains(item: settle.Key) && deps.TryGetValue(key: settle.Key, value: out var dep) && (dep.To != Node.H) && dep.Members.Any(static m => m.Kind == MemberKind.Companion)) {
                view.UnackedSettles.Add(item: settle);
            }
        }
        // The companion follows the player only while both stand here from the same arrival (or at home while the
        // player sits in its home seat).
        view.CompanionFollows = view.CompanionBody && view.PlayerBody && ((node.Id == Node.H) || ((view.CompanionArrivalKey is not null) && (view.CompanionArrivalKey == view.PlayerArrivalKey)));
        return view;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static void Send(ModelState s, Node from, Node to, MsgKind kind, string key, Member[] members, string arg = "") {
        s.Network.Add(item: new Msg(From: from, To: to, Kind: kind, Key: key, Members: members, Arg: arg, Dup: false));
    }
    private static void Record(ModelState s, Node at, Rec rec) {
        s.At(node: at).Records.Add(item: rec);
    }
    private static string Mint(ModelState s, Node at) {
        var node = s.At(node: at);

        node.KeyCounter++;
        return $"{at}{node.KeyCounter}";
    }
    private static Member Player() => new(Id: Member.PlayerId, Kind: MemberKind.Player, Follows: null, Epoch: 0, Hop: "");
    private static Member Companion(int epoch, string hop, int? follows) => new(Id: Member.CompanionId, Kind: MemberKind.Companion, Follows: follows, Epoch: epoch, Hop: hop);
    private static Member? CompanionOf(Member[] members) => members.FirstOrDefault(static m => m.Kind == MemberKind.Companion);
    private static bool InFlight(ModelState s, Node from, string key, MsgKind kind) => s.Network.Any(m => (m.From == from) && (m.Key == key) && (m.Kind == kind));
    private static bool Reachable(ModelState s, Node a, Node b) => (a == b) || (s.LinkUp[(int)a, (int)b] && s.LinkUp[(int)b, (int)a]);

    // ---- handlers -------------------------------------------------------------------------------------------------

    public static void Deliver(ModelState s, Msg msg) {
        var to = s.At(node: msg.To);
        var view = Fold(node: to);

        switch (msg.Kind) {
            case MsgKind.Reserve: OnReserve(s: s, to: to, view: view, msg: msg); break;
            case MsgKind.Reserved: OnReserved(s: s, at: to, msg: msg); break;
            case MsgKind.ReserveRefused: OnReserveRefused(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.HopRequest: OnHopRequest(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopGranted: OnHopGranted(s: s, at: to, msg: msg); break;
            case MsgKind.HopRefused: OnHopRefused(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.Commit: OnCommit(s: s, to: to, view: view, msg: msg); break;
            case MsgKind.Committed: OnCommitted(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.Missing: OnMissing(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.Status: OnStatus(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.StatusReply: OnStatusReply(s: s, at: to, view: view, msg: msg); break;
            case MsgKind.HopSettled: OnHopSettled(s: s, home: to, view: view, msg: msg); break;
            case MsgKind.HopSettledAck:
                if (!to.Records.Any(r => (r is HopAcked a) && (a.Key == msg.Key))) {
                    Record(s: s, at: msg.To, rec: new HopAcked(Key: msg.Key));
                }
                break;
        }
    }

    // The destination decides each member on its own: a player is admitted; a companion is admitted for a declared
    // role when no body here already holds its incarnation; at home a companion is a return, verified against the
    // custody record (the exact custodian at the exact epoch) and bound to the held slot.
    private static void OnReserve(ModelState s, NodeState to, View view, Msg msg) {
        if (to.Leases.TryGetValue(key: msg.Key, value: out var existing)) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: [], arg: string.Join(',', existing.Members.Select(static m => m.Id)));
            return;
        }
        if (to.Records.Any(r => (r is Arr a) && (a.Key == msg.Key))) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: [], arg: LandedIds(to: to, key: msg.Key));
            return;
        }
        // A key is leased once: a lease this authority lost (a crash, a deadline) or a commit it refused ends the key.
        if (view.DeadKeys.Contains(item: msg.Key) || view.LeasedKeys.Contains(item: msg.Key)) {
            Tombstone(s: s, to: to, view: view, key: msg.Key);
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: "Dead");
            return;
        }

        var accepted = new List<Member>();
        string? companionRefusal = null;

        foreach (var member in msg.Members) {
            if (member.Kind == MemberKind.Player) {
                accepted.Add(item: member);
                continue;
            }
            if (to.Id == Node.H) {
                var verdict = VerifyReturn(s: s, home: to, view: view, from: msg.From, member: member);

                if (verdict is null) {
                    accepted.Add(item: member);
                } else {
                    companionRefusal = verdict;
                }
                continue;
            }
            if (!to.AcceptsRole) {
                companionRefusal = "Role";
            } else if (view.CompanionBody || to.Leases.Values.Any(static l => l.Members.Any(static m => m.Kind == MemberKind.Companion))) {
                companionRefusal = "Duplicate";
            } else {
                accepted.Add(item: member);
            }
        }
        // One lease per incarnation at home too: a second attempt for the same companion waits for the first.
        if ((to.Id == Node.H) && (CompanionOf(members: accepted.ToArray()) is not null) && to.Leases.Values.Any(static l => l.Members.Any(static m => m.Kind == MemberKind.Companion))) {
            accepted.RemoveAll(static m => m.Kind == MemberKind.Companion);
            companionRefusal = "Duplicate";
        }
        if (accepted.Count == 0) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.ReserveRefused, key: msg.Key, members: [], arg: companionRefusal ?? "Empty");
            return;
        }
        Record(s: s, at: to.Id, rec: new Leased(Key: msg.Key));
        to.Leases[msg.Key] = new Lease(From: msg.From, Members: accepted.ToArray());
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Reserved, key: msg.Key, members: [], arg: string.Join(',', accepted.Select(static m => m.Id)) + ((companionRefusal is null) ? "" : $"!{companionRefusal}"));
    }
    private static void Tombstone(ModelState s, NodeState to, View view, string key) {
        if (!view.DeadKeys.Contains(item: key)) {
            Record(s: s, at: to.Id, rec: new Dead(Key: key));
            _ = view.DeadKeys.Add(item: key);
        }
    }
    // Home's one verification door for a returning companion. A pending hop is resolved, never answered "no row".
    private static string? VerifyReturn(ModelState s, NodeState home, View view, Node from, Member member) {
        switch (view.Custody) {
            case CustodyKind.Released:
                return "Released";
            case CustodyKind.Pending:
                if (!InFlight(s: s, from: Node.H, key: view.PendingHopKey, kind: MsgKind.Status)) {
                    var hop = view.PendingHops.First(h => h.Key == view.PendingHopKey);

                    Send(s: s, from: Node.H, to: hop.To, kind: MsgKind.Status, key: hop.Key, members: []);
                }
                return "Pending";
            case CustodyKind.Home:
                return "Stale";
            default:
                if ((view.Custodian != from) || (view.Epoch != member.Epoch)) {
                    return "Stale";
                }
                if (view.CompanionBody) {
                    throw new ModelViolation(message: "home embodies the companion while its custody record says away");
                }
                return null;
        }
    }
    private static void OnReserved(ModelState s, NodeState at, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound)) {
            return;
        }

        var parts = msg.Arg.Split('!');
        var acceptedIds = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var members = outbound.Members.Where(m => acceptedIds.Contains(item: m.Id)).ToArray();
        var companion = CompanionOf(members: outbound.Members);
        var refused = (companion is not null) && !acceptedIds.Contains(item: companion.Id);

        if (outbound.Phase == "ShedReserving") {
            if (refused) {
                // Duplicate: another lease names the incarnation; this attempt asks again when it expires.
                return;
            }
            _ = at.Outbound.Remove(key: msg.Key);
            Send(s: s, from: at.Id, to: outbound.To, kind: MsgKind.Commit, key: msg.Key, members: outbound.Members);
            return;
        }
        if (outbound.Phase != "Reserving") {
            return;
        }
        // An attempt whose bodies no longer stand here (a body already departed under another attempt) is stale.
        if (!BodiesPresent(at: at, members: outbound.Members)) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        if (refused) {
            var reason = ((parts.Length > 1) ? parts[1] : "Duplicate");

            if (reason == "Duplicate") {
                // Another lease still names this incarnation at the destination; it expires, and this request asks again.
                return;
            }
            Record(s: s, at: at.Id, rec: new Notice(Text: $"companion refused by {outbound.To}: {reason}"));
        }
        if (members.Length == 0) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        if ((CompanionOf(members: members) is { } crossing) && (outbound.To != Node.H)) {
            // An onward hop needs home's record before anything departs.
            if (at.Id == Node.H) {
                var view = Fold(node: at);
                var next = view.MaxEpoch + 1;

                Record(s: s, at: Node.H, rec: new Hop(Key: msg.Key, From: Node.H, To: outbound.To, Epoch: view.Epoch, Next: next));
                Depart(s: s, at: at, key: msg.Key, to: outbound.To, members: WithTicket(members: members, epoch: next, hop: msg.Key), shed: false);
                return;
            }
            at.Outbound[msg.Key] = new Outbound(To: outbound.To, Members: members, Phase: "AwaitHop");
            Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.HopRequest, key: msg.Key, members: [crossing], arg: $"{at.Id},{outbound.To},{crossing.Epoch}");
            return;
        }
        // A return to home, or a player crossing without its companion: detach and commit.
        Depart(s: s, at: at, key: msg.Key, to: outbound.To, members: members, shed: false);
    }
    private static bool BodiesPresent(NodeState at, Member[] members) {
        var view = Fold(node: at);

        return members.All(m => ((m.Kind == MemberKind.Player) ? view.PlayerBody : view.CompanionBody));
    }
    private static Member[] WithTicket(Member[] members, int epoch, string hop) => members.Select(m => (m.Kind == MemberKind.Companion) ? (m with { Epoch = epoch, Hop = hop }) : m).ToArray();
    private static void Depart(ModelState s, NodeState at, string key, Node to, Member[] members, bool shed) {
        _ = at.Outbound.Remove(key: key);
        Record(s: s, at: at.Id, rec: new Dep(Key: key, To: to, Members: members, Shed: shed));
        Send(s: s, from: at.Id, to: to, kind: MsgKind.Commit, key: key, members: members);
    }
    private static void OnReserveRefused(ModelState s, NodeState at, View view, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound)) {
            return;
        }
        // Pending and Duplicate are not refusals: home is resolving its own hop record, or another lease for this
        // incarnation has yet to expire, and the same request asks again.
        if (msg.Arg is "Pending" or "Duplicate") {
            return;
        }
        _ = at.Outbound.Remove(key: msg.Key);
        if (msg.Arg == "Dead") {
            // The key is spent at the destination; a shed image tries again under a fresh key.
            if (outbound.Phase == "ShedReserving") {
                ReattemptShed(s: s, at: at, view: view, image: view.ImageOf(key: msg.Key));
            }
            return;
        }
        if (outbound.To != Node.H) {
            return;
        }
        if (msg.Arg is "Stale" or "Released") {
            // Home's definitive answer about its own title ends this copy's custody.
            if (outbound.Phase == "ShedReserving") {
                var image = view.ImageOf(key: msg.Key);

                if (view.InDoubt.Any(d => d.Key == image)) {
                    Record(s: s, at: at.Id, rec: new Settle(Key: image, Arrived: false, Retired: true));
                }
            } else if (view.CompanionBody) {
                Record(s: s, at: at.Id, rec: new Retire(Cause: msg.Arg));
            }
        }
    }
    // A shed image whose attempt died asks home again under a fresh key that names the departure it carries.
    private static void ReattemptShed(ModelState s, NodeState at, View view, string image) {
        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        var key = Mint(s: s, at: at.Id);

        Record(s: s, at: at.Id, rec: new Attempt(Key: key, Image: image));
        at.Outbound[key] = new Outbound(To: Node.H, Members: dep.Members, Phase: "ShedReserving");
        Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: dep.Members);
    }
    private static void OnHopRequest(ModelState s, NodeState home, View view, Msg msg) {
        var parts = msg.Arg.Split(',');
        var from = Enum.Parse<Node>(value: parts[0]);
        var to = Enum.Parse<Node>(value: parts[1]);
        var epoch = int.Parse(s: parts[2]);

        if (home.Records.FirstOrDefault(r => (r is Hop h) && (h.Key == msg.Key)) is Hop existing) {
            Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopGranted, key: msg.Key, members: [], arg: existing.Next.ToString());
            return;
        }
        var refusal = view.Custody switch {
            CustodyKind.Released => "Released",
            CustodyKind.Pending => "Pending",
            CustodyKind.Home => "Stale",
            _ => (((view.Custodian == from) && (view.Epoch == epoch) && (from == msg.From)) ? null : "Stale"),
        };

        if ((refusal == "Pending") && !InFlight(s: s, from: Node.H, key: view.PendingHopKey, kind: MsgKind.Status)) {
            var pending = view.PendingHops.First(h => h.Key == view.PendingHopKey);

            Send(s: s, from: Node.H, to: pending.To, kind: MsgKind.Status, key: pending.Key, members: []);
        }
        if (refusal is not null) {
            Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopRefused, key: msg.Key, members: [], arg: refusal);
            return;
        }

        var next = view.MaxEpoch + 1;

        Record(s: s, at: Node.H, rec: new Hop(Key: msg.Key, From: from, To: to, Epoch: epoch, Next: next));
        Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopGranted, key: msg.Key, members: [], arg: next.ToString());
    }
    private static void OnHopGranted(ModelState s, NodeState at, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound) || (outbound.Phase != "AwaitHop")) {
            return;
        }
        if (!BodiesPresent(at: at, members: outbound.Members)) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        Depart(s: s, at: at, key: msg.Key, to: outbound.To, members: WithTicket(members: outbound.Members, epoch: int.Parse(s: msg.Arg), hop: msg.Key), shed: false);
    }
    private static void OnHopRefused(ModelState s, NodeState at, View view, Msg msg) {
        if (!at.Outbound.TryGetValue(key: msg.Key, value: out var outbound) || (outbound.Phase != "AwaitHop")) {
            return;
        }
        if (msg.Arg == "Pending") {
            return;
        }
        Record(s: s, at: at.Id, rec: new Notice(Text: $"companion cannot follow to {outbound.To}: {msg.Arg}"));
        if ((msg.Arg is "Stale" or "Released") && view.CompanionBody) {
            Record(s: s, at: at.Id, rec: new Retire(Cause: msg.Arg));
        }

        var players = outbound.Members.Where(static m => m.Kind == MemberKind.Player).ToArray();

        if (players.Length == 0) {
            _ = at.Outbound.Remove(key: msg.Key);
            return;
        }
        Depart(s: s, at: at, key: msg.Key, to: outbound.To, members: players, shed: false);
    }
    // The commit carries the companion's ticket; the destination lands only the members it leased, binds the companion
    // to the player member landed in this same request, and at home re-verifies custody at the commit (the fence
    // against a recall or hop between lease and commit).
    private static void OnCommit(ModelState s, NodeState to, View view, Msg msg) {
        if (to.Records.Any(r => (r is Arr a) && (a.Key == msg.Key))) {
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Committed, key: msg.Key, members: [], arg: LandedIds(to: to, key: msg.Key));
            return;
        }
        if (!to.Leases.TryGetValue(key: msg.Key, value: out var lease)) {
            Tombstone(s: s, to: to, view: view, key: msg.Key);
            Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Missing, key: msg.Key, members: []);
            return;
        }

        var leased = lease.Members.Select(static m => m.Id).ToHashSet();
        var landing = msg.Members.Where(m => leased.Contains(item: m.Id)).ToArray();
        var readopted = false;

        if (CompanionOf(members: landing) is { } companion) {
            if (to.Id == Node.H) {
                if (VerifyReturn(s: s, home: to, view: view, from: msg.From, member: companion) is not null) {
                    _ = to.Leases.Remove(key: msg.Key);
                    Tombstone(s: s, to: to, view: view, key: msg.Key);
                    Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Missing, key: msg.Key, members: []);
                    return;
                }
                readopted = true;
            } else if (view.CompanionBody) {
                throw new ModelViolation(message: $"{to.Id} lands a second companion body");
            }
            if ((companion.Follows is { } follows) && !landing.Any(m => (m.Id == follows) && (m.Kind == MemberKind.Player))) {
                landing = landing.Select(m => (m.Kind == MemberKind.Companion) ? (m with { Follows = null }) : m).ToArray();
            }
        }
        _ = to.Leases.Remove(key: msg.Key);
        Record(s: s, at: msg.To, rec: new Arr(Key: msg.Key, From: msg.From, Members: landing, Readopted: readopted));
        if (landing.Any(static m => m.Kind == MemberKind.Player)) {
            s.PlayerAt = msg.To;
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.Committed, key: msg.Key, members: [], arg: string.Join(',', landing.Select(static m => m.Id)));
    }
    private static string LandedIds(NodeState to, string key) => string.Join(',', to.Records.OfType<Arr>().First(a => a.Key == key).Members.Select(static m => m.Id));
    private static void OnCommitted(ModelState s, NodeState at, View view, Msg msg) {
        var image = view.ImageOf(key: msg.Key);

        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        _ = at.Outbound.Remove(key: msg.Key);
        Record(s: s, at: at.Id, rec: new Settle(Key: image, Arrived: true, Retired: false));

        var landed = msg.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var companionArrived = landed.Contains(item: Member.CompanionId);

        if ((CompanionOf(members: dep.Members) is not null) && (dep.To != Node.H)) {
            if (at.Id == Node.H) {
                Record(s: s, at: Node.H, rec: new HopDone(Key: msg.Key, CompanionArrived: companionArrived));
            } else {
                Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.HopSettled, key: msg.Key, members: [], arg: (companionArrived ? "1" : "0"));
            }
        }
    }
    private static void OnMissing(ModelState s, NodeState at, View view, Msg msg) {
        var image = view.ImageOf(key: msg.Key);

        if (view.InDoubt.FirstOrDefault(d => d.Key == image) is not { } dep) {
            return;
        }
        // Only the latest attempt's answer counts; an older attempt's Missing is already superseded.
        if (view.LatestAttempt.TryGetValue(image, out var latest) && (latest != msg.Key)) {
            return;
        }
        if (dep.Shed) {
            // A shed image has no body to restore: the spent key is dead, so it asks again under a fresh one.
            if (!at.Outbound.ContainsKey(key: msg.Key)) {
                ReattemptShed(s: s, at: at, view: view, image: image);
            }
            return;
        }

        // Install wins over a departure: a released title restores no companion body.
        var retired = (at.Id == Node.H) && (view.Custody == CustodyKind.Released) && (CompanionOf(members: dep.Members) is not null);

        Record(s: s, at: at.Id, rec: new Settle(Key: image, Arrived: false, Retired: retired));
        if (dep.Members.Any(static m => m.Kind == MemberKind.Player)) {
            s.PlayerAt = at.Id;
        }
        if ((CompanionOf(members: dep.Members) is not null) && (dep.To != Node.H)) {
            if (at.Id == Node.H) {
                Record(s: s, at: Node.H, rec: new HopDone(Key: msg.Key, CompanionArrived: false));
            } else {
                Send(s: s, from: at.Id, to: Node.H, kind: MsgKind.HopSettled, key: msg.Key, members: [], arg: "0");
            }
        }
    }
    private static void OnStatus(ModelState s, NodeState at, View view, Msg msg) {
        string reply;

        if (at.Records.Any(r => (r is Arr a) && (a.Key == msg.Key))) {
            reply = "Committed:" + LandedIds(to: at, key: msg.Key);
        } else if (at.Leases.ContainsKey(key: msg.Key)) {
            reply = "Reserved";
        } else {
            // Answering Missing to a status query ends the key too, so the answer is final.
            Tombstone(s: s, to: at, view: view, key: msg.Key);
            reply = "Missing";
        }
        Send(s: s, from: msg.To, to: msg.From, kind: MsgKind.StatusReply, key: msg.Key, members: [], arg: reply);
    }
    private static void OnStatusReply(ModelState s, NodeState at, View view, Msg msg) {
        // Home resolving a pending hop it authorized.
        if ((at.Id == Node.H) && (view.PendingHops.FirstOrDefault(h => h.Key == msg.Key) is { } hop) && (hop.From != Node.H)) {
            if (msg.Arg.StartsWith("Committed", StringComparison.Ordinal)) {
                Record(s: s, at: Node.H, rec: new HopDone(Key: msg.Key, CompanionArrived: msg.Arg.Split(':')[1].Split(',').Contains(Member.CompanionId.ToString())));
            } else if (msg.Arg == "Missing") {
                Record(s: s, at: Node.H, rec: new HopDone(Key: msg.Key, CompanionArrived: false));
            }
            return;
        }
        // A source reconciling its in-doubt departure.
        if (view.InDoubt.FirstOrDefault(d => d.Key == view.ImageOf(key: msg.Key)) is not { } dep) {
            return;
        }
        if (msg.Arg.StartsWith("Committed", StringComparison.Ordinal)) {
            OnCommitted(s: s, at: at, view: view, msg: msg with { Kind = MsgKind.Committed, Arg = msg.Arg.Split(':')[1] });
        } else if (msg.Arg == "Missing") {
            OnMissing(s: s, at: at, view: view, msg: msg with { Kind = MsgKind.Missing });
        } else if (!InFlight(s: s, from: at.Id, key: msg.Key, kind: MsgKind.Commit)) {
            // Reserved: the destination still holds the lease, so the commit itself was lost.
            Send(s: s, from: at.Id, to: dep.To, kind: MsgKind.Commit, key: msg.Key, members: dep.Members);
        }
    }
    private static void OnHopSettled(ModelState s, NodeState home, View view, Msg msg) {
        if (home.Records.Any(r => (r is Hop h) && (h.Key == msg.Key))) {
            var arrived = (msg.Arg == "1");

            if (home.Records.FirstOrDefault(r => (r is HopDone d) && (d.Key == msg.Key)) is HopDone done) {
                if (done.CompanionArrived != arrived) {
                    throw new ModelViolation(message: $"hop {msg.Key} settled two ways");
                }
            } else {
                Record(s: s, at: Node.H, rec: new HopDone(Key: msg.Key, CompanionArrived: arrived));
            }
        }
        Send(s: s, from: Node.H, to: msg.From, kind: MsgKind.HopSettledAck, key: msg.Key, members: []);
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    /// <summary>Every successor of <paramref name="s"/>, in a fixed order, under the fault budget.</summary>
    public static IEnumerable<(string Name, ModelState Next)> Successors(ModelState s, int faultBudget, bool adversary) {
        // Network: deliver, drop, duplicate.
        for (var i = 0; i < s.Network.Count; i++) {
            var msg = s.Network[i];

            if (s.LinkUp[(int)msg.From, (int)msg.To]) {
                var next = s.Clone();

                next.Network.RemoveAt(index: i);
                Deliver(s: next, msg: msg);
                yield return ($"deliver {msg.Canon}", next);
            }
            if (adversary && (s.Budget.Faults < faultBudget)) {
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
        foreach (var (name, next) in ProgressActions(s: s)) {
            yield return (name, next);
        }
        if (!adversary || (s.Budget.Faults >= faultBudget)) {
            yield break;
        }
        foreach (var (name, next) in FaultActions(s: s)) {
            yield return (name, next);
        }
    }
    /// <summary>The protocol's own moves: the player crossing, sweeps, retries, resolution, grace and shedding, and
    /// home's recall.</summary>
    public static IEnumerable<(string Name, ModelState Next)> ProgressActions(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(node: s.Nodes[i]);
        }
        // The player crosses along the itinerary, its following companion in the same cohort.
        if ((s.PlayerAt is { } at) && (s.Itinerary < s.Route.Length) && (s.Route[s.Itinerary] != at) && views[(int)at].PlayerBody && !s.At(node: at).Outbound.Values.Any(static o => o.Members.Any(static m => m.Kind == MemberKind.Player)) && !views[(int)at].InDoubt.Any(static d => d.Members.Any(static m => m.Kind == MemberKind.Player))) {
            var next = s.Clone();
            var node = next.At(node: at);
            var view = views[(int)at];
            var key = Mint(s: next, at: at);
            var to = s.Route[s.Itinerary];
            // The companion is listed before its player: binding is by member id, never by position.
            var members = (view.CompanionFollows
                ? new[] { Companion(epoch: view.CompanionEpoch, hop: view.CompanionHop, follows: Member.PlayerId), Player() }
                : [Player()]);

            next.Itinerary++;
            node.Outbound[key] = new Outbound(To: to, Members: members, Phase: "Reserving");
            Send(s: next, from: at, to: to, kind: MsgKind.Reserve, key: key, members: members);
            yield return ($"cross {at}>{to}", next);
        }
        for (var i = 1; i < 3; i++) {
            var node = s.Nodes[i];
            var view = views[i];
            var separated = view.CompanionBody && !view.CompanionFollows && !node.Outbound.Values.Any(static o => o.Members.Any(static m => m.Kind == MemberKind.Companion)) && !view.InDoubt.Any(static d => d.Members.Any(static m => m.Kind == MemberKind.Companion));

            // Separation sweep: the companion asks to go home.
            if (separated && (node.Sweeps < SweepLimit)) {
                var next = s.Clone();
                var nextNode = next.Nodes[i];
                var key = Mint(s: next, at: node.Id);
                var members = new[] { Companion(epoch: view.CompanionEpoch, hop: view.CompanionHop, follows: null) };

                nextNode.Sweeps++;
                if (!view.Stranded) {
                    Record(s: next, at: node.Id, rec: new Stranded());
                }
                nextNode.Outbound[key] = new Outbound(To: Node.H, Members: members, Phase: "Reserving");
                Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: members);
                yield return ($"sweep {node.Id}", next);
            }
            // Shed: the grace window passes with the companion still here, and the stranded custodian detaches it into
            // a durable return record and frees its slot. A window passes only when something has gone wrong (a loss,
            // a partition, a crash); an answered return never waits that long.
            if (separated && view.Stranded && (s.Budget.Lossy || s.Converging)) {
                var next = s.Clone();
                var key = Mint(s: next, at: node.Id);
                var members = new[] { Companion(epoch: view.CompanionEpoch, hop: view.CompanionHop, follows: null) };

                if (!view.GraceElapsed) {
                    Record(s: next, at: node.Id, rec: new GraceElapsed());
                }
                Record(s: next, at: node.Id, rec: new Dep(Key: key, To: Node.H, Members: members, Shed: true));
                Record(s: next, at: node.Id, rec: new Attempt(Key: key, Image: key));
                next.Nodes[i].Outbound[key] = new Outbound(To: Node.H, Members: members, Phase: "ShedReserving");
                Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: members);
                yield return ($"shed {node.Id}", next);
            }
        }
        // Retries recover from faults: before any loss every request is answered, so none is enabled, which keeps the
        // fault-free search to the protocol's own interleavings; and a retry is a timeout, so it fires with nothing in
        // flight (a retry racing its own late reply is the duplication fault). Convergence runs them regardless.
        var recovering = (s.Budget.Lossy && (s.Network.Count == 0)) || s.Converging;

        for (var i = 0; (i < 3) && recovering; i++) {
            var node = s.Nodes[i];
            var view = views[i];

            // Retry: re-send the step a departure or attempt waits on (idempotent at the receiver). A shed image's
            // latest attempt key is what the destination knows it by.
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
                } else if (!InFlight(s: s, from: node.Id, key: key, kind: MsgKind.Status) && !InFlight(s: s, from: node.Id, key: key, kind: MsgKind.Commit)) {
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
                var kind = ((outbound.Phase == "AwaitHop") ? MsgKind.HopRequest : MsgKind.Reserve);

                if (InFlight(s: s, from: node.Id, key: key, kind: kind)) {
                    continue;
                }

                var next = s.Clone();

                next.Nodes[i].Retries[key] = node.Retries.GetValueOrDefault(key: key) + 1;
                if (kind == MsgKind.HopRequest) {
                    var crossing = CompanionOf(members: outbound.Members)!;

                    Send(s: next, from: node.Id, to: Node.H, kind: kind, key: key, members: [crossing], arg: $"{node.Id},{outbound.To},{crossing.Epoch}");
                } else {
                    Send(s: next, from: node.Id, to: outbound.To, kind: kind, key: key, members: outbound.Members);
                }
                yield return ($"retry {kind} {node.Id}:{key}", next);
            }
            // The settlement notice is a durable obligation toward home until acknowledged.
            foreach (var settle in view.UnackedSettles) {
                if (InFlight(s: s, from: node.Id, key: settle.Key, kind: MsgKind.HopSettled) || (node.Retries.GetValueOrDefault(key: "settle:" + settle.Key) >= RetryLimit)) {
                    continue;
                }
                var next = s.Clone();

                next.Nodes[i].Retries["settle:" + settle.Key] = node.Retries.GetValueOrDefault(key: "settle:" + settle.Key) + 1;
                Send(s: next, from: node.Id, to: Node.H, kind: MsgKind.HopSettled, key: settle.Key, members: [], arg: (settle.Arrived ? "1" : "0"));
                yield return ($"retry settle {node.Id}:{settle.Key}", next);
            }
        }
        // Home resolves a pending hop by asking the hop's destination.
        foreach (var hop in views[0].PendingHops) {
            if (!recovering || (hop.From == Node.H) || InFlight(s: s, from: Node.H, key: hop.Key, kind: MsgKind.Status) || (s.Nodes[0].Retries.GetValueOrDefault(key: "resolve:" + hop.Key) >= RetryLimit)) {
                continue;
            }
            var next = s.Clone();

            next.Nodes[0].Retries["resolve:" + hop.Key] = s.Nodes[0].Retries.GetValueOrDefault(key: "resolve:" + hop.Key) + 1;
            Send(s: next, from: Node.H, to: hop.To, kind: MsgKind.Status, key: hop.Key, members: []);
            yield return ($"resolve {hop.Key}", next);
        }
    }
    /// <summary>Faults and owner acts: loss and duplication live in Successors; these are crashes, partitions, lease
    /// expiry, the author's install, the player's recall, disconnect and reconnect, a hop deadline, and a stale
    /// custodian forging a return.</summary>
    public static IEnumerable<(string Name, ModelState Next)> FaultActions(ModelState s) {
        var homeView = Fold(node: s.Nodes[0]);
        var allowed = s.Budget;

        if (allowed.Allows(fault: Fault.Crash) && (s.Budget.Crashes < 1)) {
            for (var i = 0; i < 3; i++) {
                if ((s.Nodes[i].Leases.Count == 0) && (s.Nodes[i].Outbound.Count == 0) && (s.Nodes[i].Retries.Count == 0)) {
                    continue;
                }
                var next = s.Clone();

                next.Nodes[i].Leases.Clear();
                next.Nodes[i].Outbound.Clear();
                next.Nodes[i].Retries.Clear();
                next.Budget.Crashes++;
                next.Budget.Faults++;
                yield return ($"crash {s.Nodes[i].Id}", next);
            }
        }
        if (allowed.Allows(fault: Fault.Partition) && (s.Budget.Partitions < 2)) {
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
        if (allowed.Allows(fault: Fault.Expiry) && (s.Budget.LeaseExpiries < 1)) {
            for (var i = 0; i < 3; i++) {
                foreach (var key in s.Nodes[i].Leases.Keys) {
                    var next = s.Clone();

                    _ = next.Nodes[i].Leases.Remove(key: key);
                    next.Budget.LeaseExpiries++;
                    next.Budget.Faults++;
                    yield return ($"expire {s.Nodes[i].Id}:{key}", next);
                }
            }
        }
        if (allowed.Allows(fault: Fault.Install) && (s.Budget.Installs < 1) && (homeView.Custody != CustodyKind.Released)) {
            var next = s.Clone();

            Record(s: next, at: Node.H, rec: new Install());
            if (homeView.Custody != CustodyKind.Home) {
                Record(s: next, at: Node.H, rec: new Notice(Text: "placement removed; companion released"));
            }
            next.Budget.Installs++;
            next.Budget.Faults++;
            yield return ("install", next);
        }
        // Recall: only home, only for a title away or pending, never while a return lease is live, and only while the
        // owner is reachable (never on silence while the owner may share the companion's partition) and not standing
        // where the title is held (a companion beside its owner is not stranded).
        var heldAt = ((homeView.Custody == CustodyKind.Pending) && (homeView.PendingHops.FirstOrDefault(h => h.Key == homeView.PendingHopKey) is { } pendingHop))
            ? new[] { pendingHop.From, pendingHop.To }
            : [homeView.Custodian];

        if (allowed.Allows(fault: Fault.Recall) && (s.Budget.Recalls < 1) && (homeView.Custody is CustodyKind.Away or CustodyKind.Pending) && (s.PlayerAt is { } playerAt) && !heldAt.Contains(value: playerAt) && Reachable(s: s, a: Node.H, b: playerAt) && !s.Nodes[0].Leases.Values.Any(static l => l.Members.Any(static m => m.Kind == MemberKind.Companion))) {
            var next = s.Clone();

            Record(s: next, at: Node.H, rec: new Recall(NewEpoch: homeView.MaxEpoch + 1));
            Record(s: next, at: Node.H, rec: new Notice(Text: "companion recalled; what it carried since it left is lost"));
            next.Budget.Recalls++;
            next.Budget.Faults++;
            yield return ("recall", next);
        }
        if (allowed.Allows(fault: Fault.Disconnect) && (s.Budget.Disconnects < 1) && (s.PlayerAt is { } leaving) && Fold(node: s.At(node: leaving)).PlayerBody) {
            var next = s.Clone();

            Record(s: next, at: leaving, rec: new PlayerLeft());
            next.PlayerAt = null;
            next.Budget.Disconnects++;
            next.Budget.Faults++;
            yield return ($"disconnect at {leaving}", next);
        }
        if ((s.Budget.Reconnects < 1) && (s.Budget.Disconnects > 0) && (s.PlayerAt is null)) {
            var next = s.Clone();

            Record(s: next, at: Node.H, rec: new PlayerJoined());
            next.PlayerAt = Node.H;
            next.Budget.Reconnects++;
            next.Budget.Faults++;
            yield return ("reconnect", next);
        }
        // Hop deadline: the source stops waiting for home and crosses the player alone.
        if (allowed.Allows(fault: Fault.Timeout) && (s.Budget.HopTimeouts < 1)) {
            for (var i = 1; i < 3; i++) {
                foreach (var (key, outbound) in s.Nodes[i].Outbound) {
                    if (outbound.Phase != "AwaitHop") {
                        continue;
                    }
                    var next = s.Clone();
                    var node = next.Nodes[i];

                    Record(s: next, at: node.Id, rec: new Notice(Text: $"companion cannot follow to {outbound.To}: home did not answer"));
                    _ = node.Outbound.Remove(key: key);
                    Record(s: next, at: node.Id, rec: new Dep(Key: key, To: outbound.To, Members: outbound.Members.Where(static m => m.Kind == MemberKind.Player).ToArray(), Shed: false));
                    Send(s: next, from: node.Id, to: outbound.To, kind: MsgKind.Commit, key: key, members: outbound.Members.Where(static m => m.Kind == MemberKind.Player).ToArray());
                    next.Budget.HopTimeouts++;
                    next.Budget.Faults++;
                    yield return ($"hop timeout {node.Id}:{key}", next);
                }
            }
        }
        // A stale custodian forges a return with a ticket it once held.
        if (allowed.Allows(fault: Fault.Forge) && (s.Budget.Forged < 1)) {
            for (var i = 1; i < 3; i++) {
                var ticket = s.Nodes[i].Records.OfType<Arr>().Select(a => CompanionOf(members: a.Members)).LastOrDefault(static c => c is not null);

                if (ticket is null) {
                    continue;
                }
                var next = s.Clone();
                var key = $"{s.Nodes[i].Id}forged";
                var members = new[] { Companion(epoch: ticket.Epoch, hop: ticket.Hop, follows: null) };

                next.Nodes[i].Outbound[key] = new Outbound(To: Node.H, Members: members, Phase: "Reserving");
                Send(s: next, from: s.Nodes[i].Id, to: Node.H, kind: MsgKind.Reserve, key: key, members: members);
                next.Budget.Forged++;
                next.Budget.Faults++;
                yield return ($"forge {s.Nodes[i].Id}", next);
            }
        }
    }

    // ---- invariants -----------------------------------------------------------------------------------------------

    /// <summary>The decision's invariants on one state; throws <see cref="ModelViolation"/> naming the first broken.</summary>
    public static void CheckInvariants(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(node: s.Nodes[i]);
        }
        var home = views[0];
        // At most one committed embodiment of the incarnation: a copy at an epoch below the title's is stale, and
        // retires when it next speaks to home.
        var embodied = views.Count(v => v.CompanionBody && (v.CompanionEpoch >= home.Epoch));

        if (embodied > 1) {
            throw new ModelViolation(message: $"companion embodied at {embodied} authorities at the current epoch");
        }
        // Home embodies it only when its title says home; the held slot is free while the title is away or pending.
        if (home.CompanionBody && (home.Custody != CustodyKind.Home)) {
            throw new ModelViolation(message: $"home embodies the companion while custody is {home.Custody}");
        }
        if (!home.CompanionBody && (home.Custody == CustodyKind.Home) && !home.InDoubt.Any(static d => d.Members.Any(static m => m.Kind == MemberKind.Companion))) {
            throw new ModelViolation(message: "custody says home while home holds neither body nor in-doubt departure");
        }
        // No lost title: an embodied copy abroad at the current epoch is the one the title names, or the title's own
        // pending hop names it.
        for (var i = 1; i < 3; i++) {
            var view = views[i];

            if (!view.CompanionBody) {
                continue;
            }
            switch (home.Custody) {
                case CustodyKind.Away when (home.Custodian == (Node)i) && (home.Epoch == view.CompanionEpoch):
                case CustodyKind.Pending when home.PendingHops.Any(h => (h.Key == home.PendingHopKey) && (((h.From == (Node)i) && (h.Epoch == view.CompanionEpoch)) || ((h.To == (Node)i) && (h.Next == view.CompanionEpoch)))):
                case CustodyKind.Released:
                    break;
                case CustodyKind.Home when home.Epoch > view.CompanionEpoch:
                case CustodyKind.Away when home.Epoch > view.CompanionEpoch:
                case CustodyKind.Pending when home.Epoch >= view.CompanionEpoch:
                    // A stale copy awaiting retirement at its custodian.
                    break;
                default:
                    throw new ModelViolation(message: $"{(Node)i} embodies the companion at epoch {view.CompanionEpoch} while home says {home.Custody} {home.Custodian}@{home.Epoch}");
            }
        }
        // No silent re-issue: a recall or release is told to the player.
        if (home.Recalled && !s.Nodes[0].Records.OfType<Notice>().Any(static n => n.Text.StartsWith("companion recalled", StringComparison.Ordinal))) {
            throw new ModelViolation(message: "recall without a notice");
        }
        // Observation: a companion follows only the player standing beside it.
        for (var i = 0; i < 3; i++) {
            if (views[i].CompanionFollows && !views[i].PlayerBody) {
                throw new ModelViolation(message: $"{(Node)i}: companion follows an absent player");
            }
        }
        // Replay equals live: what a crash leaves (records alone) folds to the same custody facts.
        for (var i = 0; i < 3; i++) {
            var bare = new NodeState(id: s.Nodes[i].Id, acceptsRole: s.Nodes[i].AcceptsRole) { Records = s.Nodes[i].Records };
            var replayed = Fold(node: bare);

            if ((replayed.CompanionBody != views[i].CompanionBody) || (replayed.Custody != views[i].Custody) || (replayed.Epoch != views[i].Epoch) || (replayed.Custodian != views[i].Custodian) || (replayed.InDoubt.Count != views[i].InDoubt.Count)) {
                throw new ModelViolation(message: $"{(Node)i}: replay of the records differs from the live view");
            }
        }
    }
    /// <summary>Fair completion: heals every link, delivers every message and runs every progress move with no further
    /// faults, then requires the title and the body to agree and every obligation to be settled.</summary>
    public static ModelState Converge(ModelState s, int maxSteps = 600) {
        var state = s.Clone();

        state.Converging = true;
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                state.LinkUp[i, j] = true;
            }
        }
        for (var i = 0; i < 3; i++) {
            state.Nodes[i].Retries.Clear();
            state.Nodes[i].Sweeps = 0;
        }
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var step = 0; step < maxSteps; step++) {
            CheckInvariants(s: state);
            // A state seen before means the protocol cycles without a new record: only a passing deadline may break it.
            if (!seen.Add(item: state.Canon())) {
                var expired = false;

                for (var i = 0; i < 3; i++) {
                    if (state.Nodes[i].Leases.Count > 0) {
                        state.Nodes[i].Leases.Clear();
                        expired = true;
                    }
                }
                if (!expired) {
                    throw new ModelViolation(message: "convergence cycles without progress");
                }
                seen.Clear();
                continue;
            }
            if (state.Network.Count > 0) {
                var msg = state.Network.OrderBy(static m => m.Canon, StringComparer.Ordinal).First();

                _ = state.Network.Remove(item: msg);
                Deliver(s: state, msg: msg);
                continue;
            }
            // Convergence never re-crosses the player; every other progress move runs.
            var moved = false;

            foreach (var (name, next) in ProgressActions(s: state)) {
                if (name.StartsWith("cross", StringComparison.Ordinal)) {
                    continue;
                }
                state = next;
                moved = true;
                break;
            }
            if (!moved) {
                // Nothing else can move: lease deadlines pass.
                var expired = false;

                for (var i = 0; i < 3; i++) {
                    if (state.Nodes[i].Leases.Count > 0) {
                        state.Nodes[i].Leases.Clear();
                        expired = true;
                    }
                }
                if (!expired) {
                    break;
                }
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
    public static void CheckConverged(ModelState s) {
        var views = new View[3];

        for (var i = 0; i < 3; i++) {
            views[i] = Fold(node: s.Nodes[i]);
            if (views[i].InDoubt.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps an in-doubt departure after convergence: {views[i].InDoubt[0].Canon}");
            }
            if (s.Nodes[i].Outbound.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps an attempt after convergence: {s.Nodes[i].Outbound.First().Key}");
            }
            if (s.Nodes[i].Leases.Count > 0) {
                throw new ModelViolation(message: $"{(Node)i} keeps a lease after convergence");
            }
        }
        var home = views[0];

        if (home.PendingHops.Count > 0) {
            throw new ModelViolation(message: "home keeps a pending hop after convergence");
        }
        var embodied = Enumerable.Range(0, 3).Where(i => views[i].CompanionBody).ToArray();

        if (home.Custody == CustodyKind.Released) {
            // A copy abroad learns of the release on its next contact with home (a hop or a return); until then it
            // follows its player. Any copy that is separated, or at home, is gone.
            foreach (var index in embodied) {
                if ((index == 0) || !views[index].CompanionFollows) {
                    throw new ModelViolation(message: $"released title still embodied at {(Node)index}");
                }
            }
            return;
        }
        if (embodied.Length != 1) {
            throw new ModelViolation(message: $"title {home.Custody} {home.Custodian}@{home.Epoch} but companion embodied at {embodied.Length} authorities (lost title)");
        }
        var at = (Node)embodied[0];

        if ((at == Node.H) ? (home.Custody != CustodyKind.Home) : ((home.Custody != CustodyKind.Away) || (home.Custodian != at) || (home.Epoch != views[(int)at].CompanionEpoch))) {
            throw new ModelViolation(message: $"companion at {at}@{views[(int)at].CompanionEpoch} but title says {home.Custody} {home.Custodian}@{home.Epoch}");
        }
        // Separated from its player, it went home.
        if ((at != Node.H) && !views[(int)at].CompanionFollows) {
            throw new ModelViolation(message: $"companion stays separated at {at}");
        }
    }
}
