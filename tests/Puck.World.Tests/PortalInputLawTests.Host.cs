using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PortalInputLawTests {
    private const string BeyondFixture = "tests/Puck.World.Canaries/portal-window/beyond.world.json";
    private const string PortalFixture = "tests/Puck.World.Canaries/portal-window/fixture.world.json";
    private const string XRow = "portalX";

    private static readonly ulong PortalStep = EngineTicks.PerRate(ratePerSecond: 30u);

    private static WorldDefinition Load(string relative) => WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: Path.Combine(
        path1: AuthoredGameFixtures.Root,
        path2: relative
    )));
    // The boot row over a real file, so the destination it references resolves beside it.
    private static (WorldInstance Instance, TemporaryDirectory StateDirectory) FileBackedRow(string name, string path, WorldDefinition definition) {
        var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var stateDirectory = new TemporaryDirectory(prefix: $"puck-portal-input-{name}-");
        var server = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: name,
            machines: machines,
            narrationSink: new WorldConsoleNarrationSink(),
            population: new WorldPopulation(definition: definition),
            profiles: new WorldOwnedWorlds(
                directory: stateDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        return (new WorldInstance(
            documentOrigin: new WorldFileOrigin(resolvedPath: path),
            federation: new WorldFederationIdentity(
                Authenticator: new InertAuthenticator(),
                Subject: server.AuthorityIdentity
            ),
            link: new LoopbackTransport(server: server),
            name: name,
            origin: () => path,
            ownedMachines: machines,
            server: server
        ), stateDirectory);
    }
    private static long Cell(WorldServer server, string row) => WorldDefinitionRows.FindStateRow(
        name: row,
        rows: server.Definition.State
    )!.Cells!.Single().Value.Raw;

    // The portal-window pair on disk: the boot world's door shows 'beyond' through its glass, and 'beyond' gains a
    // simulation screen facing its counterpart arch, the boot world's channels, an admission row granting its session
    // Control over that screen (when asked), and one rule copying what any participant points and presses there.
    private sealed class PortalScene : IDisposable {
        private readonly TemporaryDirectory m_files = new(prefix: "puck-portal-input-files-");
        private readonly TemporaryDirectory m_hostState = new(prefix: "puck-portal-input-host-");
        private readonly TemporaryDirectory m_tapes = new(prefix: "puck-portal-input-tapes-");

        private readonly TemporaryDirectory m_bootState;

        private ulong m_steps;

        public PortalScene(bool control = true) {
            var boot = Load(relative: PortalFixture);
            var beyond = Load(relative: BeyondFixture);

            Assert.True(condition: WorldFaceCatalog.For(definition: beyond).TryFind(
                placementId: "arch",
                faceName: "glass",
                out var arch
            ));

            var archFrame = arch.Frame;
            var screenOrigin = (archFrame.Origin + (archFrame.Normal * Puck.Maths.FixedQ4816.FromInteger(value: 2))).ToVector3();

            beyond = beyond with {
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
                ChannelsRaw = boot.ChannelsRaw,
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
                        new ActionEffect.SetState(
                            FromState: $"{WorldRuleFacts.PointerPrefix}any:0:x",
                            State: XRow
                        ),
                    ]
                )],
                // Facing back at the arch: a ray leaving the counterpart glass lands on its front.
                ScreensRaw = [new WorldScreen(
                    HalfDepth: 0.03f,
                    HalfHeight: 1f,
                    HalfWidth: 1.2f,
                    Index: 0,
                    Origin: screenOrigin,
                    Right: (-archFrame.Right).ToVector3(),
                    Round: 0f,
                    Route: (new WorldScreenRoute(
                        EngageRadius: 0f,
                        Engageable: false
                    ) with { Input = SourceDestination.Simulation }),
                    Source: new WorldScreenSource.None(),
                    Up: archFrame.Up.ToVector3()
                )],
                StateRaw = new WorldStateSection(World: [
                    Row(
                        kind: CellKind.Int,
                        name: OnRow
                    ),
                    Row(
                        kind: CellKind.Fixed,
                        name: PressRow
                    ),
                    Row(
                        kind: CellKind.Fixed,
                        name: XRow
                    ),
                ]),
            };

            var bootPath = Path.GetFullPath(path: Path.Combine(
                path1: m_files.RootPath,
                path2: "portal.world.json"
            ));

            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: beyond),
                path: Path.Combine(
                    path1: m_files.RootPath,
                    path2: WorldDocumentName.DocumentFile(name: "beyond")
                )
            );
            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: boot),
                path: bootPath
            );

            var glass = WorldFaceCatalog.For(definition: boot).Rows.Single(predicate: static row => (row.Source is WorldScreenSource.Session));

            GlassScreen = glass.ScreenIndex;
            Glass = glass.Frame;
            Host = new WorldInstanceHost(
                admitsSpawn: true,
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_hostState.RootPath)
            );

            var (row, bootState) = FileBackedRow(
                definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bootPath)),
                name: WorldInstanceHost.BootInstanceName,
                path: bootPath
            );

            m_bootState = bootState;
            Boot = row;
            Host.AdmitBoot(row: row);
            Assert.True(condition: Boot.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 0),
                Slot: 0,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }

        public WorldServer Beyond {
            get {
                var session = Host.ScreenSession(
                    instanceName: WorldInstanceHost.BootInstanceName,
                    screenIndex: GlassScreen
                )!;

                Assert.True(
                    condition: Host.TryGet(
                    instance: out var beyond,
                    name: session.InstanceName!
                ),
                    userMessage: session.Refusal
                );

                return beyond!.Server;
            }
        }
        // The destination's row.
        public WorldInstance BeyondRow {
            get {
                Assert.True(condition: Host.TryGet(
                    instance: out var row,
                    name: Host.ScreenSession(
                        instanceName: WorldInstanceHost.BootInstanceName,
                        screenIndex: GlassScreen
                    )!.InstanceName!
                ));

                return row!;
            }
        }
        public WorldInstance Boot { get; }
        public WorldFaceFrame Glass { get; }
        public int GlassScreen { get; }
        public WorldInstanceHost Host { get; }
        // The boot world's replay tape, once armed.
        public WorldReplayTape? Tape { get; private set; }

        public void Dispose() {
            Host.Dispose();
            m_tapes.Dispose();
            m_bootState.Dispose();
            m_hostState.Dispose();
            m_files.Dispose();
        }
        // A replay tape over a row's own link, as a host arms one.
        public WorldReplayTape TapeOver(WorldInstance row) => new(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: row.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: row.Server.Profiles,
            stateRoot: new WorldStateRoot(path: Path.Combine(
                path1: m_tapes.RootPath,
                path2: row.Name
            )),
            transport: ((LoopbackTransport)row.Link)
        );
        // Arms the boot world's tape, which every later step runs under.
        public WorldReplayTape ArmBootTape() => (Tape = TapeOver(row: Boot));
        // Counts the session input the destination's link carries from now on.
        public Func<int> CountForwards() {
            var count = 0;

            ((LoopbackTransport)BeyondRow.Link).IntentTap = submission => count += ((submission.Principal.Kind == PrincipalKind.Session)
                ? 1
                : 0);

            return () => count;
        }
        public bool Engage() => Boot.Server.Engagement.Compose(
            actingPrincipal: Principal.Seat(slot: 0),
            entityIndex: 0,
            exclusive: false,
            target: GrantSubject.Screen(index: GlassScreen),
            targetPrincipal: Principal.Seat(slot: 0)
        );
        public void Disengage() => _ = Boot.Server.Engagement.Dissolve(
            actingPrincipal: Principal.Seat(slot: 0),
            entityIndex: 0,
            targetPrincipal: Principal.Seat(slot: 0)
        );
        // Seat 1 points at the glass from its outward side, straight through it, or off to the side of it, pressing
        // 'forward' as asked.
        public void Point(bool throughGlass, double press) {
            var aside = (throughGlass
                ? FixedQ4816.Zero
                : (Glass.HalfWidth + FixedQ4816.One));
            var channels = new ChannelValues();

            channels[0] = FixedQ4816.FromDouble(value: press);
            Boot.Link.SubmitIntent(submission: new IntentSubmission(
                EntityIndex: 0,
                Intent: new PlayerIntent(
                    Channels: channels,
                    SourceRay: new SourceRay(
                        Direction: -Glass.Normal,
                        Origin: ((Glass.Origin + (Glass.Normal * FixedQ4816.FromInteger(value: 3))) + (Glass.Right * aside))
                    )
                ),
                Principal: Principal.Seat(slot: 0),
                Tick: Boot.Server.NextInputTick
            ));
        }
        // One host step as the desktop runs it: the boot world steps through the step shell under its tape, settles its
        // screen sessions, and the other instances step.
        public void Step() {
            var context = new FixedStepContext(
                ElapsedTicks: ((m_steps + 1UL) * PortalStep),
                StepTicks: PortalStep,
                Tick: m_steps
            );

            m_steps = WorldServerStepShell.Step(
                context: in context,
                publishTick: static _ => { },
                server: Boot.Server,
                tape: Tape
            );
            Host.SettleBootScreenSessions(stepped: true);
            Host.StepInstances(masterDeltaTicks: PortalStep);
        }
    }

    [Fact]
    public void AnEngagedSeatsClickThroughTheGlass_ReachesTheDestinationsRules() {
        FixedQ4816 PressAfterAClick(bool engaged) {
            using var scene = new PortalScene();

            if (engaged) {
                Assert.True(condition: scene.Engage());
            }

            for (var tick = 0; (tick < 3); tick++) {
                scene.Point(
                    press: 1d,
                    throughGlass: true
                );
                scene.Step();
            }

            return FixedQ4816.FromRawBits(value: Cell(
                row: PressRow,
                server: scene.Beyond
            ));
        }

        Laws.RefusalWithControl(
            lawId: "portal.click-needs-engagement",
            deniedOutcome: () => (PressAfterAClick(engaged: false) == FixedQ4816.One),
            controlOutcome: () => (PressAfterAClick(engaged: true) == FixedQ4816.One)
        );
    }
    [Fact]
    public void TheDoorMapsTheGlassOntoTheCounterpart_AndARayOffTheGlassNeverReachesIt() {
        (long On, FixedQ4816 X) AfterPointing(bool throughGlass) {
            using var scene = new PortalScene();

            Assert.True(condition: scene.Engage());

            for (var tick = 0; (tick < 3); tick++) {
                scene.Point(
                    press: 1d,
                    throughGlass: throughGlass
                );
                scene.Step();
            }

            return (
                Cell(
                    row: OnRow,
                    server: scene.Beyond
                ),
                FixedQ4816.FromRawBits(value: Cell(
                    row: XRow,
                    server: scene.Beyond
                ))
            );
        }

        var through = AfterPointing(throughGlass: true);

        Assert.Equal(
            actual: through.On,
            expected: 1L
        );
        // The glass centre maps onto the counterpart's centre, straight on to the screen's centre.
        Assert.True(
            condition: (FixedQ4816.Abs(value: (through.X - FixedQ4816.FromDouble(value: 0.5d))) <= FixedQ4816.FromDouble(value: 0.01d)),
            userMessage: $"the mapped ray landed at x {through.X}, not the screen's centre"
        );
        Assert.Equal(
            actual: AfterPointing(throughGlass: false).On,
            expected: 0L
        );
    }
    [Fact]
    public void AWorldReplayingItsInput_ForwardsNothingThroughItsPortals_ItsLastReplayedTickIncluded() {
        using var scene = new PortalScene();
        var sent = scene.CountForwards();
        var tape = scene.ArmBootTape();
        var seat = Principal.Seat(slot: 0);

        Assert.True(
            condition: tape.TryBeginRecording(
                name: "clicks",
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        scene.Boot.Link.SubmitCommand(command: new WorldCommand.ComposeControl(
            EntityIndex: 0,
            Exclusive: false,
            Principal: seat,
            Target: GrantSubject.Screen(index: scene.GlassScreen),
            TargetPrincipal: seat
        ));

        for (var tick = 0; (tick < 3); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        scene.Boot.Link.SubmitCommand(command: new WorldCommand.DissolveControl(
            EntityIndex: 0,
            Principal: seat,
            TargetPrincipal: seat
        ));
        scene.Step();
        _ = tape.StopRecording();

        // Live, the clicks reached the destination.
        var live = sent();

        Assert.True(
            condition: (live > 0),
            userMessage: "the recorded clicks never reached the destination live"
        );

        // Driven back to its last click, the boot world re-engages and presses again, and sends none of it: the
        // destination is not replaying with it, even on the tick whose step ends the drive.
        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: null,
                name: "clicks",
                refusal: out refusal,
                toTick: 3
            ),
            userMessage: refusal
        );

        for (var tick = 0; ((tick < 8) && (tape.Mode == WorldReplayMode.Replaying)); tick++) {
            scene.Step();
        }

        Assert.Equal(
            actual: (tape.Mode, (sent() - live)),
            expected: (WorldReplayMode.Idle, 0)
        );
    }
    [Fact]
    public void ADestinationRecordingItsInput_RecordsWhatAPortalForwardsIt() {
        using var scene = new PortalScene();
        var beyond = scene.BeyondRow;
        var tape = scene.TapeOver(row: beyond);

        beyond.Tape = tape;
        Assert.True(
            condition: tape.TryBeginRecording(
                name: "beyond",
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        Assert.True(condition: scene.Engage());

        for (var tick = 0; (tick < 3); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        _ = tape.StopRecording();

        using var stream = File.OpenRead(path: tape.PathFor(name: "beyond"));
        var recorded = WorldReplaySnapshot.Read(stream: stream).Ticks.Sum(selector: static tick => tick.Intents.Count(predicate: static intent => (intent.Principal.Kind == PrincipalKind.Session)));

        Assert.Equal(
            actual: FixedQ4816.FromRawBits(value: Cell(
                row: PressRow,
                server: beyond.Server
            )),
            expected: FixedQ4816.One
        );
        Assert.True(
            condition: (recorded > 0),
            userMessage: "the destination's tape holds none of the input the portal forwarded it"
        );
    }

    // Records the destination's own tape while the boot world clicks through the glass three times, then starts driving
    // it back, with the boot world still engaged.
    private static WorldReplayTape RecordAndDriveBeyond(PortalScene scene) {
        var beyond = scene.BeyondRow;
        var tape = scene.TapeOver(row: beyond);

        beyond.Tape = tape;
        Assert.True(
            condition: tape.TryBeginRecording(
                name: "beyond",
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        Assert.True(condition: scene.Engage());

        for (var tick = 0; (tick < 3); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        _ = tape.StopRecording();
        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: null,
                name: "beyond",
                refusal: out refusal,
                toTick: null
            ),
            userMessage: refusal
        );

        return tape;
    }

    [Fact]
    public void ADestinationDrivenBackThroughItsTape_LeavesNoRecordedSessionHoldingItsPress() {
        using var scene = new PortalScene();
        var tape = RecordAndDriveBeyond(scene: scene);

        scene.Disengage();

        for (var tick = 0; ((tick < 8) && (tape.Mode == WorldReplayMode.Replaying)); tick++) {
            scene.Step();
        }

        // The drive restored the recorded session and replayed its press; once it ends, that session's viewer is not
        // watching, so it ends and nothing it pressed stays held.
        scene.Step();
        scene.Step();

        Assert.Equal(
            actual: (tape.Mode, Cell(
                row: OnRow,
                server: scene.Beyond
            ), Cell(
                row: PressRow,
                server: scene.Beyond
            )),
            expected: (WorldReplayMode.Idle, 0L, 0L)
        );
    }
    [Fact]
    public void AReleaseOwedToADestinationDrivingItsTape_WaitsForTheDriveToEnd() {
        using var scene = new PortalScene();
        var tape = RecordAndDriveBeyond(scene: scene);

        // While the destination drives, the boot world's ray leaves the glass: the release it owes cannot reach a link
        // that drops live input.
        for (var tick = 0; ((tick < 8) && (tape.Mode == WorldReplayMode.Replaying)); tick++) {
            scene.Point(
                press: 0d,
                throughGlass: false
            );
            scene.Step();
        }

        Assert.Equal(
            actual: tape.Mode,
            expected: WorldReplayMode.Idle
        );

        var sent = scene.CountForwards();

        scene.Point(
            press: 0d,
            throughGlass: false
        );
        scene.Step();

        Assert.Equal(
            actual: sent(),
            expected: 1
        );
    }
    [Fact]
    public void APausedDestination_IsSentNothing_UntilItStepsAgain() {
        using var scene = new PortalScene();
        var sent = scene.CountForwards();
        var beyond = scene.BeyondRow;

        Assert.True(condition: scene.Engage());
        beyond.IsPaused = true;

        for (var tick = 0; (tick < 4); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        Assert.Equal(
            actual: sent(),
            expected: 0
        );

        beyond.IsPaused = false;

        for (var tick = 0; (tick < 2); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        Assert.Equal(
            actual: ((sent() > 0), Cell(
                row: OnRow,
                server: beyond.Server
            )),
            expected: (true, 1L)
        );
    }
    [Fact]
    public void Disengaging_SendsARelease_SoTheDestinationReadsNothingPointedOrPressed() {
        using var scene = new PortalScene();

        Assert.True(condition: scene.Engage());

        for (var tick = 0; (tick < 3); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        Assert.Equal(
            actual: Cell(
                row: OnRow,
                server: scene.Beyond
            ),
            expected: 1L
        );

        scene.Disengage();

        for (var tick = 0; (tick < 3); tick++) {
            scene.Point(
                press: 1d,
                throughGlass: true
            );
            scene.Step();
        }

        Assert.Equal(
            actual: (Cell(
                row: OnRow,
                server: scene.Beyond
            ), Cell(
                row: PressRow,
                server: scene.Beyond
            )),
            expected: (0L, 0L)
        );
    }
    [Fact]
    public void TheSameClicksTwice_LeaveTheDestinationIdentical() {
        byte[] StateAfter(bool throughGlass) {
            using var scene = new PortalScene();

            Assert.True(condition: scene.Engage());

            for (var tick = 0; (tick < 4); tick++) {
                scene.Point(
                    press: (((tick % 2) == 0)
                        ? 1d
                        : 0d),
                    throughGlass: throughGlass
                );
                scene.Step();
            }

            return WorldDefinitionSerialization.Serialize(definition: scene.Beyond.Definition);
        }

        Assert.Equal(
            actual: StateAfter(throughGlass: true),
            expected: StateAfter(throughGlass: true)
        );
        Assert.NotEqual(
            actual: StateAfter(throughGlass: false),
            expected: StateAfter(throughGlass: true)
        );
    }
}
