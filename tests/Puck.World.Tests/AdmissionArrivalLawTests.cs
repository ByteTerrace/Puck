using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the arrival arm of the admission door: a traveller handed over by an authenticated federation authority
/// materializes authority only through <see cref="WorldAdmissionDoor.TryAdmitArrival"/>'s verdict, and only what the
/// destination document itself authored.
/// </summary>
public sealed class AdmissionArrivalLawTests {
    private const string SourceAuthority = "machine-a/plaza";

    /// <summary>A traveller from an authority no admission entry names is refused at reserve. The control is the
    /// identical reservation against a document whose admission section names that authority.</summary>
    [Fact]
    public void Arrival_RequiresAnAuthorizingAdmissionEntry_ControlNamedAuthorityLands() {
        Laws.RefusalWithControl(
            lawId: "admission.arrival-requires-an-authored-authority",
            deniedOutcome: static () => ReserveArrival(admission: [Arrivals(domain: "machine-z/elsewhere")]),
            controlOutcome: static () => ReserveArrival(admission: [Arrivals(domain: SourceAuthority)]));
    }
    /// <summary>An authored entry that confers no Drive over the assigned body is refused at reserve rather than
    /// landing a body nothing may move. The control is the same entry carrying the Drive template.</summary>
    [Fact]
    public void Arrival_RequiresDriveOverTheAssignedBody_ControlDriveTemplateLands() {
        Laws.RefusalWithControl(
            lawId: "admission.arrival-requires-drive-over-the-assigned-body",
            deniedOutcome: static () => ReserveArrival(admission: [Arrivals(domain: SourceAuthority, drive: false)]),
            controlOutcome: static () => ReserveArrival(admission: [Arrivals(domain: SourceAuthority)]));
    }
    /// <summary>A committed arrival holds exactly the authored templates, resolved onto the body it was assigned —
    /// no blanket <c>Control</c>/<c>all</c> beyond what the document names.</summary>
    [Fact]
    public void CommittedArrival_HoldsExactlyTheAuthoredTemplates() {
        using var fixture = Fixtures.FreshServer(definition: Document(admission: [Arrivals(domain: SourceAuthority, control: false)]));
        var slot = Land(fixture: fixture, transferId: 41);
        var rows = fixture.Server.GrantRows(principal: fixture.Server.Population.PeerPrincipal(index: slot)).ToArray();

        Assert.Equal(
            expected: new[] {
                (WorldCapability.Drive, GrantSubject.Body(index: slot)),
                (WorldCapability.Observe, GrantSubject.Body(index: slot)),
            }.OrderBy(keySelector: row => row.Item1).ToArray(),
            actual: rows.Select(selector: row => (row.Capability, row.Subject)).OrderBy(keySelector: row => row.Capability).ToArray());
        Assert.DoesNotContain(collection: rows, filter: row => (row.Subject.Kind == GrantSubjectKind.All));
    }
    /// <summary>The admitted body's identity columns name the authority the door decided against, never the profile
    /// the arriving payload asserts — an unverified string must not reach a column that elsewhere means
    /// door-verified.</summary>
    [Fact]
    public void CommittedArrival_IdentityNamesTheAuthenticatedAuthority_NotTheCarriedProfile() {
        var document = Document(admission: [Arrivals(domain: SourceAuthority)]);

        using var fixture = Fixtures.FreshServer(definition: document);
        var carried = WorldIdentity.Pinned(name: "forged-subject", moveSpeed: Puck.Maths.FixedQ4816.One, turnSpeed: Puck.Maths.FixedQ4816.One, defaults: document.PlayerDefaults);
        var slot = Land(fixture: fixture, transferId: 42, identity: carried);

        var (domain, subject) = fixture.Server.Population.PeerIdentity(bodyIndex: slot);

        Assert.Equal(expected: "forged-subject", actual: carried.Name);

        Assert.Equal(actual: domain, expected: SourceAuthority);
        Assert.Equal(actual: subject, expected: string.Empty);
    }
    /// <summary>An entry naming the exact authority decides it, whichever side of the wildcard row it is authored
    /// on — otherwise a permissive catch-all would silently outrank a narrower authored intent.</summary>
    [Fact]
    public void NamedAuthority_OutranksTheWildcardRow_InEitherAuthoredOrder() {
        var narrow = Arrivals(domain: SourceAuthority, budget: 7);
        var wildcard = Arrivals(domain: WorldAdmissionEntry.AnyAuthority, budget: 9);

        foreach (var entries in new[] { new[] { wildcard, narrow }, new[] { narrow, wildcard } }) {
            Assert.Null(@object: WorldAdmissionDoor.TryAdmitArrival(entries: entries, sourceAuthority: SourceAuthority, verdict: out var verdict));
            Assert.Equal(expected: ((ushort)7), actual: verdict!.Templates.Single(predicate: template => (template.Capability == WorldCapability.Drive)).Budget);
        }
    }
    /// <summary>A attestation claim can never verify against a keyless arrival row: a document authoring arrivals alone
    /// still admits no connecting peer.</summary>
    [Fact]
    public void ArrivalRows_AreInvisibleToTheAttestationClaimArm() {
        Assert.False(condition: WorldAdmissionDoor.TryMatchEntry(entries: [Arrivals(domain: SourceAuthority)], domain: SourceAuthority, subject: null, verdict: out _));
    }

