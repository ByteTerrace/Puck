using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class PortalInputLawTests {
    // World B, the portal-window boot document holding a peer slot, beside a plain boot world: a peer admitted to B may
    // drive its own body and control screens, and B's glass observes an ephemeral 'beyond' as the other laws author it,
    // which the host stops once nobody needs it.
    private sealed class OwnerScene : IDisposable {
        // Named to step ahead of the ephemeral destination its glass starts, whose name leads with its scope ordinal.
        public const string OwnerName = "0b";

        private readonly TemporaryDirectory m_files = new(prefix: "puck-portal-owner-files-");
        private readonly TemporaryDirectory m_hostState = new(prefix: "puck-portal-owner-host-");

        private readonly TemporaryDirectory m_bootState;
        private readonly TemporaryDirectory m_rowState;

        public OwnerScene() {
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
                Destinations = [.. b.Destinations!.Select(selector: static destination => destination with { Durability = WorldDestinationDurability.Ephemeral })],
                PopulationRaw = (b.Population with {
                    CapacityRaw = 2,
                    NetworkPlayers = 1,
                }),
            };

            using (var portal = new PortalScene()) {
                File.Copy(
                    destFileName: Path.Combine(
                        path1: m_files.RootPath,
                        path2: WorldDocumentName.DocumentFile(name: "beyond")
                    ),
                    sourceFileName: Path.Combine(
                        path1: Path.GetDirectoryName(path: portal.Boot.SourcePath)!,
                        path2: WorldDocumentName.DocumentFile(name: "beyond")
                    )
                );
            }

            var bPath = Path.GetFullPath(path: Path.Combine(
                path1: m_files.RootPath,
                path2: "b.world.json"
            ));
            var bootPath = Path.GetFullPath(path: Path.Combine(
                path1: m_files.RootPath,
                path2: "boot.world.json"
            ));

            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: b),
                path: bPath
            );
            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()),
                path: bootPath
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

            var (bootRow, bootState) = FileBackedRow(
                definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bootPath)),
                name: WorldInstanceHost.BootInstanceName,
                path: bootPath
            );

            m_bootState = bootState;
            Host.AdmitBoot(row: bootRow);

            var (row, rowState) = FileBackedRow(
                definition: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bPath)),
                name: OwnerName,
                path: bPath
            );

            m_rowState = rowState;
            Row = row;
            Glass = WorldFaceCatalog.For(definition: row.Server.Definition).Rows.Single(predicate: static face => (face.Source is WorldScreenSource.Session));
            Host.Admit(row: row);
        }

        public WorldFaceRow Glass { get; }
        public WorldInstanceHost Host { get; }
        public WorldInstance Row { get; }
        // The session B's glass holds, or null while it holds none.
        public WorldObservationSession? Session => Host.ScreenSession(
            instanceName: OwnerName,
            screenIndex: Glass.ScreenIndex
        );

        public void Dispose() {
            Host.Dispose();
            m_rowState.Dispose();
            m_bootState.Dispose();
            m_hostState.Dispose();
            m_files.Dispose();
        }
        // Admits a visitor into B, standing in its peer slot.
        public WorldPeerEventEntry Admit() {
            Assert.Null(@object: WorldAdmissionDoor.TryAdmitArrival(
                entries: Row.Server.Definition.Admission,
                sourceAuthority: "peer/visitor",
                verdict: out var verdict
            ));
            Assert.True(
                condition: Row.Server.TryAdmitPeerConnection(
                    admitted: out var peer,
                    expectedAdmissionEntries: Row.Server.Definition.Admission,
                    refusal: out var refusal,
                    verdict: verdict
                ),
                userMessage: refusal
            );

            return peer;
        }
        public void Step() => Host.StepInstances(masterDeltaTicks: PortalStep);
    }

    [Fact]
    public void AWorldSomeoneStandsIn_OpensItsOwnPortal_AndTheirClickReachesTheWorldBeyondIt() {
        using var scene = new OwnerScene();

        scene.Step();

        // Nobody stands in B: its screen opens no session and boots nothing beyond it.
        Assert.Null(@object: scene.Session);

        var peer = scene.Admit();

        scene.Step();

        var session = scene.Session;

        Assert.True(
            condition: (session?.Observation is not null),
            userMessage: session?.Refusal
        );
        Assert.True(condition: scene.Row.Server.Engagement.Compose(
            actingPrincipal: peer.Identity,
            entityIndex: peer.BodyIndex,
            exclusive: false,
            target: GrantSubject.Screen(index: scene.Glass.ScreenIndex),
            targetPrincipal: peer.Identity
        ));

        var frame = scene.Glass.Frame;

        for (var tick = 0; (tick < 3); tick++) {
            var channels = new ChannelValues();

            channels[0] = FixedQ4816.One;
            scene.Row.Server.EnqueueIntent(submission: new IntentSubmission(
                EntityIndex: peer.BodyIndex,
                Intent: new PlayerIntent(
                    Channels: channels,
                    SourceRay: new SourceRay(
                        Direction: -frame.Normal,
                        Origin: (frame.Origin + (frame.Normal * FixedQ4816.FromInteger(value: 3)))
                    )
                ),
                Principal: peer.Identity,
                Tick: scene.Row.Server.NextInputTick
            ));
            scene.Step();
        }

        Assert.True(condition: scene.Host.TryGet(
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
    [Fact]
    public void TheLastVisitorLeaving_StopsTheWorldBeyondThePortal_WithinTheSameStepPass() {
        using var scene = new OwnerScene();
        var peer = scene.Admit();

        scene.Step();

        var beyond = scene.Session!.InstanceName!;

        // The destination steps after B in the same pass, so B's settle stops a row the pass has yet to reach.
        Assert.True(
            condition: (string.CompareOrdinal(
                strA: beyond,
                strB: OwnerScene.OwnerName
            ) > 0),
            userMessage: beyond
        );
        Assert.Contains(
            collection: scene.Host.Names,
            expected: beyond
        );

        scene.Row.Server.DisconnectPeerConnection(peer: peer);
        scene.Step();

        Assert.Null(@object: scene.Session);
        Assert.DoesNotContain(
            collection: scene.Host.Names,
            expected: beyond
        );
    }
    [Fact]
    public void APausedWorld_StillClosesAPortalNobodyStandsBeside() {
        using var scene = new OwnerScene();
        var peer = scene.Admit();

        scene.Step();

        var beyond = scene.Session!.InstanceName!;

        scene.Row.IsPaused = true;
        scene.Step();

        // Paused with its visitor still in it, B keeps its portal open.
        Assert.Contains(
            collection: scene.Host.Names,
            expected: beyond
        );

        scene.Row.Server.DisconnectPeerConnection(peer: peer);
        scene.Step();

        Assert.Equal(
            actual: (scene.Row.IsPaused, (scene.Session is null), scene.Host.Names.Contains(value: beyond)),
            expected: (true, true, false)
        );
    }
}
