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
        _ = coalescer.Drain(gyro: out _, latest: out _, pressed: out var pressed, pressEdges: out _, released: out _);
        Assert.Equal(expected: GamepadButtons.ButtonSouth, actual: pressed);

        coalescer.Reset();

        // The next controller's first report primes the baseline; the same drain must still deliver the
        // previous controller's let-go, or the command dispatched from its press stays held forever.
        coalescer.Update(state: in neutral);
        Assert.True(condition: coalescer.Drain(gyro: out _, latest: out _, pressed: out pressed, pressEdges: out _, released: out var released));
        Assert.Equal(expected: GamepadButtons.None, actual: pressed);
        Assert.Equal(expected: GamepadButtons.ButtonSouth, actual: (released & GamepadButtons.ButtonSouth));
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

        Assert.True(condition: coalescer.Drain(gyro: out _, latest: out _, pressed: out _, pressEdges: out _, released: out var released));
        Assert.Equal(expected: GamepadButtons.ButtonEast, actual: (released & GamepadButtons.ButtonEast));
    }
}
