using Puck.Assets;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>What a destination's authority proved about the scene a session's stream is handed, as a frame reads it.</summary>
/// <param name="Scene">The scene inputs the stream is handed, by content.</param>
/// <param name="SharesAuthority">Whether the stream is handed what a whole reader is handed.</param>
/// <param name="First">The first delivered tick the proof covers.</param>
/// <param name="End">The tick the proof stops covering, exclusive, or <see langword="null"/> while it still covers every
/// later tick.</param>
/// <param name="SharesContent">Whether the stream is handed what a whole reader is handed except, at most, the placements
/// its disclosure varies; implied by <paramref name="SharesAuthority"/>.</param>
public readonly record struct WorldSceneShare(ContentPin Scene, bool SharesAuthority, ulong First, ulong? End, bool SharesContent = false) {
    /// <summary>Returns whether the proof covers a delivered tick.</summary>
    /// <param name="tick">The delivered tick.</param>
    /// <returns><see langword="true"/> when the tick lies in the covered range.</returns>
    public bool Covers(ulong tick) => ((tick >= First) && ((End is not { } end) || (tick < end)));
}

/// <summary>A session's current admission into its destination, as a collect copied it.</summary>
/// <param name="Token">The admission's identity: a re-admission is another token.</param>
/// <param name="Session">The session principal the surface observes as.</param>
/// <param name="Tier">What the admission discloses.</param>
/// <param name="Ended">Whether the destination ended the admission.</param>
/// <param name="CanObserve">Whether the admission discloses frames the surface may render.</param>
/// <param name="Proof">The authority's proof about the scene the stream is handed, or <see langword="null"/> for none.</param>
/// <param name="ProofHolds">Whether <paramref name="Proof"/> was issued by, and the admission observes, the instance that
/// runs under the session's instance name now.</param>
/// <param name="Window">The endpoint a window of the session may render in place of its own stream, while the proof says
/// it is handed what a whole reader is handed, or that much but the placements its disclosure varies (which a window
/// then draws from the session's own delivery, <see cref="WorldSceneShare.SharesAuthority"/> false), and covers the
/// delivery the endpoint's mirror last took; otherwise
/// <see langword="null"/>.</param>
/// <param name="Disclose">Projects a candidate document to what the admission discloses this observer, or
/// <see langword="null"/> when it discloses nothing. Built by the composition root over the authority's admission; the
/// frame never calls it, only a render envelope measurer the authority's capacity check runs.</param>
public sealed record WorldObservationAdmission(object Token, Principal Session, WorldDisclosureTier Tier, bool Ended, bool CanObserve,
    WorldSceneShare? Proof, bool ProofHolds, WorldAuthorityEndpoint? Window, Func<WorldDefinition, WorldDefinition?> Disclose);

/// <summary>One session a world's screen or infinity layer observes its destination through, as a collect copied it.</summary>
/// <param name="Token">The session's identity: a re-pointed surface opens another session, which is another token.</param>
/// <param name="Source">The session source the surface declares.</param>
/// <param name="InstanceName">The destination instance's name, or <see langword="null"/> when it did not resolve.</param>
/// <param name="GenerationId">The destination instance's generation id.</param>
/// <param name="Refusal">Why the surface observes nothing, or <see langword="null"/> while it observes.</param>
/// <param name="Mirror">The mirror the session's observation feeds, the same object for the session's whole life.</param>
/// <param name="Observation">The current admission, or <see langword="null"/> when the destination refused one.</param>
public sealed record WorldObservedSession(object Token, WorldScreenSource.Session Source, string? InstanceName, ulong GenerationId,
    string? Refusal, WorldSessionMirror Mirror, WorldObservationAdmission? Observation);

