using Puck.Abstractions.Pacing;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The windowed host's pacing decision from the display's facts alone: which refreshes the swap chain presents on,
/// whether the timer also holds the cadence, and at what rate. A display that refreshes at a fixed signal is paced by
/// its swap chain; a verified variable-refresh display may show each frame as it arrives, so the timer bounds its
/// cadence while the swap chain still waits whenever the window is composed at the signal.
/// </summary>
public sealed class PresentPacingPolicyLawTests {
    private static readonly DisplayTimingSnapshot FixedSixty = new(
        Signal: new DisplaySignalTiming(hertz: 60.0),
        VariableRefresh: VariableRefreshCapabilities.Unsupported
    );
    private static readonly DisplayTimingSnapshot VariableSixty = new(
        Signal: new DisplaySignalTiming(hertz: 60.0),
        VariableRefresh: VariableRefreshCapabilities.CreateSupported(
            range: new VariableRefreshRange(
                maximumHertz: 165.0,
                minimumHertz: 48.0
            ),
            source: VariableRefreshSource.AmdFreeSync
        )
    );
    private static readonly DisplayTimingSnapshot VariableModeMaximum = new(
        Signal: new DisplaySignalTiming(hertz: 60.0),
        VariableRefresh: VariableRefreshCapabilities.CreateSupported(
            range: new VariableRefreshRange(
                maximumHertz: null,
                minimumHertz: 48.0
            ),
            source: VariableRefreshSource.HdmiForum
        )
    );
    private static readonly DisplayTimingSnapshot SignalOnly = new(
        Signal: new DisplaySignalTiming(hertz: 60.0),
        VariableRefresh: VariableRefreshCapabilities.Unknown
    );

    [Fact]
    public void AFixedDisplayPacesAnExplicitTargetOffTheSwapChainWhenWholeRefreshesReachIt() {
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 60.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: false)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 30.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 2U, TargetHertz: 30.0, TimerBoundsSwapChain: false)
        );
        // No whole number of refreshes reaches 50 Hz, and 10 Hz would take more refreshes than a present can wait for.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 50.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 50.0, TimerBoundsSwapChain: false)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 10.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 10.0, TimerBoundsSwapChain: false)
        );
        // A target above the signal is capped at it.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 144.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: false)
        );
        // A signal the host cannot read leaves the explicit target to the timer.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 60.0, timing: DisplayTimingSnapshot.Unknown),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 60.0, TimerBoundsSwapChain: false)
        );
    }

    [Fact]
    public void AVariableRefreshDisplayHoldsAnExplicitTargetByTheTimerAboveTheSwapChain() {
        // The debug world's 60 Hz on a 48-165 Hz range at a 60 Hz desktop signal: the swap chain presents on every
        // refresh and the timer holds the 60 Hz the range would otherwise exceed.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 60.0, timing: VariableSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: true)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 30.0, timing: VariableSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 2U, TargetHertz: 30.0, TimerBoundsSwapChain: true)
        );
        // A target no whole number of refreshes reaches, or one above the signal, still presents on every refresh, and the
        // range's ceiling, not the signal, caps it.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 50.0, timing: VariableSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 50.0, TimerBoundsSwapChain: true)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 240.0, timing: VariableSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 165.0, TimerBoundsSwapChain: true)
        );
    }

    [Fact]
    public void AutomaticPacingPresentsEveryRefreshAndAVariableRefreshRangeRunsBelowItsCeiling() {
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: FixedSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.SignalTiming, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: false)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: SignalOnly),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.SignalTiming, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: false)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: VariableSixty),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.VariableRefreshRange, RefreshesPerPresent: 1U, TargetHertz: 162.0, TimerBoundsSwapChain: true)
        );
        // A range whose maximum is the video mode's never paces the timer below the signal.
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: VariableModeMaximum),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.VariableRefreshRange, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: true)
        );
        Assert.Equal(
            actual: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: DisplayTimingSnapshot.Unknown),
            expected: new PresentPacingDecision(Basis: PresentPacingBasis.Unbounded, RefreshesPerPresent: 1U, TargetHertz: 0.0, TimerBoundsSwapChain: false)
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PresentPacingPolicy.Resolve(requestedHertz: -1.0, timing: FixedSixty));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PresentPacingPolicy.Resolve(requestedHertz: double.NaN, timing: FixedSixty));
    }

    // Frames land on whole refreshes of a fixed signal, and whenever they arrive on a verified variable-refresh display
    // or one whose signal is unknown.
    [Fact]
    public void FramesLandOnTheSignalsGridOnlyWhereTheSwapChainPacesThem() {
        Assert.Equal(expected: 60d, actual: PresentPacingPolicy.GridHertz(decision: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: FixedSixty), timing: FixedSixty));
        Assert.Equal(expected: 60d, actual: PresentPacingPolicy.GridHertz(decision: PresentPacingPolicy.Resolve(requestedHertz: 30.0, timing: FixedSixty), timing: FixedSixty));
        Assert.Equal(expected: 0d, actual: PresentPacingPolicy.GridHertz(decision: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: VariableSixty), timing: VariableSixty));
        Assert.Equal(expected: 0d, actual: PresentPacingPolicy.GridHertz(decision: PresentPacingPolicy.Resolve(requestedHertz: 0.0, timing: DisplayTimingSnapshot.Unknown), timing: DisplayTimingSnapshot.Unknown));
    }

    [Fact]
    public void ADecisionConvertsItsTargetToATimerPeriod() {
        const long Frequency = 10_000_000L;

        Assert.Equal(expected: 166_666L, actual: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 60.0, TimerBoundsSwapChain: false).ToPeriodTicks(frequency: Frequency));
        // A timer bounding a swap chain runs a tenth of a percent ahead, so a fixed-signal display fills its queue and
        // paces every iteration.
        Assert.Equal(expected: 166_500L, actual: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 1U, TargetHertz: 60.0, TimerBoundsSwapChain: true).ToPeriodTicks(frequency: Frequency));
        Assert.Equal(expected: 0L, actual: new PresentPacingDecision(Basis: PresentPacingBasis.Unbounded, RefreshesPerPresent: 1U, TargetHertz: 0.0, TimerBoundsSwapChain: false).ToPeriodTicks(frequency: Frequency));
        Assert.Equal(expected: long.MaxValue, actual: new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 1e-300, TimerBoundsSwapChain: false).ToPeriodTicks(frequency: Frequency));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: -1.0, TimerBoundsSwapChain: false).ToPeriodTicks(frequency: Frequency));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new PresentPacingDecision(Basis: PresentPacingBasis.ExplicitTarget, RefreshesPerPresent: 0U, TargetHertz: 60.0, TimerBoundsSwapChain: false).ToPeriodTicks(frequency: 0L));
    }
}
