using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A timeline clock is a declared name, so every reference to one is written bare — a keyed section's
/// <c>clock</c>, a keyed value's <c>clock</c> in a block or an object literal — and a key's <c>ease</c> is one word of
/// a closed vocabulary. A quoted clock is refused by name, and the refusal names the bare spelling.</summary>
public class ClockSpellingLawTests {
    private const string Head = """
        schema: "puck.world.definition.v1"

        timeline {
            clocks [
                {
                    name: "day",
                    periodSeconds: 20min,
                    spanSeconds: 24h
                }
            ]
        }

        """;

    private static string Sky(string clock, string ease) => $$"""
        render {
            lighting {
                curvature {
                    ink {
                        clock: {{clock}}
                        keys [
                            { at: 0h, value: 0 }
                            { at: 12h, value: 1, ease: {{ease}} }
                        ]
                    }
                }
            }
            sky {
                layers [
                    fog(name: "haze", density: { clock: {{clock}}, keys [ { at: 0h, value: 0 } { at: 12h, value: 0.01 } ] })
                    sunDisc(name: "disc", radius: { clock: {{clock}}, keys [ { at: 6h, value: 1deg } ] })
                ]
                clock: {{clock}}
                keys [
                    {
                        at: 6h
                        layers {
                            haze: fog(density: 0.02)
                        }
                    }
                ]
            }
        }

        """;

    [Fact]
    public void ABareClockLowersToItsNameAtEveryReference() {
        var render = WorldSources.LowerSourceClean(source: (Head + Sky(clock: "day", ease: "Smooth")))["render"]!;

        Assert.Equal(expected: "day", actual: render["sky"]!["clock"]!.GetValue<string>());
        Assert.Equal(expected: "day", actual: render["sky"]!["layers"]![0]!["density"]!["clock"]!.GetValue<string>());
        Assert.Equal(expected: "day", actual: render["lighting"]!["curvature"]!["ink"]!["clock"]!.GetValue<string>());
        Assert.Equal(expected: "Smooth", actual: render["lighting"]!["curvature"]!["ink"]!["keys"]![1]!["ease"]!.GetValue<string>());
        // A keyed value's value takes its field's unit: one degree of the sun disc's radius, in radians.
        Assert.Equal(expected: 0.017453d, actual: render["sky"]!["layers"]![1]!["radius"]!["keys"]![0]!["value"]!.GetValue<double>(), precision: 6);
        Assert.Equal(expected: 43200L, actual: render["sky"]!["layers"]![0]!["density"]!["keys"]![1]!["at"]!.GetValue<long>());
    }
    [Fact]
    public void AQuotedClockIsRefusedNamingTheBareSpelling() {
        var refusals = WorldSources.Compile(source: (Head + Sky(clock: "\"day\"", ease: "Smooth"))).Diagnostics
            .Where(predicate: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.ArgumentWrittenBare, comparisonType: StringComparison.Ordinal))
            .ToArray();

        // The section's clock, the block's, and the two object literals'.
        Assert.Equal(expected: 4, actual: refusals.Length);
        Assert.All(collection: refusals, action: static refusal => Assert.Contains(expectedSubstring: "write 'clock: day'", actualString: refusal.Message, comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void AQuotedEaseIsRefusedNamingTheBareWord() {
        var refusal = Assert.Single(
            collection: WorldSources.Compile(source: (Head + Sky(clock: "day", ease: "\"Smooth\""))).Diagnostics,
            predicate: static diagnostic => string.Equals(a: diagnostic.Code, b: PuckDiagnosticCodes.ArgumentWrittenBare, comparisonType: StringComparison.Ordinal)
        );

        Assert.Contains(expectedSubstring: "write 'ease: Smooth'", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
}
