using System.Numerics;

using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingWheelGestureStateTests {
    private const float DeadZoneSquared = 0.01f;
    private const float SwitchThresholdSquared = 0.16f;

    // The monotonically increasing arbitration sequence the real selector carries; per test instance, so no test
    // depends on another's numbering.
    private long m_sequence;

    [Fact]
    public void AClosedGestureAcceptsNothing() {
        var state = new BindingWheelGestureState();

        Assert.False(condition: state.Opened);
        Assert.False(condition: state.CanArm);
        Assert.False(condition: state.TryCaptureSpatialNeutral(position: new Vector2(
            x: 4f,
            y: 5f
        )));
        Assert.False(condition: Select(
            state: state,
            x: 1f,
            y: 0f
        ));
        Assert.False(condition: state.AxisKnown);
    }
    [Fact]
    public void OpeningClearsCancellationAndEverySampleFromThePriorGesture() {
        var state = new BindingWheelGestureState();

        state.Open();
        Assert.True(condition: Select(
            state: state,
            x: 1f,
            y: 0f
        ));
        Assert.True(condition: state.TryCaptureSpatialNeutral(position: new Vector2(
            x: 7f,
            y: 8f
        )));
        state.Cancel();

        Assert.False(condition: state.CanArm);

        state.Open();

        Assert.True(condition: state.CanArm);
        Assert.False(condition: state.Cancelled);
        Assert.False(condition: state.AxisKnown);
        Assert.False(condition: state.SpatialNeutralKnown);
        Assert.Equal(expected: Vector2.Zero, actual: state.Axis);
        Assert.Equal(expected: 0L, actual: state.AxisSequence);
    }
    [Fact]
    public void CancellationOutlivesCloseSoALateReleaseStillReportsIt() {
        var state = new BindingWheelGestureState();

        state.Open();
        state.Cancel();
        state.Close();

        Assert.True(condition: state.Cancelled);
        Assert.False(condition: state.Opened);
        Assert.False(condition: state.CanArm);
    }
    [Fact]
    public void TheFirstSpatialPositionOfAGestureIsTheOriginAndLaterOnesCannotMoveIt() {
        var state = new BindingWheelGestureState();

        state.Open();

        Assert.True(condition: state.TryCaptureSpatialNeutral(position: new Vector2(
            x: 10f,
            y: 20f
        )));
        Assert.False(condition: state.TryCaptureSpatialNeutral(position: new Vector2(
            x: 30f,
            y: 40f
        )));
        Assert.Equal(
            actual: state.SpatialNeutral,
            expected: new Vector2(
                x: 10f,
                y: 20f
            )
        );
    }
    [Fact]
    public void ANonFiniteSampleOrRangeIsRefusedWithoutDisturbingTheGesture() {
        var state = new BindingWheelGestureState();

        state.Open();

        Assert.False(condition: Select(
            state: state,
            x: float.NaN,
            y: 1f
        ));
        Assert.False(condition: state.TrySelect(
            axis: new Vector2(
                x: 1f,
                y: 0f
            ),
            deadZoneSquared: float.NaN,
            sequence: 1L,
            switchThresholdSquared: SwitchThresholdSquared
        ));
        Assert.False(condition: state.TrySelect(
            axis: new Vector2(
                x: 1f,
                y: 0f
            ),
            deadZoneSquared: -1f,
            sequence: 2L,
            switchThresholdSquared: SwitchThresholdSquared
        ));
        Assert.False(condition: state.TrySelect(
            axis: new Vector2(
                x: 1f,
                y: 0f
            ),
            deadZoneSquared: DeadZoneSquared,
            sequence: 3L,
            switchThresholdSquared: float.PositiveInfinity
        ));
        Assert.False(condition: state.AxisKnown);
    }
    [Fact]
    public void ANeutralReadingRetainsTheThrowThatEarnedTheSwitchThreshold() {
        var state = new BindingWheelGestureState();

        state.Open();

        // A weak flick, then a full throw: the full throw is the stable direction the neutral return retains.
        Assert.True(condition: Select(
            state: state,
            x: 0.2f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0f,
            y: 0.9f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0f,
            y: 0f
        ));

        Assert.True(condition: state.AxisNeutral);
        Assert.Equal(
            actual: state.Axis,
            expected: new Vector2(
                x: 0f,
                y: 0.9f
            )
        );
    }
    [Fact]
    public void AnExcursionThatNeverReachesTheSwitchThresholdRetainsItsPeak() {
        var state = new BindingWheelGestureState();

        state.Open();

        Assert.True(condition: Select(
            state: state,
            x: 0.2f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0.35f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0.15f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0f,
            y: 0f
        ));

        Assert.True(condition: state.AxisNeutral);
        Assert.Equal(
            actual: state.Axis,
            expected: new Vector2(
                x: 0.35f,
                y: 0f
            )
        );
    }
    [Fact]
    public void AWeakOppositeSampleAfterNeutralIsSpringRebound() {
        var state = new BindingWheelGestureState();

        state.Open();

        Assert.True(condition: Select(
            state: state,
            x: 0.9f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0f,
            y: 0f
        ));
        // Below the switch threshold and on the far side: a return-spring overshoot cannot steal the selection.
        Assert.False(condition: Select(
            state: state,
            x: -0.3f,
            y: 0f
        ));
        Assert.Equal(
            actual: state.Axis,
            expected: new Vector2(
                x: 0.9f,
                y: 0f
            )
        );
        // Past the threshold it is a deliberate new throw.
        Assert.True(condition: Select(
            state: state,
            x: -0.8f,
            y: 0f
        ));
        Assert.False(condition: state.AxisNeutral);
        Assert.Equal(
            actual: state.Axis,
            expected: new Vector2(
                x: -0.8f,
                y: 0f
            )
        );
    }
    [Fact]
    public void ASecondNeutralReadingChangesNothing() {
        var state = new BindingWheelGestureState();

        state.Open();

        Assert.True(condition: Select(
            state: state,
            x: 0.9f,
            y: 0f
        ));
        Assert.True(condition: Select(
            state: state,
            x: 0f,
            y: 0f
        ));

        var sequence = state.AxisSequence;

        Assert.False(condition: Select(
            state: state,
            x: 0.05f,
            y: 0f
        ));
        Assert.Equal(expected: sequence, actual: state.AxisSequence);
    }

    private bool Select(BindingWheelGestureState state, float x, float y) {
        return state.TrySelect(
            axis: new Vector2(
                x: x,
                y: y
            ),
            deadZoneSquared: DeadZoneSquared,
            sequence: ++m_sequence,
            switchThresholdSquared: SwitchThresholdSquared
        );
    }
}
