using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class PortalInputLawTests {
    // A destination document: the fixture world, admitting a viewer's session with its whole-world view, or admitting
    // nobody.
    private static WorldDefinition Observable(bool admits) => (Fixtures.BuildDocument() with {
        Admission = (admits
            ? [new WorldAdmissionEntry(
                Disclosure: WorldDisclosureTier.Replica,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(
                    Budget: 64,
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.All
                )],
                Mode: WorldAdmissionTrustMode.FederatedAuthority
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
    // A window renders its destination endpoint's whole replica only for the instance its session observes: a replacement
    // started under a stopped destination's name never admitted that session, so its endpoint is not the window's.
    [Fact]
    public void AWindowRendersOnlyTheEndpointOfTheWorldItsSessionObserves() {
        using var scene = new HubScene(
            first: "d1",
            second: "d2"
        );
        using var files = new TemporaryDirectory(prefix: "puck-portal-window-endpoint-");
        var session = scene.Host.ScreenSession(
            instanceName: WorldInstanceHost.BootInstanceName,
            screenIndex: 1
        )!;
        var name = session.InstanceName!;
        var observation = session.Observation!;

        Assert.True(condition: scene.Host.TryWindowEndpoint(
            endpoint: out _,
            name: name,
            observation: observation
        ));
        Assert.True(
            condition: scene.Host.TryStop(
                name: name,
                reason: out var reason
            ),
            userMessage: reason
        );

        var path = Path.GetFullPath(path: Path.Combine(
            path1: files.RootPath,
            path2: "replacement.world.json"
        ));
        var replacement = Observable(admits: true);

        File.WriteAllBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: replacement),
            path: path
        );

        var (row, rowState) = FileBackedRow(
            definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)),
            name: name,
            path: path
        );

        using var disposeRowState = rowState;

        scene.Host.Admit(row: row);

        Assert.True(condition: scene.Host.TryEndpoint(
            endpoint: out _,
            name: name
        ));
        Assert.False(condition: scene.Host.TryWindowEndpoint(
            endpoint: out _,
            name: name,
            observation: observation
        ));
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
        scene.Host.SettleBootScreenSessions(stepped: true);

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
    public void AnEditOfAScreensRendering_KeepsItsDestination_OnlyANewDestinationReplacesIt() {
        (string? Instance, bool Runs, int Width) AfterEdit(Func<WorldScreenSource.Session, WorldScreenSource.Session> edit) {
            using var scene = new HubScene(
                first: "d1",
                second: "d2"
            );
            var before = scene.Resolved(screen: 2);

            scene.Boot.Server.EnqueueMutation(mutation: new WorldMutation.UpsertScreen(
                Principal: Principal.Console,
                Screen: (scene.Screen(arg: 2) with { Source = edit(((WorldScreenSource.Session)scene.Screen(arg: 2).Source)) })
            ));
            scene.Boot.Server.Advance(stepTicks: PortalStep);
            scene.Host.SettleBootScreenSessions(stepped: true);

            var after = scene.Resolved(screen: 2);
            var session = scene.Host.ScreenSession(
                instanceName: WorldInstanceHost.BootInstanceName,
                screenIndex: 2
            )!;

            return (((after.Instance == before.Instance)
                ? "kept"
                : after.Instance), (after.Runs && scene.Host.Names.Contains(value: before.Instance!)), (session.Source.Resolution?.Width ?? 0));
        }

        // A new panel size reaches the session's source, and the destination runs on under the same instance.
        Assert.Equal(
            actual: AfterEdit(edit: static source => source with {
                Resolution = new WorldScreenResolution(
                Height: 96,
                Width: 128
            ),
            }),
            expected: ("kept", true, 128)
        );
        // A new destination replaces it.
        Assert.NotEqual(
            actual: AfterEdit(edit: static source => source with { Destination = "d3" }).Instance,
            expected: "kept"
        );
    }
}
