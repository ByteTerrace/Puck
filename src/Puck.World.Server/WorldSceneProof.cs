using Puck.Assets;

namespace Puck.World.Server;

/// <summary>The authority's proof about the scene inputs one session's stream is handed: their content identity
/// (<see cref="WorldRecipientView.SceneIdentity"/>) and whether they equal what a whole reader of the world is handed, over
/// a range of the authority's deliveries. The authority issues it as it composes the session's delivery, from the
/// filtered result and the whole input it holds there, and closes it at the first delivery whose identity differs, or
/// when the stream stops observing. Only the authority constructs one, and it never leaves the authority's process: a
/// viewer in another process holds none, so it keeps a residency of its own.</summary>
/// <remarks>A viewer may draw a session's picture from a scene another stream feeds only on a frame whose latched
/// scene was composed from a delivery the proof covers (<see cref="Covers"/>), checked for the frame bound.</remarks>
public sealed class WorldSceneProof {
    private readonly WorldServer m_server;

    internal WorldSceneProof(WorldServer server, ContentPin scene, bool sharesAuthority, bool sharesContent, ulong first, ulong contentFirst) {
        m_server = server;
        Scene = scene;
        SharesAuthority = sharesAuthority;
        SharesContent = (sharesAuthority || sharesContent);
        ContentFirst = contentFirst;
        First = first;
    }

    /// <summary>Gets the content identity of the scene inputs the stream is handed over the proof's range. Two proofs
    /// of one world whose identities are equal and whose ranges both cover a delivery prove that the two streams were
    /// handed the same scene inputs at it.</summary>
    public ContentPin Scene { get; }
    /// <summary>Gets whether the stream is handed what a whole reader of the world is handed: its scene inputs equal an
    /// operator's, and its world's observer disclosure redacts no body. A viewer may then draw the session from the
    /// world's own residency.</summary>
    public bool SharesAuthority { get; }
    /// <summary>Gets whether the stream is handed what a whole reader is handed except, at most, the placements its
    /// disclosure varies (<see cref="WorldPlacementAudiences.Varies"/>): it reads whole every row a scene reads by name
    /// (<see cref="WorldRecipientView.ReadsSceneInputsWhole"/>) and its world's observer disclosure redacts no body. A
    /// viewer may then draw the session from the world's own residency with the varying placements drawn from the
    /// session's own delivery for its audience alone. Implied by <see cref="SharesAuthority"/>.</summary>
    public bool SharesContent { get; }
    /// <summary>Gets the authority tick of the first delivery of the unbroken run of proofs, this one last, that each
    /// held <see cref="SharesContent"/>: the varying placements change the scene identity at every deal, while what the
    /// stream shares with a whole reader holds across the run.</summary>
    public ulong ContentFirst { get; }
    /// <summary>Gets the authority tick of the first delivery the proof covers.</summary>
    public ulong First { get; }
    /// <summary>Gets the authority tick of the first delivery past the proof's range, or <see langword="null"/> while
    /// it is the stream's current proof.</summary>
    public ulong? End { get; private set; }

    /// <summary>Returns whether the proof covers the delivery of an authority tick.</summary>
    /// <param name="tick">The authority tick of the delivery a viewer's latched scene was composed from.</param>
    /// <returns><see langword="true"/> when <paramref name="tick"/> lies in the proof's range.</returns>
    public bool Covers(ulong tick) => ((tick >= First) && ((End is not { } end) || (tick < end)));
    /// <summary>Returns whether the run of content-sharing proofs this one ends covers the delivery of an authority tick
    /// (<see cref="ContentFirst"/>), so what the stream shares with a whole reader at that delivery is a whole reader's.</summary>
    /// <param name="tick">The authority tick of the delivery a viewer's latched scene was composed from.</param>
    /// <returns><see langword="true"/> when the proof shares content and <paramref name="tick"/> lies in its run.</returns>
    public bool CoversContent(ulong tick) => (SharesContent && (tick >= ContentFirst) && ((End is not { } end) || (tick < end)));
    /// <summary>Returns whether the proof was issued by a world.</summary>
    /// <param name="server">The world's authority.</param>
    /// <returns><see langword="true"/> when <paramref name="server"/> issued the proof.</returns>
    public bool IssuedBy(WorldServer server) => ReferenceEquals(objA: m_server, objB: server);

    // Ends the range before the delivery whose identity differs, or at the moment the stream stops observing.
    internal void Close(ulong end) => End ??= end;
}
