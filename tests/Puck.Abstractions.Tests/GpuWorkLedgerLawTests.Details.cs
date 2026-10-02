using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

public sealed partial class GpuWorkLedgerLawTests {
    [Fact]
    public void SubmittedDetailsKeepTheirOwnerAndNameAcrossMutationAndReplacement() {
        var rig = new Rig(framesInFlight: 3);

        rig.Ledger.Configure(passLabels: ["sky", "shadow"], revision: 1);
        GpuWorkDetail[] names = [new(Detail: "stars", Pass: 0), new(Detail: "sun", Pass: 1)];

        rig.Ledger.ConfigureDetails(details: names);
        rig.Ledger.EnterPass(pass: 0); rig.Ledger.LeavePass();
        rig.Ledger.StandPass(pass: 1);
        var first = rig.NewFence();

        rig.Submit(fence: first);
        names[0] = new(Detail: "clouds", Pass: 0);
        rig.Ledger.ConfigureDetails(details: names);
        first.Counted.Wait();
        var sample = rig.Read()!;

        Assert.Equal(2, sample.PassCount);
        Assert.Equal(new GpuWorkDetail(Detail: "stars", Pass: 0), sample.Details[0]);
        Assert.Equal(new GpuWorkDetail(Detail: "sun", Pass: 1), sample.Details[1]);
        Assert.True(condition: sample.TryGetDetailCount(column: DispatchColumn, detail: 0, value: out var zero));
        Assert.Equal(actual: zero, expected: 0);
        Assert.False(condition: sample.TryGetDetailCount(column: DispatchColumn, detail: 1, value: out _));
        rig.Ledger.EnterPass(pass: 0); rig.Ledger.LeavePass();
        rig.Ledger.SkipPass(pass: 1);
        var second = rig.NewFence();

        rig.Submit(fence: second); second.Counted.Wait();
        Assert.Equal("clouds", rig.Read()!.Details[0].Detail);
        Assert.Equal("stars", sample.Details[0].Detail);
    }
    [Fact]
    public void DetailIdentityRefusesUnknownOwnersDuplicatesAndLateChanges() {
        var rig = new Rig(framesInFlight: 1);

        rig.Ledger.Configure(passLabels: ["sky"], revision: 1);
        Assert.Throws<ArgumentException>(testCode: () => rig.Ledger.ConfigureDetails(details: [new(Detail: "stars", Pass: 1)]));
        Assert.Throws<ArgumentException>(testCode: () => rig.Ledger.ConfigureDetails(details: [new(Detail: "", Pass: 0)]));
        Assert.Contains("stars", Assert.Throws<ArgumentException>(testCode: () => rig.Ledger.ConfigureDetails(details: [new(Detail: "stars", Pass: 0), new(Detail: "stars", Pass: 0)])).Message);
        rig.Ledger.ConfigureDetails(details: [new(Detail: "stars", Pass: 0)]);
        rig.Ledger.EnterPass(pass: 0); rig.Ledger.LeavePass();
        Assert.Throws<InvalidOperationException>(testCode: () => rig.Ledger.ConfigureDetails(details: [new(Detail: "clouds", Pass: 0)]));
    }
    [Fact]
    public void EqualDetailSnapshotsAllocateNothingAfterWarmup() {
        var rig = new Rig(framesInFlight: 1);

        rig.Ledger.Configure(passLabels: ["sky"], revision: 1);
        GpuWorkDetail[] names = [new(Detail: "stars", Pass: 0), new(Detail: "clouds", Pass: 0)];

        for (var index = 0; (index < 100); index++) { rig.Ledger.ConfigureDetails(details: names); }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 100); index++) { rig.Ledger.ConfigureDetails(details: names); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
