using Puck.Commands;

using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// Round-trip laws for the link-query seam's wire arms: the seven <see cref="WorldQuery"/> leaves
/// <c>Client.PlayerRoster</c> reads through <see cref="IServerLink.Query"/> instead of a live
/// <c>Server.WorldServer</c> reference, plus the one new <see cref="SessionRequest"/> leaf
/// (<see cref="SessionRequest.RememberPreferredController"/>). Each round-trips through the SAME
/// <see cref="WorldFrameCodec"/> encode-then-decode path the loopback transport and <c>WorldTcpHost</c> both use —
/// never a second, test-only codec — so a case dropped from <see cref="WorldSubmissionCodec"/>'s closed switch
/// (query kind or type lookup) turns the matching law red rather than passing silently.
/// </summary>
public sealed class LinkQuerySeamCodecLawTests {
    private static WorldQuery RoundTripQuery(WorldQuery query) {
        Assert.True(condition: WorldFrameCodec.TryEncode(payload: new WorldSubmissionPayload.Query(Value: query), frame: out var frame, failure: out var encodeFailure), userMessage: $"encode refused: {encodeFailure}");
        Assert.True(condition: WorldFrameCodec.TryDecode(failure: out var decodeFailure, frame: frame, payload: out var decoded), userMessage: $"decode refused: {decodeFailure}");

        var payload = Assert.IsType<WorldSubmissionPayload.Query>(@object: decoded);

        return payload.Value;
    }
    private static SessionRequest RoundTripSession(SessionRequest request) {
        Assert.True(condition: WorldFrameCodec.TryEncode(payload: new WorldSubmissionPayload.Session(Value: request), frame: out var frame, failure: out var encodeFailure), userMessage: $"encode refused: {encodeFailure}");
        Assert.True(condition: WorldFrameCodec.TryDecode(failure: out var decodeFailure, frame: frame, payload: out var decoded), userMessage: $"decode refused: {decodeFailure}");

        var payload = Assert.IsType<WorldSubmissionPayload.Session>(@object: decoded);

        return payload.Value;
    }

    [Fact]
    public void GrantAllows_RoundTrips() {
        var subject = GrantSubject.Body(index: 2);
        var decoded = Assert.IsType<WorldQuery.GrantAllows>(@object: RoundTripQuery(query: new WorldQuery.GrantAllows(
            Principal: WorldPrincipal.Seat(slot: 1),
            Capability: WorldCapability.Drive,
            Subject: subject
        )));

        Assert.Equal(actual: decoded.Principal, expected: WorldPrincipal.Seat(slot: 1));
        Assert.Equal(actual: decoded.Capability, expected: WorldCapability.Drive);
        Assert.Equal(actual: decoded.Subject, expected: subject);
    }
    [Fact]
    public void GrantHandleMint_RoundTrips() {
        var decoded = Assert.IsType<WorldQuery.GrantHandleMint>(@object: RoundTripQuery(query: new WorldQuery.GrantHandleMint(
            Principal: WorldPrincipal.Seat(slot: 0),
            Capability: WorldCapability.Observe,
            Index: 3
        )));

        Assert.Equal(actual: decoded.Principal, expected: WorldPrincipal.Seat(slot: 0));
        Assert.Equal(actual: decoded.Capability, expected: WorldCapability.Observe);
        Assert.Equal(actual: decoded.Index, expected: 3);
    }
    [Fact]
    public void GrantHandleResolve_RoundTrips() {
        var handle = new WorldHandle(
            Index: 5,
            Generation: 7,
            TablePrincipal: WorldPrincipal.Seat(slot: 2),
            TableCapability: WorldCapability.Drive
        );
        var decoded = Assert.IsType<WorldQuery.GrantHandleResolve>(@object: RoundTripQuery(query: new WorldQuery.GrantHandleResolve(Handle: handle)));

        Assert.Equal(actual: decoded.Handle, expected: handle);
    }
    [Fact]
    public void PopulationChannels_RoundTrips() {
        _ = Assert.IsType<WorldQuery.PopulationChannels>(@object: RoundTripQuery(query: new WorldQuery.PopulationChannels()));
    }
    [Fact]
    public void ProfileCatalog_RoundTrips() {
        _ = Assert.IsType<WorldQuery.ProfileCatalog>(@object: RoundTripQuery(query: new WorldQuery.ProfileCatalog()));
    }
    [Fact]
    public void FindProfile_RoundTrips() {
        var decoded = Assert.IsType<WorldQuery.FindProfile>(@object: RoundTripQuery(query: new WorldQuery.FindProfile(Name: "p1")));

        Assert.Equal(actual: decoded.Name, expected: "p1");
    }
    [Fact]
    public void PreferredControllerProfile_RoundTrips() {
        var device = new InputDeviceId(Value: Guid.NewGuid(), Persistence: InputDeviceIdentityPersistence.Reconnect);
        var decoded = Assert.IsType<WorldQuery.PreferredControllerProfile>(@object: RoundTripQuery(query: new WorldQuery.PreferredControllerProfile(Device: device)));

        Assert.Equal(actual: decoded.Device, expected: device);
    }
    [Fact]
    public void RememberPreferredController_RoundTrips() {
        var device = new InputDeviceId(Value: Guid.NewGuid(), Persistence: InputDeviceIdentityPersistence.Reconnect);
        var decoded = Assert.IsType<SessionRequest.RememberPreferredController>(@object: RoundTripSession(request: new SessionRequest.RememberPreferredController(
            Principal: WorldPrincipal.Seat(slot: 0),
            Device: device,
            IdentityName: "p2"
        )));

        Assert.Equal(actual: decoded.Principal, expected: WorldPrincipal.Seat(slot: 0));
        Assert.Equal(actual: decoded.Device, expected: device);
        Assert.Equal(actual: decoded.IdentityName, expected: "p2");
    }
}
