using Xunit;

using Puck.Commands;
using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="CameraDeviceScanReconciler.Reconcile"/> decides one camera device-scan episode's
/// effect on a device table without touching Media Foundation or a roster. A successful scan adds every newly
/// reported id and retires every previously known id the scan no longer reports. A failed scan retires nothing (it
/// is never read as "every camera unplugged") and narrates only the first failure of a run — a second consecutive
/// failure stays silent — while a success ends the run and re-arms narration for the next failure.
/// </summary>
public sealed class CameraDeviceScanReconcilerLawTests {
    private static readonly InputDeviceId Brio = InputDeviceId.FromKey(key: "camera:brio");
    private static readonly InputDeviceId C920 = InputDeviceId.FromKey(key: "camera:c920");

    [Fact]
    public void Success_AddsNewIds_AndRetiresMissingIds() {
        var known = new HashSet<InputDeviceId> { Brio };
        var outcome = new CameraDeviceScanOutcome.Success(Ids: new HashSet<InputDeviceId> { C920 });

        var decision = CameraDeviceScanReconciler.Reconcile(knownIds: known, outcome: outcome, wasFailing: false);

        Assert.Equal(expected: [C920], actual: decision.ToAdd);
        Assert.Equal(expected: [Brio], actual: decision.ToRetire);
        Assert.False(condition: decision.Narrate);
        Assert.False(condition: decision.IsFailing);
    }
    [Fact]
    public void FailureAfterASuccess_RetiresNothing_AndNarratesOnce() {
        var known = new HashSet<InputDeviceId> { Brio, C920 };
        var outcome = new CameraDeviceScanOutcome.Failure(Message: "enumeration refused");

        var decision = CameraDeviceScanReconciler.Reconcile(knownIds: known, outcome: outcome, wasFailing: false);

        Assert.Empty(collection: decision.ToAdd);
        Assert.Empty(collection: decision.ToRetire);
        Assert.True(condition: decision.Narrate);
        Assert.True(condition: decision.IsFailing);
    }
    [Fact]
    public void SecondConsecutiveFailure_NarratesNothing() {
        var known = new HashSet<InputDeviceId> { Brio, C920 };
        var outcome = new CameraDeviceScanOutcome.Failure(Message: "enumeration refused");

        var decision = CameraDeviceScanReconciler.Reconcile(knownIds: known, outcome: outcome, wasFailing: true);

        Assert.Empty(collection: decision.ToAdd);
        Assert.Empty(collection: decision.ToRetire);
        Assert.False(condition: decision.Narrate);
        Assert.True(condition: decision.IsFailing);
    }
    [Fact]
    public void SuccessAfterFailures_RetiresTheMissingIds_AndRearmsNarration() {
        var known = new HashSet<InputDeviceId> { Brio, C920 };
        var outcome = new CameraDeviceScanOutcome.Success(Ids: new HashSet<InputDeviceId> { Brio });

        var decision = CameraDeviceScanReconciler.Reconcile(knownIds: known, outcome: outcome, wasFailing: true);

        Assert.Empty(collection: decision.ToAdd);
        Assert.Equal(expected: [C920], actual: decision.ToRetire);
        Assert.False(condition: decision.Narrate);
        Assert.False(condition: decision.IsFailing);
    }
}