    private static bool ReserveArrival(IReadOnlyList<WorldAdmissionEntry> admission) {
        using var fixture = Fixtures.FreshServer(definition: Document(admission: admission));

        return fixture.Server.ReserveTransfer(request: Request(identity: null, transferId: 17)).Accepted;
    }
    private static int Land(WorldFixture fixture, ulong transferId, WorldIdentity? identity = null) {
        var reservation = fixture.Server.ReserveTransfer(request: Request(identity: identity, transferId: transferId));

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(
            condition: fixture.Server.CommitTransfer(
                sourceAuthority: SourceAuthority,
                transferId: transferId,
                members: [new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default)],
                reason: out var reason),
            userMessage: reason);

        return reservation.BodyIndices.Single();
    }
    private static WorldTransferReservationRequest Request(ulong transferId, WorldIdentity? identity) => new(
        TransferId: transferId,
        SourceAuthority: SourceAuthority,
        SourceRateHz: 240,
        SourceTick: 0,
        DeadlineSourceTick: 60,
        Border: "east",
        BorderCapacity: null,
        PartyAllOrNothing: true,
        PeerAdmission: true,
        Members: [new WorldTransferReservationMember(
            Principal: WorldPrincipal.Console,
            PreferredSlot: WorldPopulation.LocalSeatCount,
            Identity: identity,
            Source: IntentSource.Live,
            BodyColor: default,
            CatalogRig: 0,
            Mobility: new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "origin/world", Index: 4, Generation: 7), Epoch: 0))]);
    private static WorldAdmissionEntry Arrivals(string domain, bool drive = true, bool control = true, ushort budget = 64) {
        var grants = new List<WorldAdmissionGrant> { new(Capability: WorldCapability.Observe, Budget: budget) };

        if (drive) {
            grants.Insert(index: 0, item: new WorldAdmissionGrant(Capability: WorldCapability.Drive, Exclusive: true, Budget: budget));
        }

        if (control) {
            grants.Add(item: new WorldAdmissionGrant(Capability: WorldCapability.Control, Subject: GrantSubject.All));
        }

        return new WorldAdmissionEntry(
            Domain: domain,
            Subject: null,
            Mode: WorldAdmissionTrustMode.FederatedAuthority,
            Algorithm: string.Empty,
            PublicKey: string.Empty,
            Grants: grants);
    }
    private static WorldDefinition Document(IReadOnlyList<WorldAdmissionEntry> admission) {
        var document = Fixtures.BuildDocument();

        return document with {
            Population = document.Population with {
                Capacity = (WorldPopulation.LocalSeatCount + 2),
                NetworkPlayers = 2,
            },
            Admission = admission,
        };
    }
}
