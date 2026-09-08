using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The <see cref="IWorldAddonHost"/> the silo attaches to every server it runs (live) and carries into its
/// own offline re-drive seam (the replay tape) — the silo genuinely cannot mount an addon guest, so this host
/// enforces that by refusing rather than by omission: a candidate naming an enabled addon row is refused BY NAME,
/// never silently accepted with no effect.</summary>
internal sealed class WorldNoAddonHost : IWorldAddonHost {
    public bool AnyEverPumped => false;
    public int MountedCount => 0;
    public IReadOnlyList<WorldAddonReceipt> Receipts { get; } = [];

    public void ApplyContributions(ulong tick) { }
    public void Commit(IWorldAddonPreparedPlan plan) { }
    public void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied) { }
    public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) => null;
    public void Dispose() { }
    public void Finish(IWorldAddonPreparedPlan plan) { }
    public void ResolveReads(ulong tick) { }
    public void TickAddons(ulong tick) { }
    // A candidate naming an enabled addon row refuses by name: this host mounts nothing, so an enabled row here can
    // never become true. A candidate whose addon rows are all disabled (or absent) vacuously succeeds with a null
    // plan — there is nothing to prepare and nothing to refuse.
    public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason) {
        foreach (var row in candidate.Addons) {
            if (row.Enabled) {
                plan = null;
                reason = $"'{row.Name}' cannot mount — this server has no addon host attached (the silo does not run addon guests)";

                return false;
            }
        }

        plan = null;
        reason = null;

        return true;
    }
}
