using System.Numerics;
using Puck.Commands;
using Puck.Scripting;
using Puck.Scripting.Simulation;

namespace Puck.Input.Tests;

public sealed class WindowCommandInputTests {
    [Fact]
    public void MouseProjectionPreservesArbitraryButtonAndTwoAxisMotion() {
        var buttonEvent = WindowInputEvent.PointerButton(button: 16, phase: CommandPhase.Started);
        var button = WindowInputMapper.ToInputSignal(inputEvent: in buttonEvent);
        var motionEvent = WindowInputEvent.PointerDelta(delta: new Vector2(x: 3f, y: -2f));
        var motion = WindowInputMapper.ToInputSignal(inputEvent: in motionEvent);
        var wheelEvent = WindowInputEvent.PointerWheel(notches: new Vector2(x: -1f, y: 0.5f));
        var wheel = WindowInputMapper.ToInputSignal(inputEvent: in wheelEvent);

        Assert.Equal(expected: "mouse.button17", actual: button.Source);
        Assert.Equal(expected: CommandPhase.Started, actual: button.Phase);
        Assert.Equal(expected: InputSources.Mouse.Motion, actual: motion.Source);
        Assert.Equal(expected: new Vector2(x: 3f, y: -2f), actual: motion.Value.AsAxis2D);
        Assert.True(condition: motion.Transient);
        Assert.Equal(expected: InputSources.Mouse.Wheel, actual: wheel.Source);
        Assert.Equal(expected: new Vector2(x: -1f, y: 0.5f), actual: wheel.Value.AsAxis2D);
        Assert.True(condition: wheel.Transient);
    }

    [Fact]
    public void MouseButtonVocabularyIsOpenEndedButCanonical() {
        Assert.Equal(expected: InputSources.Mouse.LeftButton, actual: InputSources.Mouse.Button(number: 1));
        Assert.Equal(expected: "mouse.button17", actual: InputSources.Mouse.Button(number: 17));
        Assert.True(condition: AddonSourceCatalog.TryResolve(sourceId: "mouse.button17", shape: out var shape));
        Assert.Equal(expected: AddonSourceShape.Digital, actual: shape);
        Assert.False(condition: AddonSourceCatalog.TryResolve(sourceId: "mouse.button017", shape: out _));
        Assert.False(condition: AddonSourceCatalog.TryResolve(sourceId: "mouse.button0", shape: out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InputSources.Mouse.Button(number: 0));
    }

    [Fact]
    public void NumberRowAndNumpadProfilesRemainDistinctBindableControls() {
        var rowEvent = WindowInputEvent.KeyDown(key: KeyCode.Digit1);
        var row = WindowInputMapper.ToInputSignal(inputEvent: in rowEvent);
        var padEvent = WindowInputEvent.KeyDown(key: KeyCode.Numpad1);
        var pad = WindowInputMapper.ToInputSignal(inputEvent: in padEvent);

        Assert.Equal(expected: "keyboard.1", actual: row.Source);
        Assert.Equal(expected: "keyboard.numpad1", actual: pad.Source);
        Assert.NotEqual(expected: row.Source, actual: pad.Source);
        Assert.True(condition: AddonSourceCatalog.TryResolve(sourceId: row.Source, shape: out var rowShape));
        Assert.True(condition: AddonSourceCatalog.TryResolve(sourceId: pad.Source, shape: out var padShape));
        Assert.Equal(expected: AddonSourceShape.Digital, actual: rowShape);
        Assert.Equal(expected: AddonSourceShape.Digital, actual: padShape);
    }

    [Fact]
    public void HeldDigitalStateReassertsInFirstDownOrderWithoutRepeatsOrSameFrameDuplicates() {
        var state = new HeldDigitalInputState();
        var device = InputDeviceId.FromConnectionKey(key: "mouse-keyboard");
        var second = InputSignal.Press(source: "mouse.button17", deviceId: device);
        var first = InputSignal.Press(source: "keyboard.a", deviceId: device);

        state.Observe(signal: in second, frameKey: 1UL);
        state.Observe(signal: in first, frameKey: 2UL);

        Assert.True(condition: state.TryReassert(index: 0, frameKey: 2UL, captureTick: 10UL, signal: out var reassertedSecond));
        Assert.False(condition: state.TryReassert(index: 1, frameKey: 2UL, captureTick: 10UL, signal: out _));
        Assert.Equal(expected: "mouse.button17", actual: reassertedSecond.Source);
        Assert.Equal(expected: CommandPhase.Active, actual: reassertedSecond.Phase);

        // An OS repeat neither duplicates nor moves the already-held key.
        state.Observe(signal: in second, frameKey: 3UL);
        Assert.Equal(expected: 2, actual: state.Count);
        Assert.True(condition: state.TryReassert(index: 0, frameKey: 3UL, captureTick: 11UL, signal: out reassertedSecond));
        Assert.True(condition: state.TryReassert(index: 1, frameKey: 3UL, captureTick: 11UL, signal: out var reassertedFirst));
        Assert.Equal(expected: "mouse.button17", actual: reassertedSecond.Source);
        Assert.Equal(expected: "keyboard.a", actual: reassertedFirst.Source);

        var releaseSecond = InputSignal.Release(source: second.Source, deviceId: device);
        state.Observe(signal: in releaseSecond, frameKey: 4UL);
        state.Observe(signal: in second, frameKey: 4UL);

        Assert.True(condition: state.TryReassert(index: 0, frameKey: 4UL, captureTick: 12UL, signal: out reassertedFirst));
        Assert.False(condition: state.TryReassert(index: 1, frameKey: 4UL, captureTick: 12UL, signal: out _));
        Assert.Equal(expected: "keyboard.a", actual: reassertedFirst.Source);

        state.Clear();
        Assert.Equal(expected: 0, actual: state.Count);
    }

    [Fact]
    public void TextPayloadDoesNotBecomeAPermanentHeldControl() {
        var state = new HeldDigitalInputState();
        var typed = InputSignal.Typed(source: InputSources.Keyboard.Text, text: "n");

        state.Observe(signal: in typed, frameKey: 1UL);

        Assert.Equal(expected: 0, actual: state.Count);
    }
}
