using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A window renders at the extent its glass covers on screen, so a window session that authors a pixel
/// resolution is refused by name rather than ignored, while a camera projection keeps its resolution.</summary>
public sealed class WindowSessionResolutionLawTests {
    private static string? ReasonFor(WorldScreenProjection projection) {
        var definition = Fixtures.BuildDocument();

        _ = WorldDefinitionValidator.TryValidateLocally(
            definition: definition with {
                ScreensRaw = [definition.Screens[0] with {
                    Source = new WorldScreenSource.Session(
                        Destination: "elsewhere",
                        Projection: projection,
                        Resolution: new WorldScreenResolution(Height: 320, Width: 256)
                    ),
                }],
            },
            reason: out var reason
        );

        return reason;
    }

    [Fact]
    public void AWindowRefusesAResolutionThatACameraProjectionKeeps() {
        const string Refusal = "session.resolution must be absent under projection 'window'";

        Assert.Contains(
            actualString: ReasonFor(projection: WorldScreenProjection.Window),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: Refusal
        );
        Assert.DoesNotContain(
            actualString: (ReasonFor(projection: WorldScreenProjection.Camera) ?? string.Empty),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: Refusal
        );
    }
}
