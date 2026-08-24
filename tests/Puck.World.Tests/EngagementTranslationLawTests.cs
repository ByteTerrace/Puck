using Xunit;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Pins the controller-domain canonicalization at the authoritative intent-to-machine seam, through the ONE
/// channel→meaning vocabulary a body already wears: a kit's <see cref="WorldKit.Pad"/> map.</summary>
public sealed class EngagementTranslationLawTests {
    private const string PadKitName = "cabinet";

    [Fact]
    public void AnalogTranslationClampsToDestinationElementDomain() {
        using var fixture = Fixtures.FreshServer(definition: BuildPadKitDocument());

        var positive = fixture.Server.Engagement.Translate(
            intent: default(PlayerIntent)
                .WithChannel(ordinal: 0, value: FixedQ4816.FromInteger(value: 2))
                .WithChannel(ordinal: 1, value: FixedQ4816.FromInteger(value: 2))
                .WithChannel(ordinal: 2, value: FixedQ4816.FromInteger(value: 2)),
            kit: PadKitName
        );
        var negative = fixture.Server.Engagement.Translate(
            intent: default(PlayerIntent)
                .WithChannel(ordinal: 0, value: FixedQ4816.FromInteger(value: -2))
                .WithChannel(ordinal: 1, value: FixedQ4816.FromInteger(value: -2))
                .WithChannel(ordinal: 2, value: FixedQ4816.FromInteger(value: -2)),
            kit: PadKitName
        );

        Assert.Equal(expected: 1f, actual: positive.LeftTrigger);
        Assert.Equal(expected: 1f, actual: positive.LeftStick.X);
        Assert.Equal(expected: 1f, actual: positive.RightTrigger);
        Assert.Equal(expected: 0f, actual: negative.LeftTrigger);
        Assert.Equal(expected: -1f, actual: negative.LeftStick.X);
        Assert.Equal(expected: 0f, actual: negative.RightTrigger);
    }
    /// <summary>An application naming no kit reads the engine's baked default — the two movement roles to the left
    /// stick and nothing else, so a machine needing any other element must be given a kit that binds it.</summary>
    [Fact]
    public void UnnamedKitReadsTheBakedMovementRoleDefault() {
        using var fixture = Fixtures.FreshServer(definition: BuildPadKitDocument());

        var pad = fixture.Server.Engagement.Translate(
            intent: default(PlayerIntent)
                .WithChannel(ordinal: 0, value: FixedQ4816.One)
                .WithChannel(ordinal: 1, value: FixedQ4816.One)
                .WithChannel(ordinal: 2, value: FixedQ4816.One),
            kit: null
        );

        Assert.Equal(expected: 1f, actual: pad.LeftStick.X);
        Assert.Equal(expected: 1f, actual: pad.LeftStick.Y);
        Assert.Equal(expected: 0f, actual: pad.LeftTrigger);
        Assert.Equal(expected: 0f, actual: pad.RightTrigger);
    }

    // The fixture document plus one pad-bearing kit, and the test-pattern screen pointed at it. The pad kit reuses
    // the fixture's own body arm verbatim: a kit is ONE vocabulary with two destinations, so a kit that a body wears
    // and a kit a screen reads are the same row shape.
    private static WorldDefinition BuildPadKitDocument() {
        var definition = Fixtures.BuildDocument();
        var seatKit = definition.Kits[0];
        var padKit = seatKit with {
            Name = PadKitName,
            PadRaw = new Dictionary<string, WorldPadElement>(comparer: StringComparer.Ordinal) {
                ["forward"] = WorldPadElement.LeftTrigger,
                ["strafe"] = WorldPadElement.LeftStickX,
                ["turn"] = WorldPadElement.RightTrigger,
            },
        };
        var screen = definition.Screens[0] with {
            Route = new WorldScreenRoute(
                Engageable: false,
                EngageRadius: 0f,
                Kit: PadKitName
            ),
        };

        return (definition with {
            KitRowsRaw = [seatKit, padKit],
            ScreensRaw = [screen],
        });
    }
}
