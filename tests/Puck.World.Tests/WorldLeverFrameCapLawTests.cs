using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the lever frame cap is the lever layout's own maximum. The largest valid lever of every section
/// and every registered knob name, carrying the longest view and the widest value lanes, encodes within
/// <see cref="WorldFrameCodec.MaxPayloadBytes"/> for the lever kind, and a name or view past the layout's text bound is
/// refused by name rather than travelling.
/// </summary>
public sealed class WorldLeverFrameCapLawTests {
    private static IEnumerable<string> RegisteredNames() => typeof(WorldSessionLevers)
        .GetFields(bindingAttr: System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(predicate: field => (field.IsLiteral && (field.FieldType == typeof(string))))
        .Select(selector: field => ((string)field.GetRawConstantValue()!));

    [Fact]
    public void TheLargestValidLeverOfEveryKindEncodesWithinTheCap() {
        var names = RegisteredNames().Append(element: new string(c: 'n', count: SafeName.MaxLength)).ToArray();
        var view = new string(c: 'v', count: SafeName.MaxLength);

        Assert.NotEmpty(collection: names);

        foreach (var section in Enum.GetValues<WorldSection>()) {
            foreach (var name in names) {
                var lever = new WorldSessionLever(
                    A: double.MinValue,
                    B: double.MaxValue,
                    C: double.MinValue,
                    D: double.MaxValue,
                    Name: name,
                    Seat: int.MinValue,
                    Section: section,
                    View: view
                );

                Assert.True(
                    condition: WorldFrameCodec.TryEncode(
                        payload: new WorldSubmissionPayload.Lever(Value: lever),
                        frame: out var frame,
                        failure: out var failure
                    ),
                    userMessage: $"{section} '{name}': {failure.Detail}"
                );
                Assert.NotEmpty(collection: frame);
            }
        }
    }
    [Fact]
    public void TheCapIsTheLayoutsLargestLeverAndATextPastItsBoundIsRefused() {
        Assert.Equal(
            actual: WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Lever),
            expected: WorldSubmissionCodec.MaxLeverBytes
        );

        // Three UTF-8 bytes per character is the widest a BMP character encodes, and the bound is in bytes.
        var widest = new string(c: '\u20AC', count: SafeName.MaxLength);

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeLever(
                lever: new WorldSessionLever(Section: WorldSection.Views, Name: widest, A: 0.0, View: widest),
                bytes: out var bytes,
                failure: out var failure
            ),
            userMessage: failure.Detail
        );
        Assert.Equal(
            actual: bytes.Length,
            expected: WorldSubmissionCodec.MaxLeverBytes
        );
        Assert.False(condition: WorldSubmissionCodec.TryEncodeLever(
            lever: new WorldSessionLever(Section: WorldSection.Views, Name: (widest + "x"), A: 0.0),
            bytes: out _,
            failure: out failure
        ));
        Assert.Equal(
            actual: failure.Refusal,
            expected: WorldCodecRefusal.PayloadMalformed
        );
        Assert.False(condition: WorldSubmissionCodec.TryEncodeLever(
            lever: new WorldSessionLever(Section: WorldSection.Views, Name: "n", A: 0.0, View: (widest + "x")),
            bytes: out _,
            failure: out failure
        ));
    }
}
