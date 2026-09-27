using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a screen's engagement policy follows the live document. The fold reads each screen's reach and pad kit, and
/// each kit's pad map, from the rows the document holds now, compiled once per row object, so a live edit of the kit or
/// of the screen changes the very next fold, with the seat engaged throughout and nothing re-composed.
/// </summary>
public sealed class EngagementPolicyLiveEditLawTests {
    private const string PadKitName = "cabinet";

    private static readonly FixedQ4816 Full = FixedQ4816.FromInteger(value: 1);

    private static Dictionary<string, WorldPadElement> Pad(WorldPadElement forward) => new(comparer: StringComparer.Ordinal) {
        ["forward"] = forward,
        ["strafe"] = WorldPadElement.LeftStickX,
    };
    // The fixture document with one pad-bearing kit and the test-pattern screen reading it.
    private static WorldDefinition Document() {
        var definition = Fixtures.BuildDocument();
        var seatKit = definition.Kits[0];

        return (definition with {
            KitRowsRaw = [seatKit, seatKit with {
                Name = PadKitName,
                PadRaw = Pad(forward: WorldPadElement.LeftTrigger),
            }],
            ScreensRaw = [definition.Screens[0] with {
                Route = new WorldScreenRoute(
                    Engageable: false,
                    EngageRadius: 0f,
                    Kit: PadKitName
                ),
            }],
        });
    }
    // One tick with seat 1's body pressing forward and strafe fully; the pad the fold wrote for the screen.
    private static MachinePadState Fold(WorldFixture fixture) {
        fixture.Server.Body(index: 0)!.SubmitIntent(intent: default(PlayerIntent)
            .WithChannel(
                ordinal: 0,
                value: Full
            )
            .WithChannel(
                ordinal: 1,
                value: Full
            ));
        fixture.Step();

        return Assert.Single(collection: fixture.Server.Engagement.BuildPadSnapshot().ToArray()).Pad;
    }
    private static void Apply(WorldFixture fixture, WorldMutation mutation) {
        fixture.Server.EnqueueMutation(mutation: mutation);
        Assert.True(condition: fixture.Server.DrainAdministrative());
    }

    [Fact]
    public void ALiveEditOfAScreensKitOrReachReachesTheNextFold() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var seat = Principal.Seat(slot: 0);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: 0,
            target: GrantSubject.Screen(index: fixture.Server.Definition.Screens[0].Index),
            exclusive: true,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));

        // As authored: forward pulls the left trigger.
        var authored = Fold(fixture: fixture);

        Assert.Equal(expected: 1f, actual: authored.LeftTrigger);
        Assert.Equal(expected: 0f, actual: authored.RightTrigger);

        // The kit's pad map edited live: forward now pulls the right trigger, on the very next fold.
        var kit = fixture.Server.Definition.Kits.Single(predicate: static row => (row.Name == PadKitName));

        Apply(
            fixture: fixture,
            mutation: new WorldMutation.UpsertKit(
                Kit: kit with { PadRaw = Pad(forward: WorldPadElement.RightTrigger) },
                Principal: Principal.Console
            )
        );

        var rebound = Fold(fixture: fixture);

        Assert.Equal(expected: 0f, actual: rebound.LeftTrigger);
        Assert.Equal(expected: 1f, actual: rebound.RightTrigger);
        Assert.Equal(expected: 1f, actual: rebound.LeftStick.X);

        // The screen's reach edited live to strafe alone: forward no longer reaches the pad, strafe still does.
        var screen = fixture.Server.Definition.Screens[0];

        Apply(
            fixture: fixture,
            mutation: new WorldMutation.UpsertScreen(
                Principal: Principal.Console,
                Screen: screen with { Route = screen.Route with { Channels = ["strafe"] } }
            )
        );

        var narrowed = Fold(fixture: fixture);

        Assert.Equal(expected: 0f, actual: narrowed.RightTrigger);
        Assert.Equal(expected: 1f, actual: narrowed.LeftStick.X);

        // The read-back shows the policy the fold applies: the held member still records the reach it composed with,
        // and body.channels echoes it through WorldEngagement.Live, which carries the edited reach.
        var held = fixture.Server.Grants.Applications(principal: seat).Single(predicate: static application => (application.Target.Kind == GrantSubjectKind.Screen));
        var live = fixture.Server.Engagement.Live(application: held);

        Assert.NotEqual(
            actual: live.Reach,
            expected: held.Reach
        );
        Assert.Equal(
            actual: live.Kit,
            expected: PadKitName
        );
    }
}
