using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Fact]
    public void ReplayResetChecksBothSidesOfQueuedAndInDoubtTransfers() {
        using var host = Host();
        using var a = HostRow.Build("a", Document());
        using var b = HostRow.Build("b", Document());
        using var unrelated = HostRow.Build("unrelated", Document());
        host.Admit(a.Instance); host.Admit(b.Instance); host.Admit(unrelated.Instance);
        Join(a.Server, 0);
        Assert.Null(host.TimelineResetRefusal(a.Server));
        Assert.Null(host.TimelineResetRefusal(b.Server));
        host.SetPeerCallFault("b", new LostAnswer(b.Server, false));
        host.EnqueueTransfer("a", WorldInstanceHost.TransferScope.Body, 0,
            WorldInstanceHost.TransferDestination.Existing("b"), WorldPrincipal.Console);
        Assert.Contains("queued", host.TimelineResetRefusal(a.Server));
        Assert.Contains("queued", host.TimelineResetRefusal(b.Server));
        Assert.Null(host.TimelineResetRefusal(unrelated.Server));
        host.DrainPendingTransfers();
        Assert.Single(Capture(host, a).HostRow.InDoubtTransfers);
        Assert.Contains("in doubt", host.TimelineResetRefusal(a.Server));
        Assert.Contains("in doubt", host.TimelineResetRefusal(b.Server));
        Assert.Null(host.TimelineResetRefusal(unrelated.Server));
    }
}
