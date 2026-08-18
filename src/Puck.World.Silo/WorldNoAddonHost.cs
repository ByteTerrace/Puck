using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The <see cref="IWorldAddonHost"/> a row's replay tape carries for its own offline re-drive seam — inert,
/// since the silo mounts no addon guests (the checkpoint arm gate already refuses a row that ever pumped one).</summary>
internal sealed class WorldNoAddonHost : IWorldAddonHost {
    public bool AnyEverPumped => false;
    public int MountedCount => 0;
    public IReadOnlyList<WorldAddonReceipt> Receipts { get; } = [];

    public void ApplyContributions(ulong tick) { }
    public void CompleteMutation(int addonIndex, ushort actOrdinal, bool applied) { }
    public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) => null;
    public void Dispose() { }
    public string Mount(string name, string modulePath, string hash, ulong fuel, IReadOnlyList<WorldCapabilityRequest>? requests) => "'this host mounts no addons' refused";
    public void ResolveReads(ulong tick) { }
    public void TickAddons(ulong tick) { }
    public string Unmount(string name) => "'this host mounts no addons' refused";
}
