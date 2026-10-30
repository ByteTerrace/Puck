using System.Text;
using Puck.Assets;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The companion trust boundary: a world runs a visitor's driver only when it declares the visitor's role,
/// and an <c>accept</c> world only a module whose bytes it holds and verifies against the visitor's pin; whatever
/// drives a role holds Drive over that role's companions and nothing else.</summary>
public sealed class CompanionRoleLawTests {
    private const string SourceAuthority = "machine-a/home";

    /// <summary>A world that declares nothing for a companion's role refuses it at reservation, as a role refusal,
    /// holding nothing; the same traveller without a role is admitted.</summary>
    [Fact]
    public void AWorldDeclaringNoRoleRefusesTheCompanionAtReservation() {
        using var fixture = Fixtures.FreshServer(definition: Document(roles: []));
        var refused = fixture.Server.ReserveTransfer(request: Request(transferId: 1, companion: Companion(driver: Pin(text: "no role here"))));

        Assert.False(condition: refused.Accepted);
        Assert.True(condition: refused.CompanionRefused);
        Assert.Contains(expectedSubstring: "declares no 'companion' role", actualString: refused.Reason);
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: fixture.Server.TransferStatus(sourceAuthority: SourceAuthority, transferId: 1));

        var control = fixture.Server.ReserveTransfer(request: Request(transferId: 2, companion: null));

        Assert.True(condition: control.Accepted, userMessage: control.Reason);
    }

    /// <summary>An <c>accept</c> world refuses a visiting driver its content store does not hold, and one whose held
    /// bytes do not hash to the pin, and admits the companion once the store holds the pinned bytes.</summary>
    [Fact]
    public void AnAcceptWorldRunsOnlyAHashVerifiedDriver() {
        using var fixture = Fixtures.FreshServer(definition: Document(roles: [new WorldRole(Name: "companion", Accept: true)]));
        var module = Encoding.UTF8.GetBytes(s: "(module) ;; the visiting driver of " + nameof(AnAcceptWorldRunsOnlyAHashVerifiedDriver));
        var pin = ContentPin.Compute(content: module);
        var missing = fixture.Server.ReserveTransfer(request: Request(transferId: 1, companion: Companion(driver: pin.ToString())));

        Assert.False(condition: missing.Accepted);
        Assert.True(condition: missing.CompanionRefused);
        Assert.Contains(expectedSubstring: "content store", actualString: missing.Reason);

        // Bytes under the pin's object path that are not the pinned module: the store holds something, and it is not
        // what the visitor names.
        var path = ContentAddressedStore.ObjectPath(pin: pin, root: WorldProjectionContent.Shared.Root);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllBytes(bytes: Encoding.UTF8.GetBytes(s: "(module) ;; not the pinned driver"), path: path);
        var tampered = fixture.Server.ReserveTransfer(request: Request(transferId: 2, companion: Companion(driver: pin.ToString())));

        Assert.False(condition: tampered.Accepted);
        Assert.True(condition: tampered.CompanionRefused);

        File.Delete(path: path);
        _ = WorldProjectionContent.Shared.Put(content: module);
        var verified = fixture.Server.ReserveTransfer(request: Request(transferId: 3, companion: Companion(driver: pin.ToString())));

        Assert.True(condition: verified.Accepted, userMessage: verified.Reason);
    }

    /// <summary>A role's driver holds Drive and Observe over the companion that landed and Drive over no other body,
    /// whatever else was granted to it.</summary>
    [Fact]
    public void ARoleDriverDrivesOnlyItsCompanion() {
        using var fixture = Fixtures.FreshServer(definition: Document(roles: [new WorldRole(Name: "companion", Accept: true)]));
        var module = Encoding.UTF8.GetBytes(s: "(module) ;; the visiting driver of " + nameof(ARoleDriverDrivesOnlyItsCompanion));
        var pin = ContentPin.Compute(content: module);
        var driver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: pin.ToString(), role: "companion"));
        var stray = GrantSubject.Body(index: 0);

        _ = WorldProjectionContent.Shared.Put(content: module);
        Assert.False(condition: fixture.Server.GrantTable.TryGrant(
            grant: new WorldGrant(Budget: 4, Capability: WorldCapability.Drive, Exclusive: false, Grantee: driver, Subject: stray),
            reason: out _
        ));

        var landed = Land(fixture: fixture, transferId: 1, companion: Companion(driver: pin.ToString()));

        fixture.Step();
        Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: driver, subject: GrantSubject.Body(index: landed)).IsAllowed);
        Assert.True(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: driver, subject: GrantSubject.Body(index: landed)).IsAllowed);
        Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: driver, subject: stray).IsAllowed);
        for (var body = 0; (body < fixture.Server.Population.Capacity); body++) {
            if (body != landed) {
                Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: driver, subject: GrantSubject.Body(index: body)).IsAllowed);
            }
        }
    }

    private static WorldTransferCompanion Companion(string? driver) => new(Driver: driver, Follows: null, Home: "origin/world", Role: "companion");
    private static string Pin(string text) => ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: text)).ToString();
    private static WorldDefinition Document(IReadOnlyList<WorldRole> roles) {
        var document = Fixtures.BuildDocument();

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2),
                NetworkPlayers = 2,
            },
            Admission = [new WorldAdmissionEntry(
                Domain: SourceAuthority,
                Mode: WorldAdmissionTrustMode.FederatedAuthority,
                Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Budget: 8)]
            )],
            RolesRaw = roles,
        };
    }
    private static int Land(WorldFixture fixture, ulong transferId, WorldTransferCompanion companion) {
        var reservation = fixture.Server.ReserveTransfer(request: Request(transferId: transferId, companion: companion));

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        Assert.True(
            condition: (fixture.Server.CommitTransfer(
                sourceAuthority: SourceAuthority,
                transferId: transferId,
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    Arrival: WorldTransferArrival.Spawn,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default
                )],
                reason: out var reason
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );

        return reservation.BodyIndices.Single();
    }
    private static WorldTransferReservationRequest Request(ulong transferId, WorldTransferCompanion? companion) => new(
        TransferId: transferId,
        SourceAuthority: SourceAuthority,
        SourceRateHz: 240,
        SourceTick: 0,
        DeadlineSourceTick: 60,
        Border: "door",
        BorderCapacity: null,
        PartyAllOrNothing: true,
        PeerAdmission: true,
        Members: [new WorldTransferReservationMember(
            Principal: Principal.World,
            PreferredSlot: WorldBodiesLimits.LocalSeatCount,
            Identity: null,
            Source: IntentSource.Idle,
            BodyColor: default,
            CatalogRig: 0,
            Mobility: new WorldMobilityIdentity(
                DepartedFrom: new WorldEntityAddress(Authority: "origin/world", Generation: 3, Index: 42),
                Epoch: 0,
                Incarnation: new WorldEntityAddress(Authority: "origin/world", Generation: ((int)transferId), Index: 42)
            ),
            Companion: companion
        )]
    );
}
