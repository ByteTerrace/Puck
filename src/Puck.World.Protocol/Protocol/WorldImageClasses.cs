using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The disclosure classes of one authority's replica streams, and the one door through which a session
/// mirror presents another's image instead of applying its own deliveries.</summary>
/// <remarks>
/// <para>Several sessions observe one authority under the same disclosure (the portals of a world back to the world
/// they stand in): their streams would be composed alike and applied alike, once per mirror. Each class of equal
/// disclosure (<see cref="WorldDisclosureClass"/>) instead has one leader, whose stream feeds its own mirror as before,
/// and followers, whose streams deliver nothing while they follow and whose mirrors present the leader's
/// (<see cref="WorldSessionMirror.Presented"/>). A snapshot is applied once per authority and class.</para>
/// <para><b>Invariant: a mirror never presents an image its own disclosure withholds.</b> Enforced here: a mirror
/// presents another's image only through a binding <see cref="Judge"/> records, which is made only when the
/// follower's class, read from its own view at that delivery, equals the class the leader's stream is composed under,
/// and only when neither stream carries a record addressed to one recipient (input publications and answers). The
/// leader's mirror holds only what its own stream delivered, which its cursor keeps within its current disclosure, so
/// it holds only what the class discloses. Every stream is judged again at every delivery, before it records anything:
/// a leader whose class moved closes its class before its first delivery under the new one, which withdraws every
/// follower in order, and a follower whose class moved stops following before anything else reaches it. Every binding
/// change travels the authority's outbox as one more record, so it lands in order with the deliveries around
/// it.</para>
/// <para>Where it stops: what a follower presents is the leader's image as of its stream's own deliveries, so it
/// carries the leader's stream history (its revisions, epochs and cue queue). A follower that stops following has
/// presented nothing of its own since it began, so its stream starts again from a cut, which every judgement that
/// leaves a follower without an image asks for (<see cref="WorldImageJudgement.Reprime"/>).</para>
/// <para>Judgement runs on the thread that delivers the authority's streams; bindings change only where the outbox
/// replays its records.</para>
/// </remarks>
public sealed class WorldImageClasses {
    private readonly Dictionary<WorldDisclosureClass, WorldImageClass> m_open = [];
    private readonly Action<WorldImageRecord> m_record;

    /// <summary>Initializes a new instance of the <see cref="WorldImageClasses"/> class.</summary>
    /// <param name="route">The authority's outbox routing: handed the action that applies a binding record, it returns
    /// the delegate that applies it in order with the authority's deliveries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is <see langword="null"/>, or returns
    /// <see langword="null"/>.</exception>
    public WorldImageClasses(Func<Action<WorldImageRecord>, Action<WorldImageRecord>?> route) {
        ArgumentNullException.ThrowIfNull(argument: route);
        m_record = (route(arg: static record => record.Apply()) ?? throw new ArgumentNullException(paramName: nameof(route)));
    }

    /// <summary>Admits an observer to the classes: a session mirror built to share
    /// (<see cref="WorldSessionMirror"/>'s <c>shares</c>); any other observer keeps a stream of its own.</summary>
    /// <param name="observer">The stream's observer.</param>
    /// <returns>The observer's membership, or <see langword="null"/> when it never shares.</returns>
    public WorldImageMember? Admit(IClientSink observer) => ((observer is WorldSessionMirror { Shares: true } mirror) ? new WorldImageMember(mirror: mirror) : null);

    /// <summary>Judges a stream for the delivery in hand, before it records anything: whether it leads its class (or
    /// stands alone) and delivers, or follows its class's leader and delivers nothing.</summary>
    /// <param name="member">The stream's membership.</param>
    /// <param name="view">The stream's view, whose class is read now.</param>
    /// <param name="eligible">Whether the stream may share at all: it observes, its cut is not owed, it has not ended and
    /// its last snapshot carried no record addressed to its recipient alone.</param>
    /// <returns>The judgement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> or <paramref name="view"/> is
    /// <see langword="null"/>.</exception>
    public WorldImageJudgement Judge(WorldImageMember member, WorldRecipientView view, bool eligible) {
        ArgumentNullException.ThrowIfNull(argument: member);
        ArgumentNullException.ThrowIfNull(argument: view);

        var key = (eligible ? view.DisclosureClass : null);

        if (member.Leads is { } led) {
            if (led.Key.Equals(other: key)) { return new(Follows: false, Reprime: false); }
            Close(image: led);
        }
        if (member.Follows is { } followed) {
            if (!followed.Closed && followed.Key.Equals(other: key)) { return new(Follows: true, Reprime: false); }
            member.Follows = null;
            if (!followed.Closed) { m_record(obj: WorldImageRecord.Unfollow(mirror: member.Mirror)); }
        }
        if ((key is not null) && m_open.TryGetValue(key: key, value: out var open)) {
            member.Follows = open;
            member.Stale = true;
            m_record(obj: WorldImageRecord.Follow(mirror: member.Mirror, image: open));
            return new(Follows: true, Reprime: false);
        }
        if (key is not null) {
            var image = new WorldImageClass(key: key, leader: member);

            m_open.Add(key: key, value: image);
            member.Leads = image;
        }

        // A member that followed has presented nothing of its own since: its stream starts again from a cut.
        var reprime = member.Stale;

        member.Reprime = reprime;
        member.Stale = false;
        return new(Follows: false, Reprime: reprime);
    }
    /// <summary>Notes a cut the stream recorded: its mirror refills from its own stream after it, and a class it led
    /// closes, unless the cut is the one its judgement asked for.</summary>
    /// <param name="member">The stream's membership.</param>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is <see langword="null"/>.</exception>
    public void Cut(WorldImageMember member) {
        ArgumentNullException.ThrowIfNull(argument: member);

        member.Stale = false;
        if (member.Reprime) {
            member.Reprime = false;
            return;
        }
        if (member.Leads is { } led) { Close(image: led); }
        // A follower's own cut withdraws its mirror, which stops presenting the image there.
        member.Follows = null;
    }
    /// <summary>Takes an ended stream out of its class: a class it led closes, withdrawing its followers.</summary>
    /// <param name="member">The stream's membership.</param>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is <see langword="null"/>.</exception>
    public void Leave(WorldImageMember member) {
        ArgumentNullException.ThrowIfNull(argument: member);

        if (member.Leads is { } led) { Close(image: led); }
        if (member.Follows is { } followed) {
            member.Follows = null;
            if (!followed.Closed) { m_record(obj: WorldImageRecord.Unfollow(mirror: member.Mirror)); }
        }
        member.Stale = false;
        member.Reprime = false;
    }

