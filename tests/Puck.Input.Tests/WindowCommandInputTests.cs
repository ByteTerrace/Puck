using System.Numerics;
using Puck.Commands;

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
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out var kind, sourceId: "mouse.button17"));
        Assert.Equal(actual: kind, expected: CommandValueKind.Digital);
        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out _, sourceId: "mouse.button017"));
        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out _, sourceId: "mouse.button0"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => InputSources.Mouse.Button(number: 0));
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
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: row.Source, kind: out var rowKind));
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: pad.Source, kind: out var padKind));
        Assert.Equal(actual: rowKind, expected: CommandValueKind.Digital);
        Assert.Equal(actual: padKind, expected: CommandValueKind.Digital);
    }
    [Fact]
    public void HeldDigitalStateReassertsInFirstDownOrderWithoutRepeatsOrSameFrameDuplicates() {
        var state = new HeldDigitalInputState();
        var device = InputDeviceId.FromConnectionKey(key: "mouse-keyboard");
        var second = InputSignal.Press(source: "mouse.button17", deviceId: device);
        var first = InputSignal.Press(source: "keyboard.a", deviceId: device);

        state.Observe(frameKey: 1UL, signal: in second);
        state.Observe(frameKey: 2UL, signal: in first);

        Assert.True(condition: state.TryReassert(captureTick: 10UL, frameKey: 2UL, index: 0, signal: out var reassertedSecond));
        Assert.False(condition: state.TryReassert(captureTick: 10UL, frameKey: 2UL, index: 1, signal: out _));
        Assert.Equal(expected: "mouse.button17", actual: reassertedSecond.Source);
        Assert.Equal(expected: CommandPhase.Active, actual: reassertedSecond.Phase);

        // An OS repeat neither duplicates nor moves the already-held key.
        state.Observe(frameKey: 3UL, signal: in second);
        Assert.Equal(expected: 2, actual: state.Count);
        Assert.True(condition: state.TryReassert(captureTick: 11UL, frameKey: 3UL, index: 0, signal: out reassertedSecond));
        Assert.True(condition: state.TryReassert(captureTick: 11UL, frameKey: 3UL, index: 1, signal: out var reassertedFirst));
        Assert.Equal(expected: "mouse.button17", actual: reassertedSecond.Source);
        Assert.Equal(expected: "keyboard.a", actual: reassertedFirst.Source);

        var releaseSecond = InputSignal.Release(source: second.Source, deviceId: device);

        state.Observe(frameKey: 4UL, signal: in releaseSecond);
        state.Observe(frameKey: 4UL, signal: in second);

        Assert.True(condition: state.TryReassert(captureTick: 12UL, frameKey: 4UL, index: 0, signal: out reassertedFirst));
        Assert.False(condition: state.TryReassert(captureTick: 12UL, frameKey: 4UL, index: 1, signal: out _));
        Assert.Equal(expected: "keyboard.a", actual: reassertedFirst.Source);

        state.Clear();
        Assert.Equal(expected: 0, actual: state.Count);
    }
    [Fact]
    public void TextPayloadDoesNotBecomeAPermanentHeldControl() {
        var state = new HeldDigitalInputState();
        var typed = InputSignal.Typed(source: InputSources.Keyboard.Text, text: "n");

        state.Observe(frameKey: 1UL, signal: in typed);

        Assert.Equal(expected: 0, actual: state.Count);
    }
}
