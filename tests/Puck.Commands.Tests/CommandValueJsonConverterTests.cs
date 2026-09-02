using System.Text.Json;

using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins <see cref="CommandValueJsonConverter"/>'s <c>kind</c> parse to the exact declared member name —
/// the posture its own remarks promise and the one
/// <see cref="Puck.Abstractions.Documents.StrictEnumConverter{TEnum}"/> gives every other enum in this tree.</summary>
/// <remarks>A tolerant parse here is not merely lax: the converter writes the member name back, so a document
/// carrying <c>"2"</c>, <c>" Axis2D "</c> or <c>"Digital, Axis1D"</c> that survived a read would be REWRITTEN to a
/// name the author never typed on the next save. Refusing the token is the only outcome that keeps a round-trip
/// honest.</remarks>
public sealed class CommandValueJsonConverterTests {
    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData(" Axis2D")]
    [InlineData("Axis2D ")]
    [InlineData("Digital, Axis1D")]
    [InlineData("Digital,Axis1D")]
    [InlineData("axis2d")]
    [InlineData("Nonsense")]
    [InlineData("")]
    public void AKindThatIsNotAnExactDeclaredMemberNameIsRefused(string kind) {
        var exception = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CommandValue>(json: $$"""{"kind":{{JsonSerializer.Serialize(value: kind)}},"raw":[0,0,0,0]}"""));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: kind
        );
    }
    [Fact]
    public void ANumericKindTokenIsRefused() {
        // The JSON NUMBER form, distinct from the numeric STRING above: neither is a member name.
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<CommandValue>(json: """{"kind":2,"raw":[0,0,0,0]}"""));
    }
    [Theory]
    [InlineData(CommandValueKind.Digital)]
    [InlineData(CommandValueKind.Axis1D)]
    [InlineData(CommandValueKind.Axis2D)]
    [InlineData(CommandValueKind.Axis3D)]
    [InlineData(CommandValueKind.Orientation)]
    public void EveryDeclaredKindRoundTripsByName(CommandValueKind kind) {
        var value = new CommandValue(
            Kind: kind,
            Raw: new System.Numerics.Vector4(
                w: 4f,
                x: 1f,
                y: 2f,
                z: 3f
            )
        );
        var json = JsonSerializer.Serialize(value: value);

        Assert.Contains(
            actualString: json,
            expectedSubstring: $"\"kind\":\"{kind}\""
        );
        Assert.Equal(
            actual: JsonSerializer.Deserialize<CommandValue>(json: json),
            expected: value
        );
    }
}