    private void Close(WorldImageClass image) {
        if (m_open.TryGetValue(key: image.Key, value: out var open) && ReferenceEquals(objA: open, objB: image)) { _ = m_open.Remove(key: image.Key); }
        image.Closed = true;
        image.Leader.Leads = null;
        m_record(obj: WorldImageRecord.Close(image: image));
    }
}

/// <summary>What <see cref="WorldImageClasses.Judge"/> decided for one delivery.</summary>
/// <param name="Follows">Whether the stream follows its class's leader and delivers nothing.</param>
/// <param name="Reprime">Whether the stream's mirror presented another's image until now, so its stream owes it a cut and
/// its whole disclosed document before anything newer.</param>
public readonly record struct WorldImageJudgement(bool Follows, bool Reprime);

/// <summary>One stream's place in its authority's classes, which only <see cref="WorldImageClasses"/> changes.</summary>
public sealed class WorldImageMember {
    internal WorldImageMember(WorldSessionMirror mirror) => Mirror = mirror;

    internal WorldSessionMirror Mirror { get; }
    internal WorldImageClass? Leads { get; set; }
    internal WorldImageClass? Follows { get; set; }
    // Whether the mirror presented another's image since its stream last delivered to it.
    internal bool Stale { get; set; }
    // Whether the judgement asked the stream for the cut it records next.
    internal bool Reprime { get; set; }
}

/// <summary>One class's tenure under one leader: the key it was opened under, read on the judging thread, and the
/// mirrors presenting its leader's image, changed only where the outbox replays.</summary>
internal sealed class WorldImageClass(WorldDisclosureClass key, WorldImageMember leader) {
    private readonly List<WorldSessionMirror> m_followers = [];
    private bool m_withdrawn;

    public WorldDisclosureClass Key { get; } = key;
    public WorldImageMember Leader { get; } = leader;
    // Judging thread: the class takes no follower after it closes.
    public bool Closed { get; set; }

    // Replay side: binds a follower, or withdraws it when the class already closed.
    public void Bind(WorldSessionMirror follower) {
        if (m_withdrawn) {
            follower.Unfollow();
            return;
        }
        m_followers.Add(item: follower);
        follower.Follow(image: this, presented: Leader.Mirror);
    }
    // Replay side: a follower stopped presenting this image.
    public void Drop(WorldSessionMirror follower) => _ = m_followers.Remove(item: follower);
    // Replay side: withdraws every follower.
    public void Withdraw() {
        m_withdrawn = true;
        for (var index = (m_followers.Count - 1); (index >= 0); index--) {
            m_followers[index].Unfollow();
        }
        m_followers.Clear();
    }
}

/// <summary>One binding change, made where the authority's outbox replays its records, in order with its
/// deliveries. Only <see cref="WorldImageClasses"/> makes one.</summary>
public readonly struct WorldImageRecord {
    private enum Kind : byte {
        Follow,
        Unfollow,
        Close,
    }

    private readonly Kind m_kind;
    private readonly WorldSessionMirror? m_mirror;
    private readonly WorldImageClass? m_image;

    private WorldImageRecord(Kind kind, WorldSessionMirror? mirror, WorldImageClass? image) {
        m_kind = kind;
        m_mirror = mirror;
        m_image = image;
    }

    internal static WorldImageRecord Follow(WorldSessionMirror mirror, WorldImageClass image) => new(kind: Kind.Follow, mirror: mirror, image: image);
    internal static WorldImageRecord Unfollow(WorldSessionMirror mirror) => new(kind: Kind.Unfollow, mirror: mirror, image: null);
    internal static WorldImageRecord Close(WorldImageClass image) => new(kind: Kind.Close, mirror: null, image: image);

    internal void Apply() {
        switch (m_kind) {
            case Kind.Follow: m_image!.Bind(follower: m_mirror!); break;
            case Kind.Unfollow: m_mirror!.Unfollow(); break;
            case Kind.Close: m_image!.Withdraw(); break;
        }
    }
}
