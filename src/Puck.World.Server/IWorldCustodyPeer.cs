using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Home's answer to a custodian's request to move one companion onward.</summary>
public enum WorldHopVerdict : byte {
    /// <summary>Home recorded the hop; the companion lands at <see cref="WorldHopAnswer.Next"/>.</summary>
    Granted = 0,

    /// <summary>The title's own hop is still pending; the requester asks again.</summary>
    Pending = 1,

    /// <summary>The requester is not the title's custodian at the epoch it names: its copy is stale.</summary>
    Stale = 2,

    /// <summary>The title was released.</summary>
    Released = 3,

    /// <summary>The authority asked did not mint the incarnation.</summary>
    NotMyTitle = 4,

    /// <summary>Home could not decide now (its record is not durable, or it could not be reached); the requester asks
    /// again.</summary>
    Unavailable = 5,

    /// <summary>Home already holds this hop's settlement: the key is spent for the companion.</summary>
    Dead = 6,
}
/// <summary>What a hop's destination holds for one companion under the hop's key, answered to the companion's title
/// authority from durable records alone.</summary>
public enum WorldCustodyAnswer : byte {
    /// <summary>The arrival under the key landed the companion here.</summary>
    Landed = 0,

    /// <summary>The arrival under the key landed without the companion.</summary>
    NotLanded = 1,

    /// <summary>The key is dead here: nothing landed under it and nothing ever will.</summary>
    Dead = 2,

    /// <summary>A live lease holds the key; ask again later.</summary>
    Reserved = 3,

    /// <summary>This authority cannot yet call its answer durable; ask again later.</summary>
    Uncertain = 4,

    /// <summary>The asker is not the authority that minted the companion's incarnation.</summary>
    Unauthorized = 5,
}
/// <summary>Which decision a destination's hop confirmation question belongs to. An answer counts only for the question
/// of its own phase, so a reservation's answer can never stand for the commit's.</summary>
public enum WorldHopPhase : byte {
    /// <summary>The destination is deciding the reservation.</summary>
    Reservation = 0,

    /// <summary>The destination is deciding the commit.</summary>
    Commit = 1,
}
/// <summary>One companion a custodian asks home to move onward under a key.</summary>
/// <param name="Incarnation">The companion.</param>
/// <param name="Epoch">The custody epoch the custodian holds it at.</param>
public readonly record struct WorldHopRequestMember(WorldEntityAddress Incarnation, ulong Epoch);
/// <summary>Home's answer for one companion of a hop request.</summary>
/// <param name="Incarnation">The companion.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="Next">The epoch the granted hop lands it at, or zero.</param>
public readonly record struct WorldHopAnswer(WorldEntityAddress Incarnation, WorldHopVerdict Verdict, ulong Next);
/// <summary>The custody door between authorities: as a companion's title authority it confirms that a hop is currently
/// granted, grants a custodian's onward hop and settles a custodian's hop; as a hop's destination it answers the title
/// authority what it holds under the hop's key. The caller states nothing it is trusted for: the asking authority is
/// the one the transport authenticated (in process, the host; remotely, the federation lane's authenticated
/// namespace), and every answer comes from the answering authority's durable records.</summary>
public interface IWorldCustodyPeer {
    /// <summary>Answers, for each companion, whether home's grant of its hop under <paramref name="key"/> to
    /// <paramref name="destination"/> is current now, and the epoch it lands at.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnations">The companions.</param>
    /// <param name="destination">The authenticated asking destination.</param>
    /// <param name="phase">The decision the question belongs to.</param>
    /// <returns>One entry per companion: the landing epoch of a current grant, or <see langword="null"/>; or
    /// <see langword="null"/> when home could not be reached.</returns>
    IReadOnlyList<ulong?>? ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination, WorldHopPhase phase);
    /// <summary>Asks home to authorize the onward hop of each companion the authenticated custodian
    /// <paramref name="source"/> holds, toward <paramref name="destination"/>, under <paramref name="key"/>. Home decides
    /// each on its own title and records one hop naming the ones it grants.</summary>
    /// <param name="key">The crossing's key, minted by <paramref name="source"/>.</param>
    /// <param name="source">The authenticated custodian asking.</param>
    /// <param name="destination">Where the crossing goes.</param>
    /// <param name="members">The companions and the epochs the custodian holds them at.</param>
    /// <returns>One answer per companion, in order; every answer <see cref="WorldHopVerdict.Unavailable"/> when home
    /// could not be reached.</returns>
    IReadOnlyList<WorldHopAnswer> RequestHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members);
    /// <summary>Answers, as a hop's destination, what this authority holds for <paramref name="incarnation"/> under
    /// <paramref name="key"/>, to the authenticated <paramref name="asker"/>: only the authority that minted the
    /// incarnation may ask. A key it neither leased nor landed is tombstoned before the answer, so the answer is final.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="asker">The authenticated asking authority.</param>
    /// <returns>The answer; <see cref="WorldCustodyAnswer.Uncertain"/> when the authority could not be reached.</returns>
    WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker);
    /// <summary>Tells the title authority that the authenticated custodian <paramref name="source"/> settled its hop
    /// of <paramref name="incarnation"/> under <paramref name="key"/>. The notice is a request: home settles the hop
    /// from the hop's destination's own answer, never from the notice. A notice for a hop home never recorded settles
    /// it as not arrived, so the hop is cancelled if it is ever recorded.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="source">The authenticated custodian that settled.</param>
    /// <returns><see langword="true"/> once home recorded the hop's settlement and acknowledges the notice.</returns>
    bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source);
}
