using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using System.Numerics;

using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

using static Puck.World.Tests.AdmissionWireFixture;

namespace Puck.World.Tests;

/// <summary>
/// The unembodied session principal's laws. A session is admitted by the world's own <c>admission</c> policy (the
/// arrival verdict for the authority a viewer observes from) with no population entry and no body: it holds only the
/// verdict's rows that name a subject, acts through the ordinary grant door, and ends by revoking every row it holds.
/// An embodiment adds the verdict's body-relative rows to the same principal. Every case pairs a denial with the same
/// act under one reversed fact.
/// </summary>
public sealed class SessionPrincipalLawTests {
    /// <summary>The authority every session in these laws observes from; the wildcard entry admits it.</summary>
    private const string Viewer = "viewer/portal";
    /// <summary>The placement row the admission verdict grants a session, absent from the fixture document.</summary>
    private const string SlotRow = "slot-nw";
    /// <summary>A placement row the verdict does not grant.</summary>
    private const string ForeignRow = "slot-se";

    private static WorldAdmissionEntry Admission() => new(
        Domain: WorldAdmissionEntry.AnyAuthority,
        Subject: null,
        Mode: WorldAdmissionTrustMode.FederatedAuthority,
        Algorithm: string.Empty,
        PublicKey: string.Empty,
        Grants: [
            new WorldAdmissionGrant(
                Capability: WorldCapability.Mutate,
                Subject: GrantSubject.Placement(id: SlotRow),
                Budget: 16,
                KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements)
            ),
            new WorldAdmissionGrant(
                Capability: WorldCapability.Observe,
                Budget: 64
            ),
            new WorldAdmissionGrant(
                Capability: WorldCapability.Drive,
                Budget: 64
            ),
        ]
    );
    // The collider-bearing fixture document (it carries the "ball" creation a slot placement names), with one peer slot
    // and the admission row above.
    private static WorldDefinition Document() {
        var document = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
                NetworkPlayers = 1,
            },
            Admission = [Admission()],
        };
    }
    private static Principal AdmitSession(WorldFixture fixture) {
        Assert.True(
            condition: fixture.Server.TryAdmitSession(
                refusal: out var refusal,
                session: out var session,
                sourceAuthority: Viewer,
                tier: out _
            ),
            userMessage: refusal
        );

        return session;
    }
    // Sets the census through the ordinary session lever and returns the peer slot's body index and generation: a
    // simulated body no remote peer occupies, which is what a session embodies.
    private static (int Index, int Generation) Census(WorldFixture fixture, int count) {
        var reply = fixture.Server.ApplySession(request: new SessionRequest.SetPopulation(
            Count: count,
            Principal: Principal.Console
        ));

        Assert.True(
            condition: reply.Accepted,
            userMessage: reply.Reason
        );
        fixture.Step();

        const int Index = WorldBodiesLimits.LocalSeatCount;

        return (Index, fixture.Server.Population.Generation(index: Index));
    }
    private static bool Drives(WorldFixture fixture, Principal principal, int body) => fixture.Server.Grants.Allows(
        capability: WorldCapability.Drive,
        principal: principal,
        subject: GrantSubject.Body(index: body)
    );
    private static WorldMutation.UpsertPlacement Upsert(Principal principal, string row) => new(
        Placement: new WorldPlacement(
            PrototypeId: "ball",
            Id: row,
            Position: new Vector3(
                x: 12f,
                y: 0f,
                z: 12f
            ),
            Scale: 2f,
            YawDegrees: 0f
        ),
        Principal: principal
    );
    // Submits a placement upsert through the one submission dispatch, steps, and returns what came back and whether
    // the document changed.
    private static (WorldSubmissionResult? Result, bool Changed) Submit(WorldFixture fixture, Principal principal, string row) {
        var before = fixture.DefinitionBytes();
        WorldSubmissionResult? result = null;

        fixture.Server.Submit(
            completion: value => result = value,
            envelope: new SubmissionEnvelope(
                ConnectionId: SubmissionEnvelope.LocalConnectionId,
                SessionGeneration: 0,
                Sequence: 1,
                CorrelationId: 1,
                Principal: principal,
                Payload: new WorldSubmissionPayload.Mutation(Value: Upsert(
                    principal: principal,
                    row: row
                )),
                OperationId: Guid.NewGuid()
            )
        );
        fixture.Step();

        return (result, !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes()));
    }

    [Fact]
    public void UnembodiedJoin_TakesNoBodyAndNoBodyAuthority_EmbodimentAddsDriveToTheSamePrincipal() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Census(
            count: 1,
            fixture: fixture
        );
        var activeBefore = Enumerable.Range(
            count: fixture.Server.Population.Capacity,
            start: 0
        ).Count(predicate: index => fixture.Server.Population.IsActive(index: index));
        var session = AdmitSession(fixture: fixture);

        Assert.Equal(
            actual: session.Kind,
            expected: PrincipalKind.Session
        );
        Assert.Equal(
            actual: Enumerable.Range(
                count: fixture.Server.Population.Capacity,
                start: 0
            ).Count(predicate: index => fixture.Server.Population.IsActive(index: index)),
            expected: activeBefore
        );
        Assert.DoesNotContain(
            collection: fixture.Server.GrantRows(principal: session),
            filter: row => (row.Subject.Kind == GrantSubjectKind.Body)
        );
        Laws.RefusalWithControl(
            lawId: "session.embodiment-adds-drive-to-the-same-principal",
            deniedOutcome: () => Drives(
                body: body.Index,
                fixture: fixture,
                principal: session
            ),
            controlOutcome: () => {
                Assert.True(
                    condition: fixture.Server.TryEmbodySession(
                        bodyIndex: body.Index,
                        refusal: out var refusal,
                        session: session
                    ),
                    userMessage: refusal
                );

                return Drives(
                    body: body.Index,
                    fixture: fixture,
                    principal: session
                );
            }
        );
        Assert.Contains(
            collection: fixture.Server.GrantRows(principal: session),
            filter: row => (row.Subject == GrantSubject.Placement(id: SlotRow))
        );
    }
    [Fact]
    public void AnEmbodiedSessionsIntent_DrivesTheBodyItNames_AndOnlyOnceEmbodied() {
        FixedQ4816 ForwardAfterIntent(bool embody) {
            using var fixture = Fixtures.FreshServer(definition: Document());
            var body = Census(
                count: 1,
                fixture: fixture
            );
            var session = AdmitSession(fixture: fixture);

            if (embody) {
                Assert.True(
                    condition: fixture.Server.TryEmbodySession(
                        bodyIndex: body.Index,
                        refusal: out var refusal,
                        session: session
                    ),
                    userMessage: refusal
                );
            }

            // The census body takes a submitted intent, as the body.control verb sets it.
            fixture.Server.Body(index: body.Index)!.SetIntentSource(source: IntentSource.Live);

            var channels = new ChannelValues();

            channels[0] = FixedQ4816.One;
            fixture.Server.EnqueueIntent(submission: new IntentSubmission(
                EntityIndex: body.Index,
                Intent: new PlayerIntent(Channels: channels),
                Principal: session,
                Tick: fixture.Server.NextInputTick
            ));
            fixture.Step();

            return fixture.Server.Body(index: body.Index)!.EngagedIntent[0];
        }

        Laws.RefusalWithControl(
            lawId: "session.embodied-intent-drives-its-body",
            deniedOutcome: () => (ForwardAfterIntent(embody: false) == FixedQ4816.One),
            controlOutcome: () => (ForwardAfterIntent(embody: true) == FixedQ4816.One)
        );
    }
    [Fact]
    public void GrantAheadOfAdmission_NeverReachesTheSessionThatTakesItsEpoch_LiveSessionHoldsTheSameGrant() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var foreign = GrantSubject.Placement(id: ForeignRow);
        var ahead = Principal.Session(
            epoch: 1,
            ordinal: 0
        );

        bool GrantAndObserveHeld(Principal grantee) {
            fixture.Server.Grant(
                actor: Principal.Console,
                grant: new WorldGrant(
                    Budget: 16,
                    Capability: WorldCapability.Mutate,
                    Exclusive: false,
                    KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements),
                    Grantee: grantee,
                    Subject: foreign
                )
            );

            return fixture.Server.Grants.Allows(
                capability: WorldCapability.Mutate,
                principal: grantee,
                subject: foreign
            );
        }

        Laws.RefusalWithControl(
            lawId: "session.rows-exist-only-while-it-lives",
            deniedOutcome: () => {
                _ = GrantAndObserveHeld(grantee: ahead);

                var session = AdmitSession(fixture: fixture);

                Assert.Equal(
                    actual: session,
                    expected: ahead
                );

                return fixture.Server.Grants.Allows(
                    capability: WorldCapability.Mutate,
                    principal: session,
                    subject: foreign
                );
            },
            controlOutcome: () => GrantAndObserveHeld(grantee: ahead)
        );
    }
    [Fact]
    public void SessionMutation_GrantedRowAccepted_UngrantedRowRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var session = AdmitSession(fixture: fixture);

        Laws.RefusalWithControl(
            lawId: "session.acts-only-through-its-verdict-rows",
            deniedOutcome: () => Submit(
                fixture: fixture,
                principal: session,
                row: ForeignRow
            ).Changed,
            controlOutcome: () => Submit(
                fixture: fixture,
                principal: session,
                row: SlotRow
            ).Changed
        );
    }
    [Fact]
    public void EndedSession_RevokesItsRows_AndItsStaleEpochIsRefusedByName() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var session = AdmitSession(fixture: fixture);
        var live = Submit(
            fixture: fixture,
            principal: session,
            row: SlotRow
        );

        Assert.True(
            condition: live.Changed,
            userMessage: $"a live session's granted mutation did not apply: {live.Result}"
        );
        Assert.True(
            condition: fixture.Server.EndSession(
                refusal: out var refusal,
                session: session
            ),
            userMessage: refusal
        );
        Assert.Empty(collection: fixture.Server.GrantRows(principal: session));
        Assert.False(condition: fixture.Server.IsLiveSession(principal: session));

        var stale = Submit(
            fixture: fixture,
            principal: session,
            row: SlotRow
        );
        var refused = Assert.IsType<WorldSubmissionResult.Refusal>(@object: stale.Result);

        Assert.Equal(
            actual: refused.Code,
            expected: WorldServer.StaleSessionCode
        );
        Assert.False(condition: stale.Changed);

        // The ordinal is reused under a later epoch, so the retired principal never names the new session.
        var next = AdmitSession(fixture: fixture);

        Assert.Equal(
            actual: next.Index,
            expected: session.Index
        );
        Assert.NotEqual(
            actual: next,
            expected: session
        );
        Assert.False(condition: fixture.Server.IsLiveSession(principal: session));
    }
    [Fact]
    public void Rebuild_EndsEverySession_SoEmbodimentIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Census(
            count: 1,
            fixture: fixture
        );
        var session = AdmitSession(fixture: fixture);

        Assert.True(condition: fixture.Server.IsLiveSession(principal: session));
        fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Reset,
                Definition: null,
                PathHint: null,
                Force: false
            )
        );
        fixture.Step();

        Assert.False(
            condition: fixture.Server.TryEmbodySession(
                bodyIndex: body.Index,
                refusal: out var refusal,
                session: session
            ),
            userMessage: "an embodiment after the rebuild was admitted for a session the rebuild ended"
        );
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "is not a live session"
        );
        Assert.Empty(collection: fixture.Server.GrantRows(principal: session));
    }
    [Fact]
    public void DepartedBody_SlotReusedByANewGeneration_SessionDrivesNotTheNewOccupant() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var first = Census(
            count: 1,
            fixture: fixture
        );
        var session = AdmitSession(fixture: fixture);

        Assert.True(
            condition: fixture.Server.TryEmbodySession(
                bodyIndex: first.Index,
                refusal: out var refusal,
                session: session
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: Drives(
                body: first.Index,
                fixture: fixture,
                principal: session
            ),
            userMessage: "the embodied session does not drive the body it embodied"
        );

        _ = Census(
            count: 0,
            fixture: fixture
        );

        var second = Census(
            count: 1,
            fixture: fixture
        );

        Assert.Equal(
            actual: second.Index,
            expected: first.Index
        );
        Assert.NotEqual(
            actual: second.Generation,
            expected: first.Generation
        );
        Assert.False(
            condition: Drives(
                body: second.Index,
                fixture: fixture,
                principal: session
            ),
            userMessage: "the session still drives the body after a new generation took it"
        );
        Assert.True(condition: fixture.Server.IsLiveSession(principal: session));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void RecordedSessionMutation_ReplaysWithTheSameAcceptedOutcome(bool admittedBeforeArm) {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");
        using var fixture = Fixtures.FreshServer(definition: Document());
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: new LoopbackTransport(server: fixture.Server),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"session-mutation-{Guid.NewGuid():N}";
        var early = (admittedBeforeArm
            ? AdmitSession(fixture: fixture)
            : (Principal?)null);

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        var session = (early ?? AdmitSession(fixture: fixture));
        var accepted = Submit(
            fixture: fixture,
            principal: session,
            row: SlotRow
        );

        tape.NoteTick();

        Assert.True(
            condition: fixture.Server.EndSession(
                refusal: out refusal,
                session: session
            ),
            userMessage: refusal
        );
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();

        // The session's mutation was accepted live only because its admission minted the row; a re-drive that did not
        // reproduce the admission would refuse it, and Verify refuses a mutation whose outcome moved by name.
        Assert.True(
            condition: accepted.Changed,
            userMessage: $"the session's granted mutation did not apply live: {accepted.Result}"
        );

        using (var stream = File.OpenRead(path: tape.PathFor(name: name))) {
            var entries = WorldReplaySnapshot.Read(stream: stream).Ticks
                .SelectMany(selector: static tick => tick.Authority)
                .ToList();

            // The admission and the end, each at its point of effect; the mutation between them rides as its own entry.
            Assert.Equal(
                actual: entries.Count(predicate: static entry => (entry.GetType().Name == "SessionEvent")),
                expected: 2
            );
            Assert.Contains(
                collection: entries,
                filter: static entry => (entry.GetType().Name == "Mutation")
            );
        }

        Assert.Equal(
            actual: tape.Verify(name: name).Primary.DivergedAt,
            expected: -1
        );
    }
    [Fact]
    public async Task PeerWire_RefusesASubmissionNamingASession_ControlSeatAnswers() {
        var identity = GenerateIdentity(subject: "session-principal-peer");

        try {
            var document = BuildAdmissionDocument(entry: BuildEntry(
                grants: [new WorldAdmissionGrant(
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.Body(index: PeerBodyIndex),
                    Budget: 100
                )],
                identity: identity
            ));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = StartHost(
                clock: out _,
                server: fixture.Server
            );

            await using (StartPump(
                fixture: fixture,
                host: host
            )) {
                var ct = TestContext.Current.CancellationToken;
                var admitted = await ConnectAndAdmitAsync(
                    ct: ct,
                    host: host,
                    identity: identity
                );

                using (admitted.Client) {
                    var stream = admitted.Client.GetStream();
                    var refused = await SubmitRawAsync(
                        ct: ct,
                        query: new WorldQuery.GrantAllows(
                            Principal: Principal.Session(
                                epoch: 1,
                                ordinal: 0
                            ),
                            Capability: WorldCapability.Drive,
                            Subject: GrantSubject.Body(index: PeerBodyIndex)
                        ),
                        stream: stream
                    );
                    var answered = await SubmitRawAsync(
                        ct: ct,
                        query: new WorldQuery.GrantAllows(
                            Principal: Principal.Seat(slot: 0),
                            Capability: WorldCapability.Drive,
                            Subject: GrantSubject.Body(index: PeerBodyIndex)
                        ),
                        stream: stream
                    );

                    Assert.Equal(
                        actual: refused.Kind,
                        expected: WorldPeerWireFormat.DownstreamKind.Refusal
                    );
                    Assert.Contains(
                        expectedSubstring: nameof(WorldCodecRefusal.SessionPrincipalRemote),
                        actualString: refused.Text
                    );
                    Assert.Equal(
                        actual: answered.Kind,
                        expected: WorldPeerWireFormat.DownstreamKind.Query
                    );
                }
            }
        } finally {
            identity.Key.Dispose();
        }
    }
    [Fact]
    public void ComposeControl_OntoASessionsSet_IsRefused_OntoTheBodysOwnPeerIsComposed() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var seat = fixture.JoinSeat();
        var body = Census(
            count: 1,
            fixture: fixture
        );
        var session = AdmitSession(fixture: fixture);

        bool Compose(Principal owner) => fixture.Server.Engagement.Compose(
            actingPrincipal: Principal.Console,
            entityIndex: body.Index,
            exclusive: true,
            target: GrantSubject.Body(index: body.Index),
            targetPrincipal: owner
        );

        Laws.RefusalWithControl(
            lawId: "engagement.an-application-set-belongs-to-a-body-owner",
            deniedOutcome: () => Compose(owner: session),
            controlOutcome: () => Compose(owner: fixture.Server.Population.PeerPrincipal(index: body.Index))
        );

        // The session's ordinal is 0, which a set composed for it would have read as seat 0's body.
        Assert.False(condition: seat.Engaged);
    }
    [Fact]
    public void RejoinedSeat_UnderANewGeneration_IsNotDrivenByTheSessionThatEmbodiedItsLastOccupant() {
        var document = Document();

        using var fixture = Fixtures.FreshServer(definition: document with {
            PopulationRaw = document.Population with { ReconnectGraceSeconds = 0f },
        });

        _ = fixture.JoinSeat();
        fixture.Step();

        var generation = fixture.Server.Population.Generation(index: 0);
        var session = AdmitSession(fixture: fixture);

        Assert.True(
            condition: fixture.Server.TryEmbodySession(
                bodyIndex: 0,
                refusal: out var refusal,
                session: session
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: Drives(
                body: 0,
                fixture: fixture,
                principal: session
            ),
            userMessage: "the embodied session does not drive the seat body it embodied"
        );
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Leave(
            Principal: Principal.Seat(slot: 0),
            Slot: 0
        )).Accepted);
        fixture.Step();
        _ = fixture.JoinSeat();
        fixture.Step();

        Assert.NotEqual(
            actual: fixture.Server.Population.Generation(index: 0),
            expected: generation
        );
        Assert.False(
            condition: Drives(
                body: 0,
                fixture: fixture,
                principal: session
            ),
            userMessage: "the session still drives seat 0 after a new generation joined it"
        );
    }
    [Fact]
    public void ARowImage_CarriesItsEventBudget_SoARecordedSessionKeepsItsObservation() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var session = AdmitSession(fixture: fixture);
        var screen = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);

        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 4,
                Capability: WorldCapability.Observe,
                EventBudget: 4,
                Exclusive: false,
                Grantee: session,
                Subject: screen
            )
        );

        var row = Assert.Single(
            collection: fixture.Server.GrantRows(principal: session),
            predicate: row => (row.Subject == screen)
        );

        Assert.Equal(
            actual: row.EventBudget,
            expected: ((ushort)4)
        );
    }
    [Fact]
    public void ReDrivenAdmissionOfAnEarlierEpoch_NeverReopensALaterOne() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var first = AdmitSession(fixture: fixture);

        Assert.True(
            condition: fixture.Server.EndSession(
                refusal: out var refusal,
                session: first
            ),
            userMessage: refusal
        );

        var second = AdmitSession(fixture: fixture);

        Assert.True(
            condition: fixture.Server.EndSession(
                refusal: out refusal,
                session: second
            ),
            userMessage: refusal
        );

        // What a replay drive does with a tape that recorded the first session.
        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionAdmitted(
            MintedGrants: [],
            Session: first,
            Templates: []
        ));
        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionEnded(
            RevokedGrants: [],
            Session: first
        ));

        var third = AdmitSession(fixture: fixture);

        Assert.True(
            condition: (third.Generation > second.Generation),
            userMessage: $"{third.Describe()} reissued an epoch {second.Describe()} had retired"
        );
    }
    [Fact]
    public void RestoredCheckpoint_KeepsRetiredEpochsRetired() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var ended = AdmitSession(fixture: fixture);

        Assert.True(
            condition: fixture.Server.EndSession(
                refusal: out var refusal,
                session: ended
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out refusal
            ),
            userMessage: refusal
        );

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!),
                checkpoint: out var decoded,
                reason: out refusal
            ),
            userMessage: refusal
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);

        using var machines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-session-epochs-");

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "boot",
            machines: machines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );

        {
            Assert.True(
                condition: restored.TryAdmitSession(
                    refusal: out refusal,
                    session: out var next,
                    sourceAuthority: Viewer,
                    tier: out _
                ),
                userMessage: refusal
            );
            Assert.Equal(
                actual: next.Index,
                expected: ended.Index
            );
            Assert.True(
                condition: (next.Generation > ended.Generation),
                userMessage: $"the restored world reissued {next.Describe()} after {ended.Describe()} had ended"
            );
            Assert.False(condition: restored.IsLiveSession(principal: ended));
        }
    }

    private static async Task<(WorldPeerWireFormat.DownstreamKind Kind, string Text)> SubmitRawAsync(Stream stream, WorldQuery query, CancellationToken ct) {
        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                failure: out var failure,
                frame: out var frame,
                payload: new WorldSubmissionPayload.Query(Value: query)
            ),
            userMessage: $"query codec refused: {failure}"
        );

        await stream.WriteAsync(
            buffer: frame,
            cancellationToken: ct
        );
        await stream.FlushAsync(cancellationToken: ct);

        var reply = ((await WorldPeerWireFormat.TryReadDownstreamAsync(
            ct: ct,
            stream: stream
        ))
            ?? throw new InvalidOperationException(message: "connection closed before the reply"));

        return (reply.Kind, WorldPeerWireFormat.DecodeText(body: reply.Body.Span));
    }
}
