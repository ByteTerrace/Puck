using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the shared ordered held-modifier primitive: the press/release hysteresis band, press-order recovery,
/// and the <see cref="InputSignal"/> overload's phase-to-value folding.</summary>
public sealed class HeldOrderTrackerTests {
    [Fact]
    public void SetLatchesAndReleasesAcrossTheHysteresisBand() {
        var tracker = new HeldOrderTracker(modifierCount: 1, pressThreshold: 0.5f, releaseThreshold: 0.25f);

        Assert.True(condition: tracker.Set(index: 0, value: 0.6f));   // crosses press → latched
        Assert.Equal(expected: 1, actual: tracker.Count);

        Assert.False(condition: tracker.Set(index: 0, value: 0.4f));  // inside the band → holds, no change
        Assert.Equal(expected: 1, actual: tracker.Count);

        Assert.True(condition: tracker.Set(index: 0, value: 0.2f));   // crosses release → unlatched
        Assert.Equal(expected: 0, actual: tracker.Count);
    }

    [Fact]
    public void HeldOrderRecoversPressOrder() {
        var tracker = new HeldOrderTracker(modifierCount: 3, pressThreshold: 0.5f, releaseThreshold: 0.25f);

        _ = tracker.Set(index: 2, value: 1f);
        _ = tracker.Set(index: 0, value: 1f);

        Assert.Equal(expected: [2, 0], actual: tracker.HeldOrder.ToArray());
    }

    [Fact]
    public void SignalOverloadReleasesOnACompletedEdgeWhateverValueItCarries() {
        var tracker = new HeldOrderTracker(modifierCount: 1, pressThreshold: 0.5f, releaseThreshold: 0.25f);

        Assert.True(condition: tracker.Set(index: 0, signal: InputSignal.Press(source: "trigger")));
        Assert.Equal(expected: 1, actual: tracker.Count);

        Assert.True(condition: tracker.Set(index: 0, signal: InputSignal.Release(source: "trigger")));
        Assert.Equal(expected: 0, actual: tracker.Count);
    }

    [Fact]
    public void ResetReleasesEveryModifier() {
        var tracker = new HeldOrderTracker(modifierCount: 2, pressThreshold: 0.5f, releaseThreshold: 0.25f);

        _ = tracker.Set(index: 0, value: 1f);
        _ = tracker.Set(index: 1, value: 1f);
        tracker.Reset();

        Assert.Equal(expected: 0, actual: tracker.Count);
    }

    [Fact]
    public void MismatchedPerModifierThresholdListsAreRefused() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => new HeldOrderTracker(pressThresholds: [0.5f, 0.5f], releaseThresholds: [0.25f]));
    }
}
