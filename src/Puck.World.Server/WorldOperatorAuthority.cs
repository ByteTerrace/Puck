using Puck.Commands;

namespace Puck.World.Server;

/// <summary>The authority acquired by a registry-admitted operator invocation.
/// An invocation's capability checks its live grant again whenever it acquires authority data.</summary>
public sealed class WorldOperatorAuthority {
    private readonly WorldServer m_server;
    private readonly OperatorContext m_invocation;

    internal WorldOperatorAuthority(WorldServer server, OperatorContext invocation) {
        m_server = server;
        m_invocation = invocation;
    }

    private WorldServer Current {
        get {
            if (!m_server.Grants.IsOperator(m_invocation.Invocation.Principal)) {
                throw new InvalidOperationException("The invocation no longer operates this world.");
            }
            return m_server;
        }
    }

    /// <summary>Reads a state hash and its tick at one settled authority boundary.</summary>
    public (ulong Tick, ulong Hash) CaptureStateHash(WorldStateHashScope scope) {
        lock (m_server.AuthorityGate) {
            var server = Current;
            var tick = server.NextInputTick - 1UL;
            return (tick, WorldStateHashComposition.Hash(scope: scope, server: server, tick: tick));
        }
    }
    /// <summary>Reads the authoritative component digests and their tick at one settled boundary.</summary>
    public (ulong Tick, ulong[] Digests) CaptureStateComponents() {
        lock (m_server.AuthorityGate) {
            var server = Current;
            var tick = server.NextInputTick - 1UL;
            return (tick, WorldStateHashComposition.AuthoritativeComponents(server: server, tick: tick));
        }
    }
}
