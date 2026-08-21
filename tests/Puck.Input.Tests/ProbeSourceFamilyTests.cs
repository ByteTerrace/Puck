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
    [Theory]
    [InlineData("probe.head-x")]
    [InlineData("probe.a")]
    [InlineData("probe.mouth-open")]
    public void KebabNamesResolveToNonRelativeAxis1D(string sourceId) {
        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: sourceId, kind: out var kind));
        Assert.Equal(expected: CommandValueKind.Axis1D, actual: kind);
        Assert.True(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: sourceId));
        Assert.False(condition: InputSourceVocabulary.IsRelative(sourceId: sourceId));
    }
    [Theory]
    [InlineData("probe.")]
    [InlineData("probe.Head-X")]
    [InlineData("probe.head_x")]
    [InlineData("probe.head x")]
    public void MalformedNamesDoNotResolve(string sourceId) {
        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: sourceId, kind: out _));
    }
    [Fact]
    public void ANameLongerThanSixtyFourCharactersDoesNotResolve() {
        var sourceId = ("probe." + new string(c: 'a', count: 65));

        Assert.False(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: sourceId, kind: out _));
    }
    [Fact]
    public void ASixtyFourCharacterNameResolves() {
        var sourceId = ("probe." + new string(c: 'a', count: 64));

        Assert.True(condition: InputSourceVocabulary.TryResolveDeclaredKind(sourceId: sourceId, kind: out var kind));
        Assert.Equal(expected: CommandValueKind.Axis1D, actual: kind);
    }
}
