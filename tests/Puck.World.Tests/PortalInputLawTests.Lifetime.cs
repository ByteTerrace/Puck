using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class PortalInputLawTests {
    // A destination document: the fixture world, admitting a viewer's session with its whole-world view, or admitting
    // nobody.
    private static WorldDefinition Observable(bool admits) => (Fixtures.BuildDocument() with {
        Admission = (admits
            ? [new WorldAdmissionEntry(
                Algorithm: string.Empty,
                Disclosure: WorldDisclosureTier.Replica,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(
                    Budget: 64,
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.All
                )],
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                PublicKey: string.Empty,
                Subject: null
            )]
            : []),
    });

    // A hub whose screens 1 and 2 each observe an ephemeral destination through a session, the destinations written beside
    // it: d1, d2 and d3 admit a viewer, 'closed' admits nobody.
    private sealed class HubScene : IDisposable {
        private readonly TemporaryDirectory m_files = new(prefix: "puck-portal-hub-files-");
        private readonly TemporaryDirectory m_hostState = new(prefix: "puck-portal-hub-host-");

        private readonly TemporaryDirectory m_bootState;

        public HubScene(string first, string second) {
            foreach (var (name, admits) in ((IEnumerable<(string, bool)>)[("d1", true), ("d2", true), ("d3", true), ("closed", false)])) {
                File.WriteAllBytes(
                    bytes: WorldDefinitionSerialization.Serialize(definition: Observable(admits: admits)),
                    path: Path.Combine(
                        path1: m_files.RootPath,
                        path2: WorldDocumentName.DocumentFile(name: name)
                    )
                );
            }

            var hub = Fixtures.BuildDocument();
            var template = hub.Screens.Single();

            Screen = index => (template with {
                Index = index,
                Source = new WorldScreenSource.Session(Destination: ((index == 1)
                    ? first
                    : second)),
            });
            hub = hub with {
                Destinations = [.. ((IEnumerable<string>)["d1", "d2", "d3", "closed"]).Select(selector: static name => new WorldDestination(
                    Durability: WorldDestinationDurability.Ephemeral,
                    Name: SafeName.Parse(candidate: name),
                    Reference: name
                ))],
                References = [.. ((IEnumerable<string>)["d1", "d2", "d3", "closed"]).Select(selector: static name => new WorldReference(
                    Document: name,
                    Name: SafeName.Parse(candidate: name)
                ))],
                ScreensRaw = [template, Screen(arg: 1), Screen(arg: 2)],
            };

            var hubPath = Path.GetFullPath(path: Path.Combine(
                path1: m_files.RootPath,
                path2: "hub.world.json"
            ));

            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: hub),
                path: hubPath
            );
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
                definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: hubPath)),
                name: WorldInstanceHost.BootInstanceName,
                path: hubPath
            );

            m_bootState = bootState;
            Boot = row;
            Host.AdmitBoot(row: row);
        }

        public WorldInstance Boot { get; }
        public WorldInstanceHost Host { get; }
        // The hub's session screen at an index, observing the destination it was built with.
        public Func<int, WorldScreen> Screen { get; private set; }

        public void Dispose() {
            Host.Dispose();
            m_bootState.Dispose();
            m_hostState.Dispose();
            m_files.Dispose();
        }
        // The instance a session screen resolved, and whether it still runs.
        public (string? Instance, bool Runs) Resolved(int screen) {
            var name = Host.ScreenSession(
                instanceName: WorldInstanceHost.BootInstanceName,
                screenIndex: screen
            )?.InstanceName;

            return (name, ((name is not null) && Host.Names.Contains(value: name)));
        }
    }

    [Fact]
    public void ARefusedAdmission_StopsTheEmptyEphemeralDestinationItStarted() {
        (int Instances, bool Runs) AfterBoot(string destination) {
            using var scene = new HubScene(
                first: destination,
                second: "d1"
            );

            return (scene.Host.Names.Count, scene.Resolved(screen: 1).Runs);
        }

        Assert.Equal(
            actual: AfterBoot(destination: "closed"),
            expected: (2, false)
        );
        Assert.Equal(
            actual: AfterBoot(destination: "d2"),
            expected: (3, true)
        );
    }
    [Fact]
    public void SessionScreensBootOneDestinationEach_AndARepointStopsTheOneLeftBehind() {
        using var scene = new HubScene(
            first: "d1",
            second: "d2"
        );

        // The boot world and one destination per session screen, no more.
        var first = scene.Resolved(screen: 1);
        var left = scene.Resolved(screen: 2);

        Assert.Equal(
            actual: scene.Host.Names.Count,
            expected: 3
        );
        Assert.True(condition: (first.Runs && left.Runs));

        scene.Boot.Server.EnqueueMutation(mutation: new WorldMutation.UpsertScreen(
            Principal: Principal.Console,
            Screen: (scene.Screen(arg: 2) with { Source = new WorldScreenSource.Session(Destination: "d3") })
        ));
        scene.Boot.Server.Advance(stepTicks: PortalStep);
        scene.Host.SettleBootScreenSessions(forwards: true);

        var repointed = scene.Resolved(screen: 2);

        Assert.NotEqual(
            actual: repointed.Instance,
            expected: left.Instance
        );
        Assert.Equal(
            actual: (scene.Host.Names.Count, scene.Resolved(screen: 1).Runs, scene.Host.Names.Contains(value: left.Instance!), repointed.Runs),
            expected: (3, true, false, true)
        );
    }
    [Fact]
    public void AWorldSomeoneStandsIn_OpensItsOwnPortal_AndTheirClickReachesTheWorldBeyondIt() {
        using var files = new TemporaryDirectory(prefix: "puck-portal-owner-files-");
        using var hostState = new TemporaryDirectory(prefix: "puck-portal-owner-host-");

        // World B is the portal-window boot document, holding a peer slot and admitting a peer that may drive its own
        // body and control screens; C is 'beyond' as the other laws author it.
        var b = Load(relative: PortalFixture);

        b = b with {
            Admission = [new WorldAdmissionEntry(
                Algorithm: string.Empty,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [
                    new WorldAdmissionGrant(
                        Budget: 64,
                        Capability: WorldCapability.Drive
                    ),
                    new WorldAdmissionGrant(
                        Capability: WorldCapability.Control,
                        Subject: GrantSubject.All
                    ),
                ],
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                PublicKey: string.Empty,
                Subject: null
            )],
            PopulationRaw = (b.Population with {
                CapacityRaw = 2,
                NetworkPlayers = 1,
            }),
        };

        using (var portal = new PortalScene()) {
            File.Copy(
                destFileName: Path.Combine(
                    path1: files.RootPath,
                    path2: WorldDocumentName.DocumentFile(name: "beyond")
                ),
                sourceFileName: Path.Combine(
                    path1: Path.GetDirectoryName(path: portal.Boot.SourcePath)!,
                    path2: WorldDocumentName.DocumentFile(name: "beyond")
                )
            );
        }

        var bPath = Path.GetFullPath(path: Path.Combine(
            path1: files.RootPath,
            path2: "b.world.json"
        ));

        File.WriteAllBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: b),
            path: bPath
        );

        using var host = new WorldInstanceHost(
            admitsSpawn: true,
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostState.RootPath)
        );
        var bootPath = Path.GetFullPath(path: Path.Combine(
            path1: files.RootPath,
            path2: "boot.world.json"
        ));

        File.WriteAllBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()),
            path: bootPath
        );

        var (bootRow, bootState) = FileBackedRow(
            definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bootPath)),
            name: WorldInstanceHost.BootInstanceName,
            path: bootPath
        );

        using var disposeBootState = bootState;

        host.AdmitBoot(row: bootRow);

        var (row, rowState) = FileBackedRow(
            definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bPath)),
            name: "b",
            path: bPath
        );

        using var disposeRowState = rowState;
        var glass = WorldFaceCatalog.For(definition: row.Server.Definition).Rows.Single(predicate: static face => (face.Source is WorldScreenSource.Session));

        host.Admit(row: row);
        host.StepInstances(masterDeltaTicks: PortalStep);

        // Nobody stands in B: its screen opens no session and boots nothing beyond it.
        Assert.Null(@object: host.ScreenSession(
            instanceName: "b",
            screenIndex: glass.ScreenIndex
        ));

        Assert.Null(@object: WorldAdmissionDoor.TryAdmitArrival(
            entries: row.Server.Definition.Admission,
            sourceAuthority: "peer/visitor",
            verdict: out var verdict
        ));
        Assert.True(
            condition: row.Server.TryAdmitPeerConnection(
                admitted: out var peer,
                expectedAdmissionEntries: row.Server.Definition.Admission,
                refusal: out var refusal,
                verdict: verdict
            ),
            userMessage: refusal
        );
        host.StepInstances(masterDeltaTicks: PortalStep);

        var session = host.ScreenSession(
            instanceName: "b",
            screenIndex: glass.ScreenIndex
        );

        Assert.True(
            condition: (session?.Observation is not null),
            userMessage: session?.Refusal
        );
        Assert.True(condition: row.Server.Engagement.Compose(
            actingPrincipal: peer.Identity,
            entityIndex: peer.BodyIndex,
            exclusive: false,
            target: GrantSubject.Screen(index: glass.ScreenIndex),
            targetPrincipal: peer.Identity
        ));

        var frame = glass.Frame;

        for (var tick = 0; (tick < 3); tick++) {
            var channels = new ChannelValues();

            channels[0] = FixedQ4816.One;
            row.Server.EnqueueIntent(submission: new IntentSubmission(
                EntityIndex: peer.BodyIndex,
                Intent: new PlayerIntent(
                    Channels: channels,
                    SourceRay: new SourceRay(
                        Direction: -frame.Normal,
                        Origin: (frame.Origin + (frame.Normal * FixedQ4816.FromInteger(value: 3)))
                    )
                ),
                Principal: peer.Identity,
                Tick: row.Server.NextInputTick
            ));
            host.StepInstances(masterDeltaTicks: PortalStep);
        }

        Assert.True(condition: host.TryGet(
            instance: out var c,
            name: session!.InstanceName!
        ));
        Assert.Equal(
            actual: FixedQ4816.FromRawBits(value: Cell(
                row: PressRow,
                server: c!.Server
            )),
            expected: FixedQ4816.One
        );
    }
}
