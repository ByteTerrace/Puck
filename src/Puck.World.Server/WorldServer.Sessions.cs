using Puck.Commands;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>The refusal code a submission carries when its principal names a session this world does not hold live:
    /// one that ended, whose epoch is retired, or one it never admitted.</summary>
    public const string StaleSessionCode = "world.session.stale";

    /// <inheritdoc cref="WorldGrants.EndSession"/>
    public bool EndSession(Principal session, out string refusal) => m_grants.EndSession(
        refusal: out refusal,
        session: session
    );
    /// <inheritdoc cref="WorldGrants.IsLiveSession"/>
    public bool IsLiveSession(Principal principal) => m_grants.IsLiveSession(principal: principal);
    /// <inheritdoc cref="WorldGrants.TryAdmitSession"/>
    public bool TryAdmitSession(string sourceAuthority, out Principal session, out string refusal) => m_grants.TryAdmitSession(
        refusal: out refusal,
        session: out session,
        sourceAuthority: sourceAuthority
    );
    /// <inheritdoc cref="WorldGrants.TryEmbodySession"/>
    public bool TryEmbodySession(Principal session, int bodyIndex, out string refusal) => m_grants.TryEmbodySession(
        bodyIndex: bodyIndex,
        refusal: out refusal,
        session: session
    );
}
