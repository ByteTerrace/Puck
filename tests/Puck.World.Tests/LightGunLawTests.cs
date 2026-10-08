using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.HumbleGamingBrick.Forge;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using System.Numerics;
using System.Text.Json;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a seat applied to a <c>Simulation</c> screen aims that screen's machine's light gun where its pointer ray
/// maps, in fixed point from the screen row alone, through the one pad path every applied intent reaches a machine by.
/// A running program sees the gun: aimed at a lit pixel its infrared receiver reads light, aimed at a dark one, off the
/// screen, with no ray, or with no application it reads dark. A tape of pointer intents replays to the same machine
/// state and state hash.
/// </summary>
public sealed class LightGunLawTests {
    private const string CgbEngine = "gaming-brick";
    private const string LightRow = "gunLight";
    private const int MachineScreen = 8;
    private const int TicksPerAim = 4;

    // The machine screen faces +z at (0, 1, 3), two units wide: a ray down −z from z = 8 lands on it. Its source is the
    // probe cartridge's picture, white on the left half and black on the right.
    private static SourceRay RayTo(float x) => new(
        Direction: FixedVector3.FromVector3(value: -Vector3.UnitZ),
        Origin: FixedVector3.FromVector3(value: new Vector3(
            x: x,
            y: 1f,
            z: 8f
        ))
    );

    private static readonly SourceRay Dark = RayTo(x: 0.5f);
    private static readonly SourceRay Lit = RayTo(x: -0.5f);
    private static readonly SourceRay Miss = (Lit with { Direction = FixedVector3.UnitZ });

