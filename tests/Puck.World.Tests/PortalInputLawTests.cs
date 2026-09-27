using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: input a viewer forwards through a portal reaches the destination's rules as its session's input. A session
/// intent latches on the session, never a body; <c>$pointer:any:&lt;screen&gt;</c> reads the first participant allowed to
/// point at the screen (holding <c>Control</c> over it) whose ray lands on it, and <c>press:&lt;channel&gt;</c> reads that
/// participant's channel, a press made between two steps included. A release clears the slot, and an ended session reads
/// nothing and is refused.
/// </summary>
public sealed partial class PortalInputLawTests {
    private const string OnRow = "portalOn";
    private const string PressRow = "portalPress";
    private const string Viewer = "viewer/portal";

    // The fixture's screen 0 faces +z at (0, 1, 0), two units square: a ray down -z from z = 5 lands on it.
    private static SourceRay OnScreen => new(
        Direction: FixedVector3.FromVector3(value: -System.Numerics.Vector3.UnitZ),
        Origin: FixedVector3.FromVector3(value: new System.Numerics.Vector3(
            x: 0f,
            y: 1f,
            z: 5f
        ))
    );

    private static WorldStateRow Row(string name, CellKind kind) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: ((kind == CellKind.Fixed)
                ? CellValue.Fixed(rawBits: 0L)
                : CellValue.Int(value: 0L))
        )]
    );
    // A destination whose screen 0 takes simulation input, admitting a viewer's session with its whole-world view and,
    // when granted, Control over that screen; one rule copies what any participant points and presses there into state.
    private static WorldDefinition Destination(bool control) {
        var document = Fixtures.BuildDocument();

        return document with {
            Admission = [new WorldAdmissionEntry(
                Algorithm: string.Empty,
                Disclosure: WorldDisclosureTier.Replica,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [
                    new WorldAdmissionGrant(
                        Budget: 64,
                        Capability: WorldCapability.Observe,
                        Subject: GrantSubject.All
                    ),
                    .. (control
                        ? (WorldAdmissionGrant[])[new WorldAdmissionGrant(
                            Capability: WorldCapability.Control,
                            Subject: GrantSubject.Screen(index: 0)
                        )]
                        : []),
                ],
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                PublicKey: string.Empty,
                Subject: null
            )],
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "portal-copy"),
                Effects: [
                    new ActionEffect.SetState(
                        FromState: $"{WorldRuleFacts.PointerPrefix}any:0:on",
                        State: OnRow
                    ),
                    new ActionEffect.SetState(
                        FromState: $"{WorldRuleFacts.PointerPrefix}any:0:press:forward",
                        State: PressRow
                    ),
                ]
            )],
            ScreensRaw = [.. document.Screens.Select(selector: row => row with {
                Route = (row.Route with { Input = SourceDestination.Simulation }),
            })],
            StateRaw = new WorldStateSection(World: [
                Row(
                    kind: CellKind.Int,
                    name: OnRow
                ),
                Row(
                    kind: CellKind.Fixed,
                    name: PressRow
                ),
            ]),
        };
    }
    private static WorldSessionObservation Observe(WorldFixture fixture) {
        var observation = fixture.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed),
            sourceAuthority: Viewer
        );

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        return observation!;
    }
    // One forward as the destination receives it: the session's ray in this world and its "forward" channel.
    private static void Forward(WorldFixture fixture, Principal session, SourceRay? ray, double press) {
        var channels = new ChannelValues();

        channels[0] = FixedQ4816.FromDouble(value: press);
        fixture.Server.EnqueueIntent(submission: new IntentSubmission(
            EntityIndex: -1,
            Intent: new PlayerIntent(
                Channels: channels,
                SourceRay: ray
            ),
            Principal: session,
            Tick: fixture.Server.NextInputTick
        ));
    }
    private static (long On, FixedQ4816 Press) Read(WorldFixture fixture) => (
        fixture.Row(name: OnRow).Cells!.Single().Value.Raw,
        FixedQ4816.FromRawBits(value: fixture.Row(name: PressRow).Cells!.Single().Value.Raw)
    );

    [Fact]
    public void ASessionPointsAtAScreenOnlyWhileItHoldsControlOverIt() {
        long OnWith(bool control) {
            using var fixture = Fixtures.FreshServer(definition: Destination(control: control));
            var observation = Observe(fixture: fixture);

            Forward(
                fixture: fixture,
                press: 1d,
                ray: OnScreen,
                session: observation.Session
            );
            fixture.Step();
            fixture.Step();

            return Read(fixture: fixture).On;
        }

        Laws.RefusalWithControl(
            lawId: "portal.pointing-needs-control-over-the-screen",
            deniedOutcome: () => (OnWith(control: false) == 1L),
            controlOutcome: () => (OnWith(control: true) == 1L)
        );
    }
    [Fact]
    public void AnEndedSession_ReadsNothing_AndItsPrincipalIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Destination(control: true));
        var observation = Observe(fixture: fixture);

        Forward(
            fixture: fixture,
            press: 1d,
            ray: OnScreen,
            session: observation.Session
        );
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture).On,
            expected: 1L
        );

        observation.Dispose();
        Forward(
            fixture: fixture,
            press: 1d,
            ray: OnScreen,
            session: observation.Session
        );
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture),
            expected: (0L, FixedQ4816.Zero)
        );
    }
    [Fact]
    public void APressAndReleaseBetweenTwoSteps_ReachesOneStepsRulesExactlyOnce() {
        using var fixture = Fixtures.FreshServer(definition: Destination(control: true));
        var observation = Observe(fixture: fixture);

        // Both arrive before the destination steps, as from a source stepping twice as fast.
        Forward(
            fixture: fixture,
            press: 1d,
            ray: OnScreen,
            session: observation.Session
        );
        Forward(
            fixture: fixture,
            press: 0d,
            ray: OnScreen,
            session: observation.Session
        );
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture).Press,
            expected: FixedQ4816.One
        );

        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture).Press,
            expected: FixedQ4816.Zero
        );
    }
    [Fact]
    public void AReleaseForward_ClearsWhatTheSessionPointedAndPressed() {
        using var fixture = Fixtures.FreshServer(definition: Destination(control: true));
        var observation = Observe(fixture: fixture);

        Forward(
            fixture: fixture,
            press: 1d,
            ray: OnScreen,
            session: observation.Session
        );
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture),
            expected: (1L, FixedQ4816.One)
        );

        Forward(
            fixture: fixture,
            press: 0d,
            ray: null,
            session: observation.Session
        );
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture),
            expected: (0L, FixedQ4816.Zero)
        );
    }
}
