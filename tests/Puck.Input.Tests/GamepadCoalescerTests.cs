using Puck.Input.Devices;

namespace Puck.Input.Tests;

public sealed class GamepadCoalescerTests {
    [Fact]
    public void Reset_converts_a_held_button_into_a_release_edge() {
        var coalescer = new GamepadCoalescer();
        var neutral = GamepadState.Neutral;
        var held = GamepadState.Neutral with { Buttons = GamepadButtons.ButtonSouth, };

        coalescer.Update(state: in neutral);
        coalescer.Update(state: in held);
        _ = coalescer.Drain(gyro: out _, latest: out _, pressEdges: out _, pressed: out var pressed, released: out _);
        Assert.Equal(actual: pressed, expected: GamepadButtons.ButtonSouth);

        coalescer.Reset();

        // The next controller's first report primes the baseline; the same drain must still deliver the
        // previous controller's let-go, or the command dispatched from its press stays held forever.
        coalescer.Update(state: in neutral);
        Assert.True(condition: coalescer.Drain(gyro: out _, latest: out _, pressEdges: out _, pressed: out pressed, released: out var released));
        Assert.Equal(actual: pressed, expected: GamepadButtons.None);
        Assert.Equal(actual: released & GamepadButtons.ButtonSouth, expected: GamepadButtons.ButtonSouth);
    }
    [Fact]
    public void Reset_promotes_an_undrained_press_to_a_release_edge() {
        var coalescer = new GamepadCoalescer();
        var neutral = GamepadState.Neutral;
        var held = GamepadState.Neutral with { Buttons = GamepadButtons.ButtonEast, };

        coalescer.Update(state: in neutral);
        coalescer.Update(state: in held);
        coalescer.Reset();
        coalescer.Update(state: in neutral);

        Assert.True(condition: coalescer.Drain(gyro: out _, latest: out _, pressEdges: out _, pressed: out _, released: out var released));
        Assert.Equal(actual: released & GamepadButtons.ButtonEast, expected: GamepadButtons.ButtonEast);
    }
}
