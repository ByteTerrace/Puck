using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$pointer:&lt;seat&gt;:&lt;screenIndex&gt;:x|y|on</c> reads the seat's pointer ray, carried in its intent,
/// mapped through a <c>Simulation</c> screen's row in fixed point from document data alone. A hit reads the
/// source-normalized fractions and <c>on</c> 1; a miss, or no ray, reads <c>on</c> 0. A tape of pointer intents replays
/// to the same mapped hit and state hash, carrying every ray bit for bit, and an unknown seat or screen is refused by
/// name at compile time.
/// </summary>
public sealed class PointerWorldRuleFactLawTests {
    private const string OnRow = "pointerOn";
    private const string XRow = "pointerX";
    private const string YRow = "pointerY";

    // The fixture's screen 0 faces +z at (0, 1, 0), two units square: a ray down −z from z = 5 lands on it.
    private static SourceRay RayTo(float x, float y) => new(
        Direction: FixedVector3.FromVector3(value: -System.Numerics.Vector3.UnitZ),
        Origin: FixedVector3.FromVector3(value: new System.Numerics.Vector3(
            x: x,
            y: y,
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
    // The fixture document with its screen taking the simulation destination and one ungated rule copying the
    // three pointer facets of seat 1 on that screen into state every tick.
    private static WorldDefinition PointerDocument(string seat = "1", string screen = "0", SourceDestination? input = SourceDestination.Simulation, string facet = "x") {
        var document = Fixtures.BuildDocument();

        return document with {
            ScreensRaw = [.. document.Screens.Select(selector: row => row with {
                Route = (row.Route with { Input = input }),
            })],
            StateRaw = new WorldStateSection(World: [
                Row(
                    kind: CellKind.Fixed,
                    name: XRow
                ),
                Row(
                    kind: CellKind.Fixed,
                    name: YRow
                ),
                Row(
                    kind: CellKind.Int,
                    name: OnRow
                ),
            ]),
            Rules = [
                new WorldRule(
                Name: CellName.Parse(candidate: "pointer-copy"),
                Effects: [
                    new ActionEffect.SetState(
                        FromState: $"{WorldRuleFacts.PointerPrefix}{seat}:{screen}:{facet}",
                        State: XRow
                    ),
                    new ActionEffect.SetState(
                        FromState: $"{WorldRuleFacts.PointerPrefix}1:0:y",
                        State: YRow
                    ),
                    new ActionEffect.SetState(
                        FromState: $"{WorldRuleFacts.PointerPrefix}1:0:on",
                        State: OnRow
                    ),
                ]
            ),
            ],
        };
    }
    private static (long X, long Y, long On) Read(WorldFixture fixture) => (
        fixture.Row(name: XRow).Cells!.Single().Value.Raw,
        fixture.Row(name: YRow).Cells!.Single().Value.Raw,
        fixture.Row(name: OnRow).Cells!.Single().Value.Raw
    );
    private static void Join(WorldFixture fixture) => Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
        Principal: Principal.Seat(slot: 0),
        Slot: 0,
        IdentityName: null,
        WireProtocolKey: WorldProtocol.WireProtocolKey
    )).Accepted);
    private static IntentSubmission Pointing(ulong tick, SourceRay? ray) => new(
        Tick: tick,
        EntityIndex: 0,
        Intent: new PlayerIntent(
            Channels: default,
            SourceRay: ray
        ),
        Principal: Principal.Seat(slot: 0)
    );
    private static void Submit(WorldFixture fixture, SourceRay? ray) => fixture.Server.ApplyIntentSubmission(
        body: fixture.Server.Body(index: 0)!,
        submission: Pointing(
            ray: ray,
            tick: 0UL
        )
    );
    private static SourceHit Expected(SourceRay ray) => WorldScreenMappings.Normalized(screen: PointerDocument().Screens.Single()).MapRay(ray: ray);
    private static void AssertRefused(WorldDefinition definition, string refusal) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: refusal
        );
    }

    [Fact]
    public void ARayOnTheScreenReadsItsSourceFractions_AMissAndNoRayReadOff() {
        using var fixture = Fixtures.FreshServer(definition: PointerDocument());

        Join(fixture: fixture);

        // A hit a quarter of the way across the face and three quarters down it.
        var hit = RayTo(
            x: -0.5f,
            y: 0.5f
        );
        var expected = Expected(ray: hit);

        Assert.True(condition: expected.IsOnSource);

        Submit(
            fixture: fixture,
            ray: hit
        );
        fixture.Step();

        var (x, y, on) = Read(fixture: fixture);

        Assert.Equal(
            actual: (x, y, on),
            expected: (expected.Coordinate.X.Value, expected.Coordinate.Y.Value, 1L)
        );
        // body.channels echoes the ray the body integrated and the hit it maps to.
        Assert.Contains(
            actualString: fixture.Server.DescribeChannels(
                body: fixture.Server.Body(index: 0)!,
                bodyIndex: 0
            ),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"screen:0=on({FixedQ4816.FromRawBits(value: x)}, {FixedQ4816.FromRawBits(value: y)})"
        );
        // The glass bezel insets the image, so face point (1/4, 3/4) is source fraction ((1/4 − b) / (1 − 2b), (3/4 − b) / (1 − 2b)).
        Assert.InRange(
            actual: ((double)FixedQ4816.FromRawBits(value: x)),
            high: 0.235,
            low: 0.233
        );
        Assert.InRange(
            actual: ((double)FixedQ4816.FromRawBits(value: y)),
            high: 0.767,
            low: 0.765
        );

        // A miss: the same ray pointed away from the screen meets its plane behind its origin.
        Submit(
            fixture: fixture,
            ray: (hit with { Direction = FixedVector3.UnitZ })
        );
        fixture.Step();

        Assert.Equal(
            actual: Read(fixture: fixture),
            expected: (0L, 0L, 0L)
        );

        // Off the face and onto the bezel both read off, and so does a tick with no ray at all.
        foreach (var ray in new SourceRay?[] {
            RayTo(
                x: 3f,
                y: 1f
            ),
            RayTo(
                x: 0.99f,
                y: 1f
            ),
            null,
        }) {
            Submit(
                fixture: fixture,
                ray: hit
            );
            fixture.Step();
            Assert.Equal(
                actual: Read(fixture: fixture).On,
                expected: 1L
            );

            Submit(
                fixture: fixture,
                ray: ray
            );
            fixture.Step();
            Assert.Equal(
                actual: Read(fixture: fixture),
                expected: (0L, 0L, 0L)
            );
        }
    }
    [Fact]
    public void TheSeatFoldsItsPointerRayIntoTheIntentOnlyWhenBothHalvesArrivedThisTick() {
        var seat = new Puck.World.Client.SeatController();
        var ray = RayTo(
            x: 0.25f,
            y: 1.5f
        );

        seat.SetPointerOrigin(origin: ray.Origin);
        Assert.Null(@object: seat.HeldIntent().SourceRay);

        seat.SetPointerDirection(direction: ray.Direction);
        Assert.Equal(
            actual: seat.HeldIntent().SourceRay,
            expected: ray
        );

        // A held channel folds beside the ray rather than replacing it.
        seat.HoldChannel(
            controlId: "keyboard.w",
            ordinal: 0,
            scale: FixedQ4816.One
        );
        Assert.Equal(
            actual: seat.HeldIntent().SourceRay,
            expected: ray
        );

        // Consume-then-clear, like the sticks: the next tick carries no ray until both halves arrive again.
        seat.ClearAnalog();
        Assert.Null(@object: seat.HeldIntent().SourceRay);
    }
    [Fact]
    public void AnUnknownSeatOrScreenIsRefusedByName_ControlAWellFormedReadValidates() {
        AssertRefused(
            definition: PointerDocument(seat: "5"),
            refusal: nameof(WorldRuleRefusal.PointerMalformed)
        );
        AssertRefused(
            definition: PointerDocument(seat: "0"),
            refusal: nameof(WorldRuleRefusal.PointerMalformed)
        );
        AssertRefused(
            definition: PointerDocument(facet: "z"),
            refusal: nameof(WorldRuleRefusal.PointerMalformed)
        );
        AssertRefused(
            definition: PointerDocument(screen: "7"),
            refusal: nameof(WorldRuleRefusal.ScreenUnknown)
        );
        AssertRefused(
            definition: PointerDocument(input: SourceDestination.Presentation),
            refusal: nameof(WorldRuleRefusal.PointerMalformed)
        );
        AssertRefused(
            definition: PointerDocument(input: null),
            refusal: nameof(WorldRuleRefusal.PointerMalformed)
        );

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: PointerDocument(),
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void ARecordedTapeOfPointerIntentsReplaysToTheSameMappedHitAndStateHash() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");
        var rays = new SourceRay?[] {
            RayTo(
                x: -0.5f,
                y: 0.5f
            ),
            RayTo(
                x: 0.25f,
                y: 1.5f
            ),
            null,
            RayTo(
                x: 3f,
                y: 1f
            ),
        };

        // Two recordings on one tape store: the pointing one, and a control whose second ray lands elsewhere, so the
        // pointer's mapped state provably reaches the hash the verify compares.
        (WorldReplaySnapshot Tape, bool Match, (long, long, long) Live) Record(SourceRay?[] stream) {
            using var fixture = Fixtures.FreshServer(definition: PointerDocument());
            var transport = new LoopbackTransport(server: fixture.Server);
            var tape = new WorldReplayTape(
                stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
                liveServer: fixture.Server,
                profiles: fixture.Server.Profiles,
                transport: transport,
                engines: [],
                machineHostFactory: Fixtures.MachineHostFactory,
                addonHostFactory: static (_, _) => new NullAddonHost()
            );
            var name = $"pointer-{Guid.NewGuid():N}";

            Join(fixture: fixture);
            Assert.True(
                condition: tape.TryBeginRecording(
                    name: name,
                    refusal: out var refusal
                ),
                userMessage: $"refused to arm: {refusal}"
            );

            var live = default((long, long, long));

            for (var tick = 0; (tick < stream.Length); tick++) {
                transport.SubmitIntent(submission: Pointing(
                    ray: stream[tick],
                    tick: ((ulong)(tick + 1))
                ));
                fixture.Step();
                tape.NoteTick();

                if (tick == 1) {
                    live = Read(fixture: fixture);
                }
            }

            _ = tape.StopRecording();

            using var file = File.OpenRead(path: tape.PathFor(name: name));

            return (WorldReplaySnapshot.Read(stream: file), tape.Verify(name: name).Match, live);
        }

        var recorded = Record(stream: rays);
        var control = Record(stream: [rays[0], rays[0], rays[2], rays[3]]);
        var expected = Expected(ray: rays[1]!.Value);

        Assert.True(condition: expected.IsOnSource);
        Assert.Equal(
            actual: recorded.Live,
            expected: (expected.Coordinate.X.Value, expected.Coordinate.Y.Value, 1L)
        );
        // The tape carries every ray bit for bit, an absent one as absent.
        Assert.Equal(
            actual: recorded.Tape.Ticks.Select(selector: static tick => tick.Intents.Single().Intent.SourceRay),
            expected: rays
        );
        Assert.True(condition: recorded.Match);
        Assert.True(condition: control.Match);
        Assert.NotEqual(
            actual: control.Tape.RecordedAuthoritativeHashes[1],
            expected: recorded.Tape.RecordedAuthoritativeHashes[1]
        );
        Assert.Equal(
            actual: control.Tape.RecordedAuthoritativeHashes[0],
            expected: recorded.Tape.RecordedAuthoritativeHashes[0]
        );
    }
}
