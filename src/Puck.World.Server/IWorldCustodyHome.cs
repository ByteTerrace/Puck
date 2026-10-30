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
/// <summary>One companion a custodian asks home to move onward under a key.</summary>
/// <param name="Incarnation">The companion.</param>
/// <param name="Epoch">The custody epoch the custodian holds it at.</param>
public readonly record struct WorldHopRequestMember(WorldEntityAddress Incarnation, ulong Epoch);
/// <summary>Home's answer for one companion of a hop request.</summary>
/// <param name="Incarnation">The companion.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="Next">The epoch the granted hop lands it at, or zero.</param>
public readonly record struct WorldHopAnswer(WorldEntityAddress Incarnation, WorldHopVerdict Verdict, ulong Next);
/// <summary>The title authority as a destination or a custodian reaches it: the one door that confirms a hop is
/// currently granted, and the one that grants a custodian's onward hop. The caller states nothing it is trusted for:
/// the asking authority is the one the transport authenticated (in process, the host; remotely, the federation
/// lane's authenticated namespace), and every answer comes from home's durable records.</summary>
public interface IWorldCustodyHome {
    /// <summary>Answers, for each companion, whether home's grant of its hop under <paramref name="key"/> to
    /// <paramref name="destination"/> is current now, and the epoch it lands at.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnations">The companions.</param>
    /// <param name="destination">The authenticated asking destination.</param>
    /// <returns>One entry per companion: the landing epoch of a current grant, or <see langword="null"/>; or
    /// <see langword="null"/> when home could not be reached.</returns>
    IReadOnlyList<ulong?>? ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination);
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
}