/// <summary>
/// What a frame reads of the sessions every running world's screens and infinity layers observe, of the instances this
/// process runs, and of the boot world's endpoint, copied at a host step's collect and published whole
/// (<see cref="WorldPresentationFeed.Observations"/>). The screen binder renders sessions from this table and never from
/// the instance host.
/// </summary>
public sealed class WorldObservationTable {
    private readonly IReadOnlyDictionary<(string Owner, int Screen), WorldObservedSession> m_screens;
    private readonly IReadOnlyDictionary<(string Owner, string Layer), WorldObservedSession> m_layers;
    private readonly IReadOnlyDictionary<string, WorldRenderEnvelope> m_running;
    private readonly Dictionary<object, WorldObservedSession> m_byToken = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Initializes a new instance of the <see cref="WorldObservationTable"/> class.</summary>
    /// <param name="screens">Each screen session, by owning instance and screen.</param>
    /// <param name="layers">Each infinity layer session, by owning instance and layer.</param>
    /// <param name="running">Each running instance's render envelope, by instance name.</param>
    /// <param name="bootEndpoint">The boot world's endpoint, or <see langword="null"/> for a host with none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="screens"/>, <paramref name="layers"/> or
    /// <paramref name="running"/> is <see langword="null"/>.</exception>
    public WorldObservationTable(IReadOnlyDictionary<(string Owner, int Screen), WorldObservedSession> screens,
        IReadOnlyDictionary<(string Owner, string Layer), WorldObservedSession> layers, IReadOnlyDictionary<string, WorldRenderEnvelope> running,
        WorldAuthorityEndpoint? bootEndpoint) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: layers);
        ArgumentNullException.ThrowIfNull(argument: running);
        m_screens = screens;
        m_layers = layers;
        m_running = running;
        BootEndpoint = bootEndpoint;
        foreach (var session in screens.Values) {
            m_byToken[session.Token] = session;
        }
        foreach (var session in layers.Values) {
            m_byToken[session.Token] = session;
        }
    }

    /// <summary>Gets the table of a process running no world.</summary>
    public static WorldObservationTable Empty { get; } = new(
        bootEndpoint: null,
        layers: new Dictionary<(string Owner, string Layer), WorldObservedSession>(),
        running: new Dictionary<string, WorldRenderEnvelope>(comparer: StringComparer.Ordinal),
        screens: new Dictionary<(string Owner, int Screen), WorldObservedSession>()
    );

    /// <summary>Gets the boot world's endpoint, or <see langword="null"/> for a host with none.</summary>
    public WorldAuthorityEndpoint? BootEndpoint { get; }
    /// <summary>Gets each screen session, by owning instance and screen.</summary>
    public IReadOnlyDictionary<(string Owner, int Screen), WorldObservedSession> Screens => m_screens;
    /// <summary>Gets each infinity layer session, by owning instance and layer.</summary>
    public IReadOnlyDictionary<(string Owner, string Layer), WorldObservedSession> Layers => m_layers;
    /// <summary>Gets each running instance's render envelope, by instance name.</summary>
    public IReadOnlyDictionary<string, WorldRenderEnvelope> Running => m_running;

    /// <summary>Reads the session a world's screen observes its destination through.</summary>
    /// <param name="owner">The owning instance.</param>
    /// <param name="screen">The screen index.</param>
    /// <returns>The session, or <see langword="null"/> when the screen declares none or its owner holds none.</returns>
    public WorldObservedSession? Screen(string owner, int screen) => m_screens.GetValueOrDefault(key: (owner, screen));
    /// <summary>Reads the session a named infinity layer observes its destination through.</summary>
    /// <param name="owner">The owning instance.</param>
    /// <param name="layer">The layer's name.</param>
    /// <returns>The session, or <see langword="null"/> when the layer declares none or its owner holds none.</returns>
    public WorldObservedSession? Infinity(string owner, string layer) => m_layers.GetValueOrDefault(key: (owner, layer));
    /// <summary>Reads a session's current record by its identity, wherever the authority holds it.</summary>
    /// <param name="token">The session's <see cref="WorldObservedSession.Token"/>.</param>
    /// <returns>The record, or <see langword="null"/> when the authority no longer holds the session.</returns>
    public WorldObservedSession? Current(object token) => m_byToken.GetValueOrDefault(key: token);
    /// <summary>Returns whether an instance runs in this process under a name.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when one runs under it.</returns>
    public bool Runs(string instance) => m_running.ContainsKey(key: instance);
    /// <summary>Reads the render envelope of an instance this process runs, which a view showing it registers its
    /// measurer with.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The envelope, or <see langword="null"/> when no instance runs under the name.</returns>
    public WorldRenderEnvelope? EnvelopeOf(string instance) => m_running.GetValueOrDefault(key: instance);
}
