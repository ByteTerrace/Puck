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

        private readonly TemporaryDirectory m_bootState;

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
        public WorldInstance Boot { get; }
        public WorldFaceFrame Glass { get; }
        public int GlassScreen { get; }
        public WorldInstanceHost Host { get; }

        public void Dispose() {
            Host.Dispose();
            m_bootState.Dispose();
            m_hostState.Dispose();
            m_files.Dispose();
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
            Boot.Server.EnqueueIntent(submission: new IntentSubmission(
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
        // One host step as the desktop runs it: the boot world steps, settles its screen sessions, and the other
        // instances step.
        public void Step(bool forwards = true) {
            Boot.Server.Advance(stepTicks: PortalStep);
            Host.SettleBootScreenSessions(forwards: forwards);
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
    public void AWorldReplayingItsInput_ForwardsNothingThroughItsPortals() {
        long OnAfterClicks(bool forwards) {
            using var scene = new PortalScene();

            Assert.True(condition: scene.Engage());

            for (var tick = 0; (tick < 3); tick++) {
                scene.Point(
                    press: 1d,
                    throughGlass: true
                );
                scene.Step(forwards: forwards);
            }

            return Cell(
                row: OnRow,
                server: scene.Beyond
            );
        }

        Laws.RefusalWithControl(
            lawId: "portal.replay-forwards-nothing",
            deniedOutcome: () => (OnAfterClicks(forwards: false) == 1L),
            controlOutcome: () => (OnAfterClicks(forwards: true) == 1L)
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
