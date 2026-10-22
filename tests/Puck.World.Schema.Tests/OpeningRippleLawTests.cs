using System.Text.Json;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>An opening ripples only when its session source authors <c>ripple</c> under projection <c>window</c>: a ripple
/// on a camera projection is refused by name, a window may author it, and the member round-trips through the wire.</summary>
public sealed class OpeningRippleLawTests {
    private static string? ReasonFor(WorldScreenProjection projection) {
        var definition = Fixtures.BuildDocument();

        _ = WorldDefinitionValidator.TryValidateLocally(
            definition: definition with {
                ScreensRaw = [definition.Screens[0] with {
                    Source = new WorldScreenSource.Session(
                        Destination: "elsewhere",
                        Projection: projection,
                        Ripple: true
                    ),
                }],
            },
            reason: out var reason
        );

        return reason;
    }

    [Fact]
    public void ARippleIsRefusedOffAWindow() {
        const string Refusal = "session.ripple requires projection 'window'";

        Assert.Contains(
            actualString: ReasonFor(projection: WorldScreenProjection.Camera),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: Refusal
        );
        Assert.DoesNotContain(
            actualString: (ReasonFor(projection: WorldScreenProjection.Window) ?? string.Empty),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: Refusal
        );
    }
    [Fact]
    public void ARippleIsOmittedFromTheWireUnlessAuthored() {
        var plain = JsonSerializer.Serialize<WorldScreenSource>(new WorldScreenSource.Session(Destination: "elsewhere", Projection: WorldScreenProjection.Window), WorldJsonContext.Default.Options);
        var rippling = JsonSerializer.Serialize<WorldScreenSource>(new WorldScreenSource.Session(Destination: "elsewhere", Projection: WorldScreenProjection.Window, Ripple: true), WorldJsonContext.Default.Options);

        Assert.DoesNotContain(actualString: plain, comparisonType: StringComparison.Ordinal, expectedSubstring: "ripple");
        Assert.Contains(actualString: rippling, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"ripple\": true");
    }
}
