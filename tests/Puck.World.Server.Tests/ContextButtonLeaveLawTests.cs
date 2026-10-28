using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: the context button that engages a screen also leaves it. A seat captured on a screen whose route names an
/// engage channel dissolves its applications on that channel's rising edge, and holding the button after leaving does
/// not press again; the validator refuses an engage channel the route's kit plays as a pad button, since that press
/// would leave the screen it was meant to play.
/// </summary>
public sealed class ContextButtonLeaveLawTests {
    private const string Interact = "interact";
    private const string PadKitName = "cabinet";

    private static readonly FixedQ4816 Full = FixedQ4816.FromInteger(value: 1);

    // The fixture document with a binary context channel, one pad-bearing kit, and its first screen engageable on the
    // context channel through that kit.
    private static WorldDefinition Document(bool padPlaysInteract = false) {
        var definition = Fixtures.BuildDocument();
        var seatKit = definition.Kits[0];
        var pad = new Dictionary<string, WorldPadElement>(comparer: StringComparer.Ordinal) {
            ["forward"] = WorldPadElement.LeftStickY,
        };

        if (padPlaysInteract) {
            pad[Interact] = WorldPadElement.South;
        }

        return (definition with {
            ChannelsRaw = [.. definition.Channels, new WorldChannel(
                Composition: true,
                Name: Interact,
                Shape: ChannelShape.Binary
            )],
            KitRowsRaw = [seatKit, seatKit with {
                Name = PadKitName,
                PadRaw = pad,
            }],
            ScreensRaw = [definition.Screens[0] with {
                Route = new WorldScreenRoute(
                    Engageable: true,
                    EngageRadius: 3f,
                    EngageChannel: Interact,
                    Kit: PadKitName
                ),
            }],
        });
    }
    // One tick with seat 1's body holding the context channel at the given value.
    private static void Step(WorldFixture fixture, int interactOrdinal, FixedQ4816 value) {
        fixture.Server.Body(index: 0)!.SubmitIntent(intent: default(PlayerIntent).WithChannel(
            ordinal: interactOrdinal,
            value: value
        ));
        fixture.Step();
    }

    [Fact]
    public void TheEngageChannelsPressLeavesTheScreenItCapturedAndAHeldPressDoesNotRepeat() {
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

        Assert.True(condition: fixture.Server.Population.Channels.TryGetOrdinal(name: Interact, ordinal: out var interactOrdinal));

        // Released: the seat stays captured.
        Step(fixture: fixture, interactOrdinal: interactOrdinal, value: FixedQ4816.Zero);
        Assert.True(condition: fixture.Server.Body(index: 0)!.Engaged);

        // Pressed: the rising edge leaves the screen, back to the own-body default.
        Step(fixture: fixture, interactOrdinal: interactOrdinal, value: Full);
        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: 0),
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: seat))
        );
        Assert.False(condition: fixture.Server.Body(index: 0)!.Engaged);

        // Held: no new edge, and a seat that engages nothing has nothing to leave.
        Step(fixture: fixture, interactOrdinal: interactOrdinal, value: Full);
        Assert.False(condition: fixture.Server.Body(index: 0)!.Engaged);
    }
    [Fact]
    public void AContextPressUsesTheOwnersLiveMappingAndRecordsItsConsumedActivation() {
        var basis = Document();
        var definition = basis with {
            SeatModesRaw = [new("focus", [new("none"), new("panel")], "panel")],
            InputModes = new(basis.Channels.Where(predicate: row => (row.Name != Interact)).Select(selector: row => row.Name).ToArray(), [
                new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "none", [
                    new("none", [], ["none"]), new("panel", [Interact], ["panel"]),
                ]),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat();
        fixture.Step();
        var principal = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.Engagement.Compose(0, GrantSubject.Screen(index: definition.Screens[0].Index), true, principal, principal));
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var logical, name: Interact));
        WorldInputSource[] sources = [new(0, logical, WorldInputContributionKind.Level, "key:context")];
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: principal, sources: sources);

        Assert.False(condition: fixture.Server.Population.Channels.TryGetOrdinal(name: Interact, ordinal: out _));
        Assert.True(condition: fixture.Server.Body(index: 0)!.Channels.TryGetOrdinal(name: Interact, ordinal: out _));
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.NextInputTick;

        Assert.True(condition: producer.Observe(0, Full.Value, tick, default, 71));
        fixture.Server.EnqueueIntent(submission: new(tick, 0, default, principal, Input: producer.Publish(tick)));
        fixture.Step();
        Assert.False(condition: fixture.Server.Body(index: 0)!.Engaged);
        Assert.Contains(71UL, owner.LastSwitch.ConsumedActivations.ToArray());
        Assert.False(condition: owner.TryHeld(channel: logical, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(71UL, cancelled.Event.Activation);
    }
    [Fact]
    public void AnEngageChannelTheRoutesKitPlaysAsAPadButtonIsRefusedByName() {
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(),
            neighbours: null,
            reason: out var cleanReason
        ), userMessage: cleanReason);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(padPlaysInteract: true),
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "engageChannel 'interact' is a pad button of kit 'cabinet'"
        );
    }
}
