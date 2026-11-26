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
            PopulationRaw = initial.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 8), NetworkPlayers = 8 },
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
            extensions: Puck.Abstractions.PuckExtensionSet.Compose(extensions: [new WorldServerExtension()]),
            // Small enough for the probe to reach: a session's budget, and what every guest may hold together.
            guestKeysPerSession: GuestKeysPerSession,
            guestKeys: GuestKeys
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
        // The client's crossing log takes every record at once but a settlement, which it holds: the client's commit
        // is confirmed and its departure stays in doubt until the probe answers the record.
        var clientLog = new HeldSettlementLog();
        var clientLines = new NarratedLines();

        boot.Server.InstallCrossingLog(log: clientLog);
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

        using var clientNarration = client.AttachNarrationSink(sink: clientLines);

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
                // The host loop steps its boot world itself, whenever that world has work of its own: here, a departure
                // still in doubt, whose crossing record settles when the tick opens and drains the mailbox.
                if (client.ShouldStepBoot(stepTicks: Fixtures.StepTicks)) {
                    _ = boot.Server.DrainAdministrative();
                    boot.Server.Advance(stepTicks: Fixtures.StepTicks);
                }
                client.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }
            bool SeatAway() => (seats.RoutedEndpoint(slot: 0)?.Identity.StartsWith(value: "$traveler/", comparisonType: StringComparison.Ordinal) == true);

            // 1. The player's seat arrives.
            await silo.UntilAsync(done: () => (GuestBodies(host: source, session: session, world: "alpha") == 1) && SeatAway(), each: StepClient, what: "the client's seat arrived in the closed group");
            Assert.Equal(expected: 1, actual: Counts(host: source, world: "alpha").MobilityCredentials);

            // 2. The door refuses everyone else, and a guest in any other shape, by name, and writes nothing.
            var keysBefore = Counts(host: source, world: "alpha").TransferKeys;
            var rate = alpha.Server.Configuration.SimulationRateHz;
            VerifiedPeerIdentity Session(string subject, out string ns, bool vouched = true, string? domain = null) {
                ns = $"{(domain ?? issuerDomain)}/{subject}/{Guid.NewGuid():N}";
                return new VerifiedPeerIdentity(
                    Authority: ns,
                    Session: new VerifiedSession(Domain: (domain ?? issuerDomain), Run: ns[(ns.LastIndexOf(value: '/') + 1)..], Subject: subject, Vouched: vouched),
                    ValidThrough: long.MaxValue
                );
            }
            WorldTransferCaller Guest(string subject, out string ns, bool vouched = true, string? domain = null) =>
                WorldTransferCaller.Authenticated(identity: Session(domain: domain, ns: out ns, subject: subject, vouched: vouched));
            WorldTransferReservationRequest Request(string ns, ulong id, Func<WorldTransferReservationMember, WorldTransferReservationMember>? shape = null, int body = 0, string border = "", int? borderCapacity = null) {
                var minted = new WorldEntityAddress(Authority: ns, Generation: 1, Index: body);
                var member = new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Identity: null,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: minted, Epoch: 0, Incarnation: minted),
                    PreferredSlot: -1,
                    Principal: Principal.Seat(slot: 0),
                    Source: IntentSource.Live
                );

                return new(
                    Border: border,
                    BorderCapacity: borderCapacity,
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
            async Task<WorldTransferReservationReply> ReserveAsync(WorldTransferCaller? caller, WorldTransferReservationRequest request) {
                var reserving = alpha.Server.ReserveTransferAsync(caller: caller, ct: Token, request: request);

                await silo.UntilAsync(done: () => reserving.IsCompleted, what: "a reservation was answered");
                return await reserving;
            }
            async Task<string> RefusedAsync(WorldTransferCaller caller, WorldTransferReservationRequest request) {
                var reply = await ReserveAsync(caller: caller, request: request);

                Assert.False(condition: reply.Accepted, userMessage: "a disallowed party was leased");
                return reply.Reason;
            }
            async Task<(WorldTransferStatus Status, string Reason)> CommitAsync(WorldTransferCaller caller, string ns, ulong id, WorldTransferCommitMember member) {
                var committing = alpha.Server.CommitTransferAsync(caller: caller, ct: Token, members: [member], sourceAuthority: ns, transferId: id);

                await silo.UntilAsync(done: () => committing.IsCompleted, what: "a guest's commit was answered");
                return await committing;
            }
            WorldTransferCommitMember Landing(WorldTransferArrival arrival = WorldTransferArrival.Spawn, Puck.Maths.FixedQ4816 turn = default, WorldIdentityProjection? profile = null) => new(
                Arrival: arrival,
                BodyMotionProgramName: string.Empty,
                PlanarVelocity: default,
                Position: default,
                Profile: profile,
                TravelTurn: turn,
                VerticalVelocity: default,
                YawRadians: default
            );
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

            // A producer's source is no player's seat, whatever the sender says holds it.
            Assert.Contains(
                expectedSubstring: "is not a player's seat",
                actualString: await RefusedAsync(caller: guest, request: Request(id: 5, ns: second, shape: member => member with { RemoteHuman = true, Source = IntentSource.Producer(name: "patrol") }))
            );
            // The sender's border and its capacity are not read: a border with no room would refuse a cohort that named
            // it. The seat it leases is refused at commit for the arrival turn it carries, and the session is forgotten.
            var bordered = await ReserveAsync(caller: guest, request: Request(border: "adjacency/beta", borderCapacity: 0, id: 6, ns: second));

            Assert.True(condition: bordered.Accepted, userMessage: bordered.Reason);
            var turned = await CommitAsync(caller: guest, id: 6, member: Landing(turn: Puck.Maths.FixedQ4816.One), ns: second);

            Assert.Equal(expected: WorldTransferStatus.Missing, actual: turned.Status);
            Assert.Contains(expectedSubstring: "it carries an arrival turn", actualString: turned.Reason);
            await silo.UntilAsync(done: () => (alpha.Server.TransferStatus(caller: guest, sourceAuthority: second, transferId: 6) == WorldTransferStatus.Ended), what: "the session whose commit carried a turn was forgotten");
            Assert.Equal(expected: keysBefore, actual: Counts(host: source, world: "alpha").TransferKeys);

            // A guest lands under the subject its issuer vouched for, at this world's own rates, whatever identity it
            // sends: the id another player holds and speeds of its own choosing are not what lands.
            var claimed = new WorldIdentityProjection(ColorHex: "#336699", Id: "player-one", MoveSpeed: Puck.Maths.FixedQ4816.FromInteger(value: 99), Name: "Second", TurnSpeed: Puck.Maths.FixedQ4816.FromInteger(value: 99));
            var entering = await ReserveAsync(caller: guest, request: Request(body: 1, id: 7, ns: second, shape: member => member with { Identity = claimed, PreferredSlot = (WorldBodiesLimits.LocalSeatCount + 7) }));

            Assert.True(condition: entering.Accepted, userMessage: entering.Reason);
            Assert.NotEqual(expected: (WorldBodiesLimits.LocalSeatCount + 7), actual: entering.BodyIndices[0]);
            var entered = await CommitAsync(caller: guest, id: 7, member: Landing(profile: claimed), ns: second);

            Assert.True(condition: (entered.Status == WorldTransferStatus.Committed), userMessage: entered.Reason);
            var landedAs = alpha.Server.ExecuteAuthorityOperation(operation: () => alpha.Server.Population.EntryBody(index: entering.BodyIndices[0])!.Profile!.Project());

            Assert.Equal(expected: "player-two", actual: landedAs.Id);
            Assert.Equal(expected: "Second", actual: landedAs.Name);
            Assert.Null(@object: landedAs.MoveSpeed);
            Assert.Null(@object: landedAs.TurnSpeed);
            Assert.Equal(expected: 2, actual: Counts(host: source, world: "alpha").MobilityCredentials);

            // A question about a key the session never reserved is answered and writes no row.
            var rows = Counts(host: source, world: "alpha").TransferKeys;

            Assert.Equal(expected: WorldTransferStatus.Missing, actual: alpha.Server.TransferStatus(caller: guest, sourceAuthority: second, transferId: 999));
            Assert.Equal(expected: rows, actual: Counts(host: source, world: "alpha").TransferKeys);

            // A session's budget. This one holds the key it landed under; each lease it takes and gives up keeps its
            // row, and the lease past the budget is refused by name.
            for (var id = 8UL; (id < (7UL + GuestKeysPerSession)); id++) {
                var extra = await ReserveAsync(caller: guest, request: Request(body: ((int)id), id: id, ns: second));

                Assert.True(condition: extra.Accepted, userMessage: extra.Reason);
                alpha.Server.AbortTransfer(caller: guest, sourceAuthority: second, transferId: id);
            }
            Assert.Contains(
                expectedSubstring: $"holds the {GuestKeysPerSession} transfer keys one player's session may hold here",
                actualString: await RefusedAsync(caller: guest, request: Request(body: 99, id: 99, ns: second))
            );

            // What every guest may hold together. Another player's session leases until the guests' share is full, well
            // inside its own budget, and is then refused by name.
            var third = Guest(subject: "player-three", ns: out var thirdSession);
            string? shareRefusal = null;

            for (var id = 1UL; ((id <= GuestKeysPerSession) && (shareRefusal is null)); id++) {
                var reply = await ReserveAsync(caller: third, request: Request(body: ((int)id), id: id, ns: thirdSession));

                shareRefusal = (reply.Accepted ? null : reply.Reason);
            }
            Assert.Contains(expectedSubstring: $"this world's guests hold the {GuestKeys} transfer keys it keeps for players", actualString: shareRefusal);
            Assert.Equal(expected: GuestKeys, actual: Counts(host: source, world: "alpha").GuestTransferKeys);

            // No guest's row is counted against a world of the group: beta still reserves here.
            Assert.True(condition: source.Instances.TryGet(instance: out var beta, name: "beta"));
            var member = WorldTransferCaller.Colocated(source: beta!.Server);
            var fromBeta = await ReserveAsync(caller: member, request: Request(id: 1, ns: beta.Server.AuthorityIdentity));

            Assert.True(condition: fromBeta.Accepted, userMessage: fromBeta.Reason);
            alpha.Server.AbortTransfer(caller: member, sourceAuthority: beta.Server.AuthorityIdentity, transferId: 1);
            Assert.Equal(expected: GuestKeys, actual: Counts(host: source, world: "alpha").GuestTransferKeys);

            // A world of the group gives a credential to a vouched session alone: a vouched identity that signed for no
            // session (its namespace is its bare subject, which can spell a world's name) and a session no root vouched
            // for are refused before any door reads their namespace.
            var boundary = alpha.Server.TransferBoundary!;

            Assert.Null(@object: boundary.CredentialRefusal(identity: Session(ns: out _, subject: "player-four")));
            Assert.Contains(expectedSubstring: "answers only a player's session", actualString: boundary.CredentialRefusal(identity: new VerifiedPeerIdentity(Authority: "beta", ValidThrough: long.MaxValue)));
            Assert.Contains(expectedSubstring: "answers only a player's session", actualString: boundary.CredentialRefusal(identity: Session(ns: out _, subject: "pinned", vouched: false)));

            // The same over a real connection. The issuer vouches for a second key of the signed-in player, and that
            // key signs the door's challenge with no session behind it, which the engine's own client never does. The
            // door refuses the handshake by name. The same sign-in signing for a session is given its credential.
            using var bareKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var vouching = Task.Run(function: () => WorldIssuanceClient.Issue(
                cancellationToken: Token,
                clock: TimeProvider.System,
                credential: credential,
                endpoint: issuerEndpoint!,
                key: bareKey,
                remoteKeyHash: transportKey
            ), cancellationToken: Token);

            await silo.UntilAsync(done: () => vouching.IsCompleted, each: StepClient, what: "the issuer vouched for a second key");
            var bare = FederationHandshakeAsync(endpoint: endpoint, oracle: new SessionlessOracle(identity: await vouching, key: bareKey));

            await silo.UntilAsync(done: () => bare.IsCompleted, each: StepClient, what: "the door answered a claim that signed for no session");
            var (bareKind, bareText) = await bare;

            Assert.Equal(expected: ((byte)WorldFederationResponse.Refusal), actual: bareKind);
            Assert.StartsWith(
                expectedStartString: $"{nameof(WorldFederationRefusal.AuthenticationFailed)}: closed rewind group answers only a player's session",
                actualString: bareText
            );
            using var sessioned = new IssuedKeySigningOracle(
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
            var admitted = FederationHandshakeAsync(endpoint: endpoint, oracle: sessioned);

            await silo.UntilAsync(done: () => admitted.IsCompleted, each: StepClient, what: "the door answered a claim that signed for a session");
            Assert.Equal(expected: ((byte)WorldFederationResponse.Authenticated), actual: (await admitted).Kind);

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
                Assert.Equal(expected: 0, actual: GuestBodies(host: candidate, session: second, world: identity.World.Value));
                AssertNoGuestRows(host: candidate, world: identity.World.Value);
            }
            // What beta's own reservation left in alpha is the group's, and the restore keeps it.
            Assert.Equal(expected: 1, actual: Counts(host: candidate, world: "alpha").TransferKeys);
            Assert.True(condition: candidate.Instances.TryGet(instance: out var restoredAlpha, name: "alpha"));
            Assert.Equal(expected: point.Worlds["alpha"].Tick, actual: restoredAlpha!.CompletedTicks);

            // 5. The client is told its session ended there, takes its seat back and joins again as a first entry. It is
            // told twice. Its commit was confirmed and its settlement record never landed, so its departure is still in
            // doubt, and the restored group answers both that departure's status question (Ended) and its traveler's
            // observation (SessionEnded). One of the two returns the seat.
            silo = new SiloPump(host: candidate);
            Assert.True(condition: clientLog.Holding, userMessage: "the client's settlement record was not in flight at the restore");
            bool ToldEnded() => clientLines.Any(predicate: static line => line.Contains(value: nameof(WorldFederationRefusal.SessionEnded), comparisonType: StringComparison.Ordinal));
            int Returns() => clientLines.Count(predicate: static line => line.Contains(value: "returned to", comparisonType: StringComparison.Ordinal));

            // The observation's refusal arrives while the client is not stepped, so it is the first word the client acts on.
            await silo.UntilAsync(done: ToldEnded, what: "the client's observation was refused as ended");
            for (var step = 0; (step < 8); step++) {
                StepClient();
            }
            // That word does not seat the player at home while the departure still holds the body the seat left with.
            Assert.True(condition: SeatAway());
            Assert.False(condition: boot.Server.Population.IsActive(index: 0));
            Assert.Equal(expected: 0, actual: Returns());

            // The store refuses the settlement record, and from here answers at once. The departure resolves as ended
            // and restores the body, which is the seat's one return.
            clientLog.Answer(durability: WorldCrossingDurability.Refused);
            var returned = false;
            string? owedAtHome = null;

            await silo.UntilAsync(
                done: () => {
                    if (!returned && (seats.RoutedEndpoint(slot: 0)?.Identity == WorldInstanceHost.BootInstanceName)) {
                        // The seat is returned once: it stands in the boot world as one body, and the client owes no
                        // restoration of another for it.
                        returned = true;
                        owedAtHome = client.TimelineResetRefusal(source: boot.Server);
                        Assert.True(condition: boot.Server.Population.IsActive(index: 0));
                    }
                    return returned && SeatAway() && (GuestBodies(host: candidate, session: session, world: "alpha") == 1);
                },
                each: StepClient,
                what: "the client's seat came home and joined the restored group again"
            );
            Assert.Null(@object: owedAtHome);
            Assert.Equal(expected: 1, actual: Counts(host: candidate, world: "alpha").MobilityCredentials);
            // The seat came home once, as the body it left with, and its new traveler is left where it stands.
            await silo.UntilAsync(done: () => (clientLog.Settled == 2), each: StepClient, what: "the client settled its return and then its second entry");
            for (var step = 0; (step < 16); step++) {
                StepClient();
            }
            Assert.True(condition: SeatAway());
            Assert.Equal(expected: 1, actual: Returns());
            Assert.DoesNotContain(collection: clientLines, filter: static line => line.Contains(value: "seated again as a new body", comparisonType: StringComparison.Ordinal));

            // 6. The player walks on to beta. alpha keeps the session's credential and a route; beta holds the body.
            var walking = candidate.Instances.TryGet(instance: out var candidateAlpha, name: "alpha");

            Assert.True(condition: walking);
            var guestSlot = candidateAlpha!.Server.ExecuteAuthorityOperation(operation: () => Enumerable.Range(start: 0, count: candidateAlpha.Server.Population.Capacity).First(predicate: index => (candidateAlpha.Server.Population.ResolveIncarnation(authority: candidateAlpha.Server.AuthorityIdentity, index: index)?.Authority == session)));

            _ = candidate.Instances.EnqueueTransfer(
                actingPrincipal: candidateAlpha.Server.Population.PeerPrincipal(index: guestSlot),
                destination: WorldInstanceHost.TransferDestination.Existing(name: "beta"),
                scope: WorldInstanceHost.TransferScope.Body,
                sourceInstance: "alpha",
                sourceSlot: guestSlot
            );
            await silo.UntilAsync(
                done: () => (GuestBodies(host: candidate, session: session, world: "beta") == 1) && (GuestBodies(host: candidate, session: session, world: "alpha") == 0),
                each: StepClient,
                what: "the player's body walked from alpha to beta"
            );
            Assert.Equal(expected: 1, actual: Counts(host: candidate, world: "alpha").MobilityCredentials);
            Assert.Equal(expected: 1, actual: Counts(host: candidate, world: "beta").MobilityCredentials);
            Assert.NotEqual(expected: 0, actual: Counts(host: candidate, world: "alpha").GuestTransferKeys);

            // 7. The client goes away. alpha holds the credential and no body, so the absence is passed to beta, where
            // the body parks and its reconnect grace runs out on the tick. beta retires it, alpha is told, and neither
            // row keeps a body, a credential or a ledger row of the session.
            clientClosed = true;
            client.Dispose();
            clientNetwork.Dispose();
            await silo.UntilAsync(
                done: () => (GuestBodies(host: candidate, session: session, world: "beta") == 0) && (Counts(host: candidate, world: "alpha").MobilityCredentials == 0),
                what: "the departed player's traveler was retired at beta when its grace ran out, and at alpha with it"
            );
            foreach (var identity in identities) {
                AssertNoGuestRows(host: candidate, world: identity.World.Value);
            }
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
    private const int GuestKeysPerSession = 4;
    private const int GuestKeys = 8;

    // A crossing log that takes every record at once except the first settlement, which waits for the probe's answer,
    // as a store that is slow to answer one write does.
    private sealed class HeldSettlementLog : IWorldCrossingLog {
        private TaskCompletionSource<WorldCrossingAppend>? m_held;
        private bool m_answered;
        private int m_settled;

        /// <summary>Gets whether a settlement record is waiting for its answer.</summary>
        public bool Holding => (Volatile.Read(location: ref m_held) is not null);
        /// <summary>Gets how many settlement records are durable.</summary>
        public int Settled => Volatile.Read(location: ref m_settled);

        public ValueTask<WorldCrossingAppend> Append(in WorldCrossingEntry entry) {
            if (entry.Record is not WorldCrossingRecord.Settlement) {
                return new ValueTask<WorldCrossingAppend>(result: WorldCrossingAppend.Durable);
            }
            if (m_answered) {
                _ = Interlocked.Increment(location: ref m_settled);
                return new ValueTask<WorldCrossingAppend>(result: WorldCrossingAppend.Durable);
            }
            var held = new TaskCompletionSource<WorldCrossingAppend>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(location: ref m_held, value: held);
            return new ValueTask<WorldCrossingAppend>(task: held.Task);
        }
        /// <summary>Answers the held settlement record; every later record is durable at once.</summary>
        public void Answer(WorldCrossingDurability durability) {
            m_answered = true;
            if (durability == WorldCrossingDurability.Durable) {
                _ = Interlocked.Increment(location: ref m_settled);
            }
            Interlocked.Exchange(location1: ref m_held, value: null)?.SetResult(result: new WorldCrossingAppend(
                Durability: durability,
                Reason: ((durability == WorldCrossingDurability.Durable) ? string.Empty : $"the store answered {durability}")
            ));
        }
    }
    // Every line a host narrated, in order, read from the probe's thread while route observers narrate from theirs.
    private sealed class NarratedLines : IWorldNarrationSink, IEnumerable<string> {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> m_lines = new();

        public IEnumerator<string> GetEnumerator() => m_lines.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Narrate(in WorldNarration narration) => m_lines.Enqueue(item: narration.Text);
    }
    // A signing oracle the engine's own client never is: the issuer vouches for its key as it does for a player's, and
    // its claims sign the challenge alone, with no session bytes behind it.
    private sealed class SessionlessOracle(ECDsa key, WorldIssuedIdentity identity) : ISigningOracle {
        public byte[] Sign(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken) {
            var codec = new CborAttestationCodec();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return AttestationChainEnvelope.Encode(
                chain: identity.Chain,
                claim: codec.EncodeAttestation(attestation: AttestationSigner.SignClaim(
                    audience: WorldAttestedAuthenticator.Audience,
                    claimBytes: challenge.ToArray(),
                    codec: codec,
                    domain: identity.Domain,
                    notAfter: Math.Min(val1: (now + 60L), val2: identity.NotAfter),
                    notBefore: (now - 60L),
                    purpose: WorldAttestedAuthenticator.Purpose,
                    sequence: null,
                    signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                    signerKey: key,
                    subject: identity.Subject
                ))
            );
        }
    }
    // Opens a federation connection to a door and answers its challenge with the oracle's claim, as a peer's lane does.
    // Returns the door's answer: Authenticated, or a refusal and its text.
    private static async Task<(byte Kind, string Text)> FederationHandshakeAsync(string endpoint, ISigningOracle oracle) {
        // A network on the wall clock, as the probe's own client is: the silo's row verifies on system time.
        using var network = new WorldPeerNetwork(transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout);
        var address = IPEndPoint.Parse(s: endpoint);
        await using var stream = ((Puck.Networking.Peers.PeerStream)await network.ConnectAsync(ct: Token, endpoint: address));


        await HandshakeWireFormat.WriteHelloAsync(ct: Token, key: WorldFederationCodec.WireKey, stream: stream);
        var challenge = await WorldFederationCodec.ReadResponseAsync(ct: Token, stream: stream);

        Assert.True(condition: challenge.Ok, userMessage: challenge.Failure.ToString());
        Assert.Equal(expected: ((byte)WorldFederationResponse.Challenge), actual: challenge.Kind);
        var counterChallenge = RandomNumberGenerator.GetBytes(count: WorldAdmissionDoor.ChallengeBytes);
        var proof = oracle.Sign(
            cancellationToken: Token,
            challenge: WorldFederationCodec.BindProofChallenge(
                challenge: challenge.Body.Span,
                clientKeyHash: network.Peer.Id.KeyHash,
                counterChallenge: counterChallenge,
                serverKeyHash: stream.Link.RemoteId.KeyHash,
                serverProof: false
            )
        );

        await WorldFederationCodec.WriteRequestAsync(
            body: WorldFederationCodec.EncodeAuthentication(challenge: counterChallenge, proof: proof),
            ct: Token,
            kind: WorldFederationRequest.Authenticate,
            stream: stream
        );
        var reply = await WorldFederationCodec.ReadResponseAsync(ct: Token, stream: stream);

        Assert.True(condition: reply.Ok, userMessage: reply.Failure.ToString());
        return (reply.Kind, ((reply.Kind == ((byte)WorldFederationResponse.Refusal)) ? System.Text.Encoding.UTF8.GetString(bytes: reply.Body.Span) : string.Empty));
    }

    // A row that holds nothing of any guest: no held transaction, no credential, no leased traveler and no ledger row
    // a guest's session owns. Rows the group's own worlds hold are theirs and stay.
    private static void AssertNoGuestRows(WorldSiloHost host, string world) {
        var counts = Counts(host: host, world: world);

        Assert.Equal(
            expected: new WorldTransferTableCounts(ActiveTransactions: 0, GuestTransferKeys: 0, MobilityCredentials: 0, MobilityLeases: 0, TransferKeys: counts.TransferKeys),
            actual: counts
        );
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
