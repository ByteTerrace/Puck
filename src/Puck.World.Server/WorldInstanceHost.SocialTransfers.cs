using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // A synchronous transfer releases every pre-commit source hold on exit. Only detached members explicitly
    // handed to reconciliation remain frozen; an unreachable commit must never thaw merely because time passed.
    private sealed class SourceSocialHold(WorldServer server, WorldTransferKey key, int capacity) : IDisposable {
        private readonly List<WorldEntityAddress> m_observers = new(capacity);
        private HashSet<WorldEntityAddress>? m_retained;

        public bool TryCapture(WorldMobilityIdentity? mobility, out WorldSocialMemoryCheckpoint? snapshot, out string reason) {
            var result = server.ExecuteAuthorityOperation(() => {
                if (mobility is not { } identity) {
                    return (Accepted: false, Snapshot: (WorldSocialMemoryCheckpoint?)null, Reason: "source traveler has no mobility identity");
                }
                if (server.SocialMemory is not { } bank) {
                    return (Accepted: true, Snapshot: (WorldSocialMemoryCheckpoint?)null, Reason: string.Empty);
                }
                if (!bank.TryFreezeObserver(identity.Incarnation, key, out var refusal)) {
                    return (Accepted: false, Snapshot: (WorldSocialMemoryCheckpoint?)null, Reason: refusal);
                }
                m_observers.Add(identity.Incarnation);
                return (Accepted: true, Snapshot: (WorldSocialMemoryCheckpoint?)bank.CaptureFrozenObserver(identity.Incarnation, key), Reason: string.Empty);
            });
            snapshot = result.Snapshot; reason = result.Reason;
            return result.Accepted;
        }

        public void KeepForResolution(List<LandedMember> members) {
            m_retained = new(members.Count);
            foreach (var member in members) { m_retained.Add(member.Mobility.Incarnation); }
        }

        public void Dispose() => server.ExecuteAuthorityOperation(() => {
            foreach (var observer in m_observers) {
                if (m_retained?.Contains(observer) != true) { server.SocialMemory?.ThawObserver(observer, key); }
            }
        });
    }

    // Remove successful restores from BOTH lists so checkpoint profile ordinals still match. Failed restores
    // keep their memory frozen and their recovery record; no retry may overwrite an occupied source slot.
    private static bool RestoreDetachedMembers(WorldInstance source, WorldTransferKey key,
        List<LandedMember> members, List<WorldTransferCommitMember> commits) {
        for (var index = 0; index < members.Count;) {
            var member = members[index];
            if (!RestoreDetachedMember(source, member)) { index++; continue; }
            source.Server.ExecuteAuthorityOperation(() => source.Server.SocialMemory?.ThawObserver(member.Mobility.Incarnation, key));
            members.RemoveAt(index);
            commits.RemoveAt(index);
        }
        return members.Count == 0;
    }
}
