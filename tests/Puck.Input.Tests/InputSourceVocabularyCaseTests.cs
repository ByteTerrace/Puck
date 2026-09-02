using Puck.Commands;

namespace Puck.Input.Tests;

/// <summary>A source id's case is authored-document noise, never identity: the binding compiler tables and
/// dispatches source ids <see cref="StringComparer.OrdinalIgnoreCase"/>, so the catalog that decides whether an
/// authored row names a REAL control has to answer the same way or a mis-cased but working row is refused as an
/// unknown control.</summary>
public sealed class InputSourceVocabularyCaseTests {
    [InlineData("Gamepad.ButtonSouth")]
    [InlineData("GAMEPAD.BUTTONSOUTH")]
    [InlineData("gamepad.buttonsouth")]
    [Theory]
    public void ADeclaredSourceIdResolvesInEveryCasing(string sourceId) {
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(
            kind: out var kind,
            sourceId: sourceId
        ));
        Assert.Equal(actual: kind, expected: CommandValueKind.Digital);
        Assert.True(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: sourceId));
    }
    [InlineData("Keyboard.A")]
    [InlineData("KEYBOARD.F12")]
    [InlineData("keyboard.Numpad7")]
    [InlineData("Mouse.Button7")]
    [InlineData("Probe.Head-X")]
    [Theory]
    public void AParametricFamilyMemberResolvesInEveryCasing(string sourceId) {
        Assert.True(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: sourceId));
    }
    [Fact]
    public void TheRelativeAndUnaddressableMarkersAnswerCaseInsensitivelyToo() {
        Assert.True(condition: InputSourceVocabulary.IsRelative(sourceId: "Mouse.Motion"));
        Assert.True(condition: InputSourceVocabulary.IsExplicitlyUnaddressable(sourceId: "Keyboard.Text"));
        // Case-insensitivity widens no vocabulary: an id nothing declares stays unknown in every casing.
        Assert.False(condition: InputSourceVocabulary.IsRelative(sourceId: "Gamepad.ButtonSouth"));
        Assert.False(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: "Gamepad.ButtonSouht"));
        Assert.False(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: "Probe.Head_X"));
    }
}