    private static string CartridgePath(TemporaryDirectory directory) {
        var path = directory.PathOf(name: "light-gun.gbc");

        File.WriteAllBytes(
            bytes: LightGunProbeCartridge.Create(),
            path: path
        );

        return path;
    }
    // The fixture document with one machine booting the probe cartridge on a Simulation screen, and the byte the
    // cartridge publishes its received light to mirrored into state every tick.
    private static WorldDefinition Document(string cartridgePath, SourceDestination? input = SourceDestination.Simulation) {
        var document = Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: LightRow),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0)
            )]
        )]);

        return document with {
            MachinesRaw = [.. document.Machines, new WorldMachine(
                "gallery",
                CgbEngine,
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = cartridgePath } }),
                Memory: [new WorldMachineMemory(
                    Name: "sensed",
                    Direction: WorldMachineMemoryDirection.Read,
                    Space: "bus",
                    Format: "u8",
                    Row: LightRow,
                    Address: LightGunProbeCartridge.SensedAddress,
                    Key: null,
                    Access: "inspect"
                )]
            )],
            ScreensRaw = [
                .. document.Screens,
                new WorldScreen(
                    Index: MachineScreen,
                    Origin: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: 3f
                    ),
                    Right: Vector3.UnitX,
                    Up: Vector3.UnitY,
                    HalfWidth: 1f,
                    HalfHeight: 0.9f,
                    HalfDepth: 0.03f,
                    Round: 0f,
                    Source: new WorldScreenSource.Machine(
                        Instance: "gallery",
                        Output: "video"
                    ),
                    Route: (new WorldScreenRoute(
                        Engageable: true,
                        EngageRadius: 100f
                    ) with { Input = input })
                ),
            ],
        };
    }
    private static WorldFixture Boot(WorldDefinition definition) => Fixtures.FreshServer(
        definition: definition,
        machineCatalog: TestMachines.Catalog()
    );
    private static Principal Join(WorldFixture fixture) {
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        return seat;
    }
    private static Principal Engage(WorldFixture fixture) {
        var seat = Join(fixture: fixture);

        Assert.True(condition: fixture.Server.Engagement.Compose(
            actingPrincipal: seat,
            entityIndex: seat.Index,
            exclusive: false,
            target: GrantSubject.Screen(index: MachineScreen),
            targetPrincipal: seat
        ));

        return seat;
    }
    private static IntentSubmission Pointing(ulong tick, SourceRay? ray) => new(
        EntityIndex: 0,
        Intent: new PlayerIntent(
            Channels: default,
            SourceRay: ray
        ),
        Principal: Principal.Seat(slot: 0),
        Tick: tick
    );
    private static long Sensed(WorldFixture fixture) => fixture.Row(name: LightRow).Cells!.Single().Value.Raw;
    // Holds one aim for a few ticks, long enough for the machine to draw under it and the mirror to carry the byte out.
    private static long Hold(WorldFixture fixture, SourceRay? ray) {
        for (var tick = 0; (tick < TicksPerAim); tick++) {
            fixture.Server.ApplyIntentSubmission(
                body: fixture.Server.Body(index: 0)!,
                submission: Pointing(
                    ray: ray,
                    tick: 0UL
                )
            );
            fixture.Step();
        }

        return Sensed(fixture: fixture);
    }
    private static byte[] MachineState(WorldFixture fixture) =>
        Assert.Single(collection: ((IWorldMachineCheckpointHost)fixture.Server.Machines).CaptureCheckpoint().Instances).RuntimeState;

    [Fact]
    public void AGunAimedAtALitPixelReadsLightInTheRunningProgram_ADarkPixelAMissNoRayAndNoApplicationReadDark() {
        using var directory = new TemporaryDirectory(prefix: "puck-light-gun-");
        using var fixture = Boot(definition: Document(cartridgePath: CartridgePath(directory: directory)));
        var seat = Engage(fixture: fixture);

        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: Lit
            ),
            expected: 1L
        );
        // screen.state's read-back reports the tick's aim.
        Assert.Equal(
            actual: fixture.Server.Engagement.PointerOn(screenIndex: MachineScreen),
            expected: fixture.Server.Engagement.Aim(
                ray: Lit,
                screenIndex: MachineScreen
            )
        );
        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: Dark
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: Miss
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: fixture.Server.Engagement.PointerOn(screenIndex: MachineScreen),
            expected: MachinePointer.Off
        );
        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: null
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: Lit
            ),
            expected: 1L
        );

        // The pointer reaches the machine only through the seat's control application: dissolved, the same aim reads
        // dark.
        Assert.Equal(
            actual: fixture.Server.Engagement.Dissolve(
                actingPrincipal: seat,
                entityIndex: seat.Index,
                targetPrincipal: seat
            ),
            expected: ControlOutcome.Dissolved
        );
        Assert.Equal(
            actual: Hold(
                fixture: fixture,
                ray: Lit
            ),
            expected: 0L
        );
    }
    [Fact]
    public void TheAimIsTheScreenRowsNormalizedHit_AndOnlyASimulationScreenAimsAtAll() {
        using var directory = new TemporaryDirectory(prefix: "puck-light-gun-");
        var document = Document(cartridgePath: CartridgePath(directory: directory));
        using var fixture = Boot(definition: document);
        var screens = document.Screens;
        var expected = WorldScreenMappings.Normalized(screen: screens.Single(predicate: static screen => (screen.Index == MachineScreen))).MapRay(ray: Lit);
        var aim = fixture.Server.Engagement.Aim(
            ray: Lit,
            screenIndex: MachineScreen
        );

        Assert.True(condition: expected.IsOnSource);
        Assert.Equal(
            actual: aim,
            expected: new MachinePointer(
                x: ((ushort)expected.Coordinate.X.Value),
                y: ((ushort)expected.Coordinate.Y.Value)
            )
        );
        // The lit half is the left half of the picture; the dark aim lands on the right half.
        Assert.True(condition: (aim.Column(width: 160) < LightGunProbeCartridge.DarkColumn));
        Assert.True(condition: (fixture.Server.Engagement.Aim(
            ray: Dark,
            screenIndex: MachineScreen
        ).Column(width: 160) >= LightGunProbeCartridge.DarkColumn));

        foreach (var ray in new SourceRay?[] { Miss, RayTo(x: 0.99f), null }) {
            Assert.Equal(
                actual: fixture.Server.Engagement.Aim(
                    ray: ray,
                    screenIndex: MachineScreen
                ),
                expected: MachinePointer.Off
            );
        }

        // An index the document lacks maps no aim, and neither does a screen whose route input is not Simulation: the
        // aim reads the running document's row, so that screen is a document of its own.
        Assert.Equal(
            actual: fixture.Server.Engagement.Aim(
                ray: Lit,
                screenIndex: 99
            ),
            expected: MachinePointer.Off
        );

        using var presentation = Boot(definition: Document(
            cartridgePath: CartridgePath(directory: directory),
            input: SourceDestination.Presentation
        ));

        Assert.Equal(
            actual: presentation.Server.Engagement.Aim(
                ray: Lit,
                screenIndex: MachineScreen
            ),
            expected: MachinePointer.Off
        );
    }
    [Fact]
    public void ARecordedTapeOfPointerIntentsReplaysToTheSameMachineStateAndStateHash() {
        using var directory = new TemporaryDirectory(prefix: "puck-light-gun-");
        var document = Document(cartridgePath: CartridgePath(directory: directory));
        var aims = new SourceRay?[] { Lit, Lit, Lit, Lit, Dark, Dark, Dark, Dark, Lit, Lit, Lit, Lit };
        var machineCatalog = TestMachines.Catalog();

        (WorldReplaySnapshot Tape, WorldReplayVerdict Verdict, byte[] Machine, long Sensed) Record(SourceRay?[] stream) {
            using var fixture = Fixtures.FreshServer(
                definition: document,
                machineCatalog: machineCatalog
            );
            var transport = new LoopbackTransport(server: fixture.Server);
            var tape = new WorldReplayTape(
                stateRoot: new WorldStateRoot(path: directory.RootPath),
                liveServer: fixture.Server,
                profiles: fixture.Server.Profiles,
                transport: transport,
                engines: machineCatalog.Engines.Values,
                machineHostFactory: Fixtures.MachineHostFactory,
                addonHostFactory: static (_, _) => new NullAddonHost()
            );
            var name = $"light-gun-{Guid.NewGuid():N}";

            var seat = Join(fixture: fixture);

            Assert.True(
                condition: tape.TryBeginRecording(
                    name: name,
                    refusal: out var refusal
                ),
                userMessage: $"refused to arm: {refusal}"
            );
            // The application rides the tape as the ordinary command, so the replay composes it too.
            transport.SubmitCommand(command: new WorldCommand.ComposeControl(
                EntityIndex: seat.Index,
                Exclusive: false,
                Principal: seat,
                Target: GrantSubject.Screen(index: MachineScreen),
                TargetPrincipal: seat
            ));

            for (var tick = 0; (tick < stream.Length); tick++) {
                transport.SubmitIntent(submission: Pointing(
                    ray: stream[tick],
                    tick: ((ulong)(tick + 1))
                ));
                fixture.Step();
                tape.NoteTick();
            }

            _ = tape.StopRecording();

            var machine = MachineState(fixture: fixture);
            var sensed = Sensed(fixture: fixture);

            using var file = File.OpenRead(path: tape.PathFor(name: name));

            return (WorldReplaySnapshot.Read(stream: file), tape.Verify(name: name).Primary, machine, sensed);
        }

        var recorded = Record(stream: aims);
        var again = Record(stream: aims);
        var control = Record(stream: [.. aims.Select(selector: static _ => ((SourceRay?)Dark))]);

        Assert.True(
            condition: recorded.Verdict.Match,
            userMessage: recorded.Verdict.Describe()
        );
        Assert.True(
            condition: control.Verdict.Match,
            userMessage: control.Verdict.Describe()
        );
        Assert.Equal(
            actual: recorded.Sensed,
            expected: 1L
        );
        Assert.Equal(
            actual: control.Sensed,
            expected: 0L
        );
        // The same intents drive the machine to the same state; a different aim drives it elsewhere, and the tape's
        // authoritative hash sees the difference through the mirrored byte.
        Assert.Equal(
            actual: again.Machine,
            expected: recorded.Machine
        );
        Assert.NotEqual(
            actual: control.Machine,
            expected: recorded.Machine
        );
        Assert.Equal(
            actual: again.Tape.RecordedAuthoritativeHashes,
            expected: recorded.Tape.RecordedAuthoritativeHashes
        );
        Assert.NotEqual(
            actual: control.Tape.RecordedAuthoritativeHashes,
            expected: recorded.Tape.RecordedAuthoritativeHashes
        );
    }
}
