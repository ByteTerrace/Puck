using Puck.Testing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Attestation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Networking;
using Puck.World.Presentation.Tests;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

public sealed partial class WorldReleaseCutoverLawTests {
    // A closed rewind group, its issuer and a signed-in client, in one process over real QUIC on loopback: the silo
    // rows are the ones a hosted deployment activates, and the client is a host that joins its boot seat as a
    // --connect client does. The capture, the restore and the candidate run through the release coordinator.
    [Fact]
    public async Task AClosedGroupAdmitsAPlayerAsAGuestAndARestoreEndsIt() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var owner = Guid.NewGuid();
        var blobs = PuckStorageTestComposition.BuildStore();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath);
        var groups = new WorldReleaseGroupStore(owner: owner, store: blobs, target: target);
        // The issuer signs its bindings on the silo's clock and each row verifies them on system time, so the silo's
        // clock stands at the present. The probe never advances it.
        var clock = new VirtualClock(start: DateTimeOffset.UtcNow);
        var authority = new WorldAuthorityBlobStore(store: blobs, target: target, timeProvider: clock, documentDirectory: null);
        var archive = new WorldReleaseArchive(blobs, target, owner);
        var points = new WorldReleaseFixtureArchive(owner: owner, store: blobs, target: target);
        var restores = new WorldReleaseRestore(blobs: blobs, owner: owner, target: target, timeProvider: clock);
        var identities = new[] {
            new WorldAuthorityIdentity(Owner: owner, World: SafeName.Parse(candidate: "alpha")),
            new WorldAuthorityIdentity(Owner: owner, World: SafeName.Parse(candidate: "beta")),
        };
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes("world.pk8", key.ExportPkcs8PrivateKey());
        using var root = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootFile = directory.WriteBytes("issuer-root.pk8", root.ExportPkcs8PrivateKey());
        using var account = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var accountFile = directory.WriteBytes("account.pk8", account.ExportPkcs8PrivateKey());
        var issuerDomain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: root.ExportSubjectPublicKeyInfo());
        // The entry row keeps one endpoint across the restore, as a deployment's does.
        int port;

        using (var probe = new UdpClient(localEP: new IPEndPoint(address: IPAddress.Loopback, port: 0))) {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }
        var endpoint = $"127.0.0.1:{port}";
        var initial = Fixtures.BuildDocument();
        WorldDefinition Document(string world) => initial with {
            HostRaw = Fixtures.StandardHost with {
                Authority = ((world == "alpha") ? endpoint : null),
                AuthorityNamespace = world,
                Listen = ((world == "alpha") ? endpoint : null),
                Presentation = WorldHostPresentation.None,
            },
            PopulationRaw = initial.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2), NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        var package = Directory.CreateDirectory(path: Path.Combine(path1: directory.RootPath, path2: "package")).FullName;
        var pins = new Dictionary<string, string>();

        foreach (var identity in identities) {
            var document = Document(world: identity.World.Value);
            var bytes = WorldDefinitionSerialization.Serialize(definition: document);

            File.WriteAllBytes(Path.Combine(path1: package, path2: (identity.World.Value + ".world.json")), bytes);
            pins[$"{owner:D}/{identity.World}"] = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
            Assert.True(condition: (await authority.PublishDefinitionAsync(identity, document, Token)).Ok);
        }
        var release = new WorldReleaseManifest {
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            Label = "guest-probe",
            SourceRevision = new string(c: 'a', count: 40),
            Engine = ("sha256/" + new string(c: 'a', count: 64)),
            Definitions = pins,
            DefinitionFiles = identities.ToDictionary(row => $"{owner:D}/{row.World}", row => (row.World.Value + ".world.json")),
            PersistenceContract = "puck.world.persistence.v1",
            PeerProtocolContract = "puck.world.peer.v1",
        };

        await archive.SaveAsync(release, package, Token);
        Required(outcome: await groups.CreateAsync("primary", release.Identity, Token));
        WorldSiloHost Host() => new(
            new WorldSiloDefinition(
                identities.Select(selector: row => new WorldSiloWorldRow(owner, row.World, new(KeyFile: keyFile), true)).ToArray(),
                new(Budget: 2),
                new("directory", JsonElement.Parse("{}")),
                directory.RootPath,
                new(Kind: "Localhost"),
                Release: new("primary", owner, release.Identity) { ClosedGroupRewind = true },
                Issuer: new WorldIssuerConfiguration(
                    KeyFile: rootFile,
                    SignIn: new(WorldAccountSignIn.Provider.Type, JsonSerializer.SerializeToElement(value: new Dictionary<string, object> {
                        ["accounts"] = new Dictionary<string, string> { ["player-one"] = Convert.ToBase64String(inArray: account.ExportSubjectPublicKeyInfo()) },
                    }))
                )
            ),
            blobs,
            new SiloConsoleRouting(
                source: () => new TextCommandSource(new CommandRegistry([])),
                tagging: new SiloConsoleTagging(output: output)
            ),
            target,
            timeProvider: clock,
            extensions: Puck.Abstractions.PuckExtensionSet.Compose(extensions: [new WorldServerExtension()])
        );
        var source = Host();
        using var sourceInstances = source.Instances;

        await ActivateAllAsync(host: source, identities: identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(host: source));
        Assert.True(condition: source.Instances.TryGet(instance: out var alpha, name: "alpha"));

        // The client: a host whose boot world's authority is the session the issuer vouched for.
        using var clientScratch = new TemporaryDirectory(prefix: "puck-guest-probe-client-");
        var seats = new RoutedSeats();
        var clientNetwork = new WorldPeerNetwork(transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);
        var clientClosed = false;
        var transportKey = alpha!.Federation.Network!.Peer.Id.KeyHash;

        Assert.True(condition: PeerEndpoint.TryParse(endpoint: out var issuerEndpoint, value: endpoint));
        var credential = WorldAccountSignIn.Provider.Client(
            JsonSerializer.SerializeToElement(value: new Dictionary<string, string> { ["subject"] = "player-one", ["keyFile"] = accountFile }),
            TimeProvider.System
        );
        // The issuance exchange waits on the silo's tick thread, which this probe steps.
        var silo = new SiloPump(host: source);
        using var oracle = new IssuedKeySigningOracle(
            issue: (runKey, ct) => WorldIssuanceClient.Issue(
                cancellationToken: ct,
                clock: TimeProvider.System,
                credential: credential,
                endpoint: issuerEndpoint!,
                key: runKey,
                remoteKeyHash: transportKey
            ),
            session: Guid.NewGuid(),
            validity: WorldAttestedAuthenticator.MaximumClaimAge
        );
        var signIn = Task.Run(function: () => oracle.Issue(cancellationToken: Token), cancellationToken: Token);

        await silo.UntilAsync(done: () => signIn.IsCompleted, what: "the issuer vouched for the client's key");
        var session = oracle.AuthorityIdentity;

        Assert.Equal(expected: issuerDomain, actual: (await signIn).Domain);
        Assert.StartsWith(expectedStartString: $"{issuerDomain}/player-one/", actualString: session);
        var clientDocument = initial with {
            PopulationRaw = initial.Population with {
                SeatActivationRaw = [SeatActivationPolicy.Eager, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand],
            },
        };
        using var boot = HostRow.Build(definition: clientDocument, name: WorldInstanceHost.BootInstanceName);

        boot.Server.OverrideAuthorityIdentity(authorityIdentity: session);
        boot.Instance.Federation = new WorldFederationIdentity(
            Authenticator: new WorldAttestedAuthenticator(oracle: oracle, remoteKeyHash: transportKey),
            Network: clientNetwork,
            Subject: session
        );
        var client = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: seats,
            stateRoot: new WorldStateRoot(path: Directory.CreateDirectory(path: clientScratch.PathOf(name: "state")).FullName)
        );

        try {
            client.AdmitBoot(row: boot.Instance);
            Assert.True(condition: boot.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 0),
                Slot: 0,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
            _ = seats.OccupySeat(profile: null, slot: 0);
            client.JoinRemote(
                authority: endpoint,
                documentPath: Path.Combine(path1: package, path2: "alpha.world.json")
            );
            void StepClient() {
                _ = client.FollowRemoteJoin();
                client.DrainPendingTransfers();
                client.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }
            bool SeatAway() => (seats.RoutedEndpoint(slot: 0)?.Identity.StartsWith(value: "$traveler/", comparisonType: StringComparison.Ordinal) == true);

            // 1. The player's seat arrives.
            await silo.UntilAsync(done: () => (GuestBodies(host: source, session: session, world: "alpha") == 1) && SeatAway(), each: StepClient, what: "the client's seat arrived in the closed group");
            Assert.Equal(expected: 1, actual: Counts(host: source, world: "alpha").MobilityCredentials);

            // 2. The door refuses everyone else, and a guest in any other shape, by name, and writes nothing.
            var keysBefore = Counts(host: source, world: "alpha").TransferKeys;
            var rate = alpha.Server.Configuration.SimulationRateHz;
            WorldTransferCaller Guest(string subject, out string ns, bool vouched = true, string? domain = null) {
                ns = $"{(domain ?? issuerDomain)}/{subject}/{Guid.NewGuid():N}";
                return WorldTransferCaller.Authenticated(identity: new VerifiedPeerIdentity(
                    Authority: ns,
                    Session: new VerifiedSession(Domain: (domain ?? issuerDomain), Run: ns[(ns.LastIndexOf(value: '/') + 1)..], Subject: subject, Vouched: vouched),
                    ValidThrough: long.MaxValue
                ));
            }
            WorldTransferReservationRequest Request(string ns, ulong id, Func<WorldTransferReservationMember, WorldTransferReservationMember>? shape = null) {
                var minted = new WorldEntityAddress(Authority: ns, Generation: 1, Index: 0);
                var member = new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Identity: null,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: minted, Epoch: 0, Incarnation: minted),
                    PreferredSlot: -1,
                    Principal: default,
                    Source: IntentSource.Live
                );

                return new(
                    Border: string.Empty,
                    BorderCapacity: null,
                    DeadlineSourceTick: 600,
                    Members: [(shape?.Invoke(arg: member) ?? member)],
                    PartyAllOrNothing: true,
                    PeerAdmission: true,
                    SourceAuthority: ns,
                    SourceRateHz: rate,
                    SourceTick: 0,
                    TransferId: id
                );
            }
            async Task<string> RefusedAsync(WorldTransferCaller caller, WorldTransferReservationRequest request) {
                var reserving = alpha.Server.ReserveTransferAsync(caller: caller, ct: Token, request: request);

                await silo.UntilAsync(done: () => reserving.IsCompleted, what: "a reservation was answered");
                var reply = await reserving;

                Assert.False(condition: reply.Accepted, userMessage: "a disallowed party was leased");
                return reply.Reason;
            }
            var guest = Guest(subject: "player-two", ns: out var second);

            Assert.Contains(
                expectedSubstring: "it is a companion",
                actualString: await RefusedAsync(caller: guest, request: Request(id: 1, ns: second, shape: member => member with { Companion = new WorldTransferCompanion(Driver: null, FollowsMember: null, Home: second, Role: "pet", Ticket: null) }))
            );
            Assert.Contains(
                expectedSubstring: "is not a player's seat",
                actualString: await RefusedAsync(caller: guest, request: Request(id: 2, ns: second, shape: member => member with { Source = IntentSource.Idle }))
            );
            Assert.Contains(
                expectedSubstring: "was not minted by the session that offers it",
                actualString: await RefusedAsync(caller: guest, request: Request(id: 3, ns: second, shape: member => member with {
                    Mobility = new WorldMobilityIdentity(DepartedFrom: new(Authority: second, Generation: 1, Index: 0), Epoch: 3, Incarnation: new(Authority: session, Generation: 1, Index: 0)),
                }))
            );
            // A session no issuer chain vouched for, a session another root vouched for, a network caller whose
            // namespace spells a world of the group, and a caller that proves nothing are all outside.
            Assert.Contains(expectedSubstring: WorldTransferBoundary.Refusal, actualString: await RefusedAsync(caller: Guest(subject: "pinned", ns: out var pinned, vouched: false), request: Request(id: 1, ns: pinned)));
            Assert.Contains(expectedSubstring: WorldTransferBoundary.Refusal, actualString: await RefusedAsync(caller: Guest(subject: "stranger", ns: out var stranger, domain: new string(c: 'f', count: 64)), request: Request(id: 1, ns: stranger)));
            Assert.Contains(
                expectedSubstring: WorldTransferBoundary.Refusal,
                actualString: await RefusedAsync(caller: WorldTransferCaller.Authenticated(identity: new VerifiedPeerIdentity(Authority: "beta", ValidThrough: long.MaxValue)), request: Request(id: 1, ns: "beta"))
            );
            Assert.Contains(expectedSubstring: WorldTransferBoundary.Refusal, actualString: (await alpha.Server.ReserveTransferAsync(ct: Token, request: Request(id: 1, ns: second))).Reason);
            Assert.Equal(expected: WorldTransferStatus.Ended, actual: alpha.Server.TransferStatus(caller: guest, sourceAuthority: second, transferId: 1));
            Assert.Equal(expected: WorldTransferStatus.Ended, actual: alpha.Server.TransferStatus(sourceAuthority: "outside", transferId: 1));
            Assert.NotNull(@object: alpha.Server.CustodyRefusal());
            Assert.Equal(expected: keysBefore, actual: Counts(host: source, world: "alpha").TransferKeys);

            // A guest in the allowed shape is leased, and its commit is refused by name when it carries motion. The
            // refused session then holds nothing live, so the ledger keeps no row of it.
            var leasing = alpha.Server.ReserveTransferAsync(caller: guest, ct: Token, request: Request(id: 4, ns: second));

            await silo.UntilAsync(done: () => leasing.IsCompleted, what: "a guest's reservation was answered");
            Assert.True(condition: (await leasing).Accepted, userMessage: (await leasing).Reason);
            var committing = alpha.Server.CommitTransferAsync(caller: guest, ct: Token, members: [new WorldTransferCommitMember(
                Arrival: WorldTransferArrival.Mapped,
                BodyMotionProgramName: string.Empty,
                PlanarVelocity: default,
                Position: default,
                Profile: null,
                VerticalVelocity: default,
                YawRadians: default
            )], sourceAuthority: second, transferId: 4);

            await silo.UntilAsync(done: () => committing.IsCompleted, what: "a guest's commit was answered");
            var (commitStatus, commitReason) = await committing;

            Assert.Equal(expected: WorldTransferStatus.Missing, actual: commitStatus);
            Assert.Contains(expectedSubstring: "it carries a Mapped arrival", actualString: commitReason);
            await silo.UntilAsync(done: () => (alpha.Server.TransferStatus(caller: guest, sourceAuthority: second, transferId: 4) == WorldTransferStatus.Ended), what: "the refused session was forgotten");
            Assert.Equal(expected: keysBefore, actual: Counts(host: source, world: "alpha").TransferKeys);
            Assert.Equal(expected: 1, actual: GuestBodies(host: source, session: session, world: "alpha"));

            // 3. A capture with the player present is a baseline that still holds the player's rows.
            var capturing = source.ExportReleaseFixtureAsync(Guid.NewGuid(), Token);

            await silo.UntilAsync(done: () => capturing.IsCompleted, each: StepClient, what: "the group was captured with a player present");
            var point = await capturing;

            Assert.NotNull(@object: point.RewindBoundary);
            Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: (await points.ReadCheckpointAsync(point, "alpha", Token)).Span,
                checkpoint: out var baseline,
                reason: out var decodeReason
            ), userMessage: decodeReason);
            Assert.Equal(expected: ["alpha", "beta"], actual: baseline!.Escrow.RewindBaseline);
            Assert.Contains(collection: baseline.Escrow.MobilityAdmissions, filter: row => (row.SourceAuthority == session));
            Assert.Contains(collection: baseline.Population.Entries, filter: entry => (entry.Admission.Mobility?.Incarnation.Authority == session));

            // 4. The restore: the candidate's rows hold no guest and no row of the session, before any tick.
            var begun = Required(outcome: await restores.BeginAsync("primary", point.RequestId, point.Identity, Guid.NewGuid(), true, Token));
            var candidate = Host();
            using var candidateInstances = candidate.Instances;
            var restoring = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
                begun,
                release,
                // A deployment's drained source is a process that has exited. Here it shares the candidate's process,
                // so its entry row gives up the endpoint once it has drained, before the candidate's row opens it.
                new SourceGone(
                    inner: new LoopbackRuntime(
                        new WorldSiloReleaseRuntime(source, candidate, identities, () => throw new InvalidOperationException(message: "a successful restore must not compensate")),
                        source,
                        candidate
                    ),
                    gone: () => {
                        alpha.Door!.Dispose();
                        alpha.Federation.Network!.Dispose();
                    }
                ),
                Token
            );

            await PumpAllAsync(hosts: [source, candidate], operation: restoring);
            Assert.True(condition: (await restoring).Completed, userMessage: (await restoring).Detail);
            foreach (var identity in identities) {
                Assert.Equal(expected: 0, actual: GuestBodies(host: candidate, session: session, world: identity.World.Value));
                Assert.Equal(expected: new WorldTransferTableCounts(ActiveTransactions: 0, MobilityCredentials: 0, MobilityLeases: 0, TransferKeys: 0), actual: Counts(host: candidate, world: identity.World.Value));
            }
            Assert.True(condition: candidate.Instances.TryGet(instance: out var restoredAlpha, name: "alpha"));
            Assert.Equal(expected: point.Worlds["alpha"].Tick, actual: restoredAlpha!.CompletedTicks);

            // 5. The client is told its session ended there, takes its seat back and joins again as a first entry.
            silo = new SiloPump(host: candidate);
            var returned = false;

            await silo.UntilAsync(
                done: () => {
                    returned |= (seats.RoutedEndpoint(slot: 0)?.Identity == WorldInstanceHost.BootInstanceName);
                    return returned && SeatAway() && (GuestBodies(host: candidate, session: session, world: "alpha") == 1);
                },
                each: StepClient,
                what: "the client's seat came home and joined the restored group again"
            );
            Assert.Equal(expected: 1, actual: Counts(host: candidate, world: "alpha").MobilityCredentials);

            // 6. The client goes away: its traveler parks, its reconnect grace runs out on the tick, and the ledger
            // keeps no row of the session.
            clientClosed = true;
            client.Dispose();
            clientNetwork.Dispose();
            await silo.UntilAsync(done: () => (GuestBodies(host: candidate, session: session, world: "alpha") == 0), what: "the departed client's traveler was retired when its grace ran out");
            Assert.Equal(expected: new WorldTransferTableCounts(ActiveTransactions: 0, MobilityCredentials: 0, MobilityLeases: 0, TransferKeys: 0), actual: Counts(host: candidate, world: "alpha"));
            await PumpAsync(host: candidate, operation: candidate.DrainAsync(ct: Token));
        } finally {
            if (!clientClosed) {
                client.Dispose();
                clientNetwork.Dispose();
            }
        }
    }

    private static int GuestBodies(WorldSiloHost host, string world, string session) {
        Assert.True(condition: host.Instances.TryGet(instance: out var row, name: world));
        return row!.Server.ExecuteAuthorityOperation(operation: () => {
            var bodies = 0;

            for (var index = 0; (index < row.Server.Population.Capacity); index++) {
                bodies += ((row.Server.Population.ResolveIncarnation(authority: row.Server.AuthorityIdentity, index: index)?.Authority == session) ? 1 : 0);
            }
            return bodies;
        });
    }
    private static WorldTransferTableCounts Counts(WorldSiloHost host, string world) {
        Assert.True(condition: host.Instances.TryGet(instance: out var row, name: world));
        return row!.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Counts);
    }

    // The release runtime with one addition: the drained source lets go of what only a live process holds before the
    // candidate starts.
    private sealed class SourceGone(IWorldReleaseRuntime inner, Action gone) : IWorldReleaseRuntime {
        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.DrainSourceAndCaptureAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishCandidateAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishRecoveredSourceAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.ReadFenceCensusAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.RecoverSourceAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            gone();
            return inner.StartCandidatePrivatelyAsync(cancellationToken: cancellationToken, operation: operation);
        }
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartRecoveredSourceAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StopCandidateAsync(cancellationToken: cancellationToken, operation: operation);
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.VerifyCandidatePrivatelyAsync(cancellationToken: cancellationToken, operation: operation);
    }

    // Steps one silo on the test's thread until a condition holds: each round drains the silo's activation work and
    // its rows' mailboxes and steps its rows, as its host loop does.
    private sealed class SiloPump(WorldSiloHost host) {
        private readonly WorldSiloSimulation m_simulation = new(host: host);
        private ulong m_step;

        public async Task UntilAsync(Func<bool> done, string what, Action? each = null) {
            for (var round = 0; (round < 6000); round++) {
                each?.Invoke();
                _ = m_simulation.TryStep(new FixedStepContext(m_step++, Fixtures.StepTicks, Fixtures.StepTicks), default);
                if (done()) {
                    return;
                }
                await Task.Delay(millisecondsDelay: 2, cancellationToken: Token);
            }
            Assert.Fail(message: $"{what}: this did not happen within the probe's rounds");
        }
    }
}
