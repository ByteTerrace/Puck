using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

public sealed partial class GpuWorkLedgerLawTests {
    [Fact]
    public void SubmittedDetailsKeepTheirOwnerAndNameAcrossMutationAndReplacement() {
        var rig = new Rig(framesInFlight: 3);
        rig.Ledger.Configure(passLabels: ["sky", "shadow"], revision: 1);
        GpuWorkDetail[] names = [new(0, "stars"), new(1, "sun")];
        rig.Ledger.ConfigureDetails(names);
        rig.Ledger.EnterPass(0); rig.Ledger.LeavePass();
        rig.Ledger.StandPass(1);
        var first = rig.NewFence();
        rig.Submit(first);
        names[0] = new(0, "clouds");
        rig.Ledger.ConfigureDetails(names);
        first.Counted.Wait();
        var sample = rig.Read()!;
        Assert.Equal(2, sample.PassCount);
        Assert.Equal(new GpuWorkDetail(0, "stars"), sample.Details[0]);
        Assert.Equal(new GpuWorkDetail(1, "sun"), sample.Details[1]);
        Assert.True(sample.TryGetDetailCount(0, DispatchColumn, out var zero));
        Assert.Equal(0, zero);
        Assert.False(sample.TryGetDetailCount(1, DispatchColumn, out _));
        rig.Ledger.EnterPass(0); rig.Ledger.LeavePass();
        rig.Ledger.SkipPass(1);
        var second = rig.NewFence();
        rig.Submit(second); second.Counted.Wait();
        Assert.Equal("clouds", rig.Read()!.Details[0].Detail);
        Assert.Equal("stars", sample.Details[0].Detail);
    }
    [Fact]
    public void DetailIdentityRefusesUnknownOwnersDuplicatesAndLateChanges() {
        var rig = new Rig(framesInFlight: 1);
        rig.Ledger.Configure(passLabels: ["sky"], revision: 1);
        Assert.Throws<ArgumentException>(() => rig.Ledger.ConfigureDetails([new(1, "stars")]));
        Assert.Throws<ArgumentException>(() => rig.Ledger.ConfigureDetails([new(0, "")]));
        Assert.Contains("stars", Assert.Throws<ArgumentException>(() => rig.Ledger.ConfigureDetails([new(0, "stars"), new(0, "stars")])).Message);
        rig.Ledger.ConfigureDetails([new(0, "stars")]);
        rig.Ledger.EnterPass(0); rig.Ledger.LeavePass();
        Assert.Throws<InvalidOperationException>(() => rig.Ledger.ConfigureDetails([new(0, "clouds")]));
    }
    [Fact]
    public void EqualDetailSnapshotsAllocateNothingAfterWarmup() {
        var rig = new Rig(framesInFlight: 1);
        rig.Ledger.Configure(passLabels: ["sky"], revision: 1);
        GpuWorkDetail[] names = [new(0, "stars"), new(0, "clouds")];
        for (var index = 0; index < 100; index++) { rig.Ledger.ConfigureDetails(names); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { rig.Ledger.ConfigureDetails(names); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
