using Xunit;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Pins the controller-domain canonicalization at the authoritative intent-to-machine seam.</summary>
public sealed class EngagementTranslationLawTests {
    [Fact]
    public void AnalogTranslationClampsToDestinationElementDomain() {
        var definition = Fixtures.BuildDocument();
        var screen = definition.Screens[Fixtures.TestPatternScreenIndex] with {
            Route = new WorldScreenRoute(
                Engageable: false,
                EngageRadius: 0f,
                Translation: [
                    new WorldScreenTranslationRow(Channel: "forward", Element: WorldPadElement.LeftTrigger),
                    new WorldScreenTranslationRow(Channel: "strafe", Element: WorldPadElement.LeftStickX),
                    new WorldScreenTranslationRow(Channel: "turn", Element: WorldPadElement.RightTrigger),
                ]
            ),
        };

        using var fixture = Fixtures.FreshServer(definition: (definition with { Screens = [screen] }));

        var positive = fixture.Server.Engagement.Translate(
            intent: default(PlayerIntent)
                .WithChannel(ordinal: 0, value: FixedQ4816.FromInteger(value: 2))
                .WithChannel(ordinal: 1, value: FixedQ4816.FromInteger(value: 2))
                .WithChannel(ordinal: 2, value: FixedQ4816.FromInteger(value: 2)),
            screenIndex: Fixtures.TestPatternScreenIndex
        );
        var negative = fixture.Server.Engagement.Translate(
            intent: default(PlayerIntent)
                .WithChannel(ordinal: 0, value: FixedQ4816.FromInteger(value: -2))
                .WithChannel(ordinal: 1, value: FixedQ4816.FromInteger(value: -2))
                .WithChannel(ordinal: 2, value: FixedQ4816.FromInteger(value: -2)),
            screenIndex: Fixtures.TestPatternScreenIndex
        );

        Assert.Equal(expected: 1f, actual: positive.LeftTrigger);
        Assert.Equal(expected: 1f, actual: positive.LeftStick.X);
        Assert.Equal(expected: 1f, actual: positive.RightTrigger);
        Assert.Equal(expected: 0f, actual: negative.LeftTrigger);
        Assert.Equal(expected: -1f, actual: negative.LeftStick.X);
        Assert.Equal(expected: 0f, actual: negative.RightTrigger);
    }
}
