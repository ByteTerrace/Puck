using Puck.Commands;

namespace Puck.Input.Tests;

/// <summary>The <c>probe.&lt;name&gt;</c> parametric source family: an open-ended, non-relative Axis1D source
/// minted by a world document's own axis binding row rather than a fixed constant set.</summary>
public sealed class ProbeSourceFamilyTests {
    [Fact]
    public void FactoryMintsTheDottedSourceId() {
        Assert.Equal(expected: "probe.head-x", actual: InputSources.Probe.Axis(name: "head-x"));
        _ = Assert.Throws<ArgumentException>(testCode: () => InputSources.Probe.Axis(name: ""));
    }
    [InlineData("probe.head-x")]
    [InlineData("probe.a")]
    [InlineData("probe.mouth-open")]
    [Theory]
    public void KebabNamesResolveToNonRelativeAxis1D(string sourceId) {
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out var kind, sourceId: sourceId));
        Assert.Equal(actual: kind, expected: CommandValueKind.Axis1D);
        Assert.True(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: sourceId));
        Assert.False(condition: InputSourceVocabulary.IsRelative(sourceId: sourceId));
    }
    [InlineData("probe.")]
    [InlineData("probe.Head-X")]
    [InlineData("probe.head_x")]
    [InlineData("probe.head x")]
    [Theory]
    public void MalformedNamesDoNotResolve(string sourceId) {
        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out _, sourceId: sourceId));
    }
    [Fact]
    public void ANameLongerThanSixtyFourCharactersDoesNotResolve() {
        var sourceId = ("probe." + new string(c: 'a', count: 65));

        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out _, sourceId: sourceId));
    }
    [Fact]
    public void ASixtyFourCharacterNameResolves() {
        var sourceId = ("probe." + new string(c: 'a', count: 64));

        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(kind: out var kind, sourceId: sourceId));
        Assert.Equal(actual: kind, expected: CommandValueKind.Axis1D);
    }
}
