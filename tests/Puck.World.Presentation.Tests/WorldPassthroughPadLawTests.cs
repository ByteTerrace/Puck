using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// Laws for a local pad's sticks as a desktop pointer (<see cref="WorldPassthroughPad"/>): the travel over an interval is
/// the exact integral of the authored speed, curve and boost ramp, so it does not depend on how many frames the interval
/// is split into; a rest restarts the ramp; the wheel turns at the authored rate; and only a local pad's own bound control
/// counts as the pad.
/// </summary>
public sealed class WorldPassthroughPadLawTests {
    private static readonly WorldScreenPadPointer Feel = new(
        Boost: 3f,
        Curve: 2f,
        RampSeconds: 0.6f,
        ScrollSpeed: 12f,
        Speed: 0.5f
    );

    // The travel a full tilt held for `seconds` covers, split into `frames` equal frames.
    private static (Vector2 Pointer, Vector2 Wheel) Held(Vector2 stick, Vector2 scroll, double seconds, int frames) {
        var pad = new WorldPassthroughPad();
        var pointer = Vector2.Zero;
        var wheel = Vector2.Zero;

        pad.SetStick(slot: 0, value: stick);
        pad.SetScroll(slot: 0, value: scroll);

        for (var frame = 0; (frame < frames); frame++) {
            var (travel, turn) = pad.Advance(feel: Feel, seconds: (seconds / frames));

            pointer += travel;
            wheel += turn;
        }

        return (pointer, wheel);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(60)]
    [InlineData(144)]
    public void TheTravelIsTheExactIntegralWhateverTheFrameRate(int framesPerSecond) {
        // A full tilt up-right held 1.5 s: speed × (T + (boost − 1) × (R/2 + (T − R))) = 0.5 × (1.5 + 2 × 1.2) = 1.95
        // picture heights, up the display (−Y), and the wheel's 12 notches a second for 1.5 s at a half tilt squared.
        var stick = Vector2.Normalize(value: new Vector2(x: 3f, y: 4f));
        var (pointer, wheel) = Held(
            frames: ((int)(1.5 * framesPerSecond)),
            scroll: new Vector2(x: 0f, y: -0.5f),
            seconds: 1.5,
            stick: stick
        );

        Assert.Equal(expected: (0.6 * 1.95), actual: pointer.X, tolerance: 1e-4);
        Assert.Equal(expected: (-0.8 * 1.95), actual: pointer.Y, tolerance: 1e-4);
        Assert.Equal(expected: 0.0, actual: wheel.X, tolerance: 1e-6);
        Assert.Equal(expected: (-12.0 * 0.25 * 1.5), actual: wheel.Y, tolerance: 1e-4);
    }
    [Fact]
    public void ASmallTiltIsFineAndARestRestartsTheRamp() {
        var pad = new WorldPassthroughPad();

        // A half tilt under a squared curve moves at a quarter of the speed, still inside the ramp's first instant.
        pad.SetStick(slot: 2, value: new Vector2(x: 0.5f, y: 0f));
        Assert.Equal(expected: 2, actual: pad.Slot);
        Assert.Equal(expected: (0.5 * 0.25 * (0.1 + (2.0 * ((0.1 * 0.1) / 1.2)))), actual: pad.Advance(feel: Feel, seconds: 0.1).Pointer.X, tolerance: 1e-6);

        // A rest restarts the ramp: the next tenth travels as the first did.
        pad.SetStick(slot: 2, value: Vector2.Zero);
        Assert.Equal(expected: Vector2.Zero, actual: pad.Advance(feel: Feel, seconds: 0.1).Pointer);
        pad.SetStick(slot: 2, value: new Vector2(x: 0.5f, y: 0f));
        Assert.Equal(expected: (0.5 * 0.25 * (0.1 + (2.0 * ((0.1 * 0.1) / 1.2)))), actual: pad.Advance(feel: Feel, seconds: 0.1).Pointer.X, tolerance: 1e-6);

        // An over-long deflection counts as a full one; no interval, no feel or a nonsense interval travels nothing.
        pad.SetStick(slot: 2, value: new Vector2(x: 5f, y: 0f));
        Assert.Equal(expected: Vector2.Zero, actual: pad.Advance(feel: Feel, seconds: 0.0).Pointer);
        Assert.Equal(expected: Vector2.Zero, actual: pad.Advance(feel: Feel, seconds: double.NaN).Pointer);
        Assert.Equal(expected: Vector2.Zero, actual: pad.Advance(feel: null, seconds: 1.0).Pointer);
        Assert.Equal(expected: (0.5 * 3.0 * 0.5), actual: pad.Advance(feel: Feel, seconds: 0.5).Pointer.X, tolerance: 1e-5);
        pad.SetStick(slot: 2, value: new Vector2(x: float.NaN, y: 0f));
        Assert.Equal(expected: Vector2.Zero, actual: pad.Advance(feel: Feel, seconds: 0.5).Pointer);

        // A ramp of 0 boosts at once.
        pad.SetStick(slot: 2, value: Vector2.UnitX);
        Assert.Equal(expected: 1.5, actual: pad.Advance(feel: (Feel with { RampSeconds = 0f }), seconds: 1.0).Pointer.X, tolerance: 1e-6);

        pad.Reset();
        Assert.Equal(expected: (-1, Vector2.Zero, Vector2.Zero), actual: (pad.Slot, pad.Stick, pad.Scroll));
    }
    [Fact]
    public void OnlyALocalPadsBoundControlIsThePad() {
        var device = InputDeviceId.FromConnectionKey(key: "xinput-0");

        Assert.True(condition: WorldPassthroughPad.IsLocalPad(deviceId: device, origin: CommandOrigin.Binding, source: "gamepad.buttonSouth"));
        // A typed line, a scripted player.signal (no device), a probe's axis, no source, and an unknown gamepad name.
        Assert.False(condition: WorldPassthroughPad.IsLocalPad(deviceId: device, origin: CommandOrigin.Text, source: "gamepad.buttonSouth"));
        Assert.False(condition: WorldPassthroughPad.IsLocalPad(deviceId: default, origin: CommandOrigin.Binding, source: "gamepad.buttonSouth"));
        Assert.False(condition: WorldPassthroughPad.IsLocalPad(deviceId: device, origin: CommandOrigin.Binding, source: "probe.axis.tilt"));
        Assert.False(condition: WorldPassthroughPad.IsLocalPad(deviceId: device, origin: CommandOrigin.Binding, source: null));
        Assert.False(condition: WorldPassthroughPad.IsLocalPad(deviceId: device, origin: CommandOrigin.Binding, source: "gamepad.nothing"));
    }
}
