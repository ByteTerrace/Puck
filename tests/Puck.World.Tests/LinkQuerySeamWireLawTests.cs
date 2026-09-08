
using Xunit;

using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using static Puck.World.Tests.AdmissionWireFixture;

namespace Puck.World.Tests;

/// <summary>
/// Proves the seven <see cref="WorldQuery"/> leaves the link-query seam added (<c>Client.PlayerRoster</c>'s replacement
/// for a live <c>Server.WorldServer</c> reference) reach an admitted peer over the REAL wire door
/// (<see cref="WorldPeerHost"/>, a genuine <see cref="PeerTestClient"/>): each query's Completion-lane answer arrives over
/// the socket, not merely in-process. Follows the same real-wire-over-server-internals discipline as
/// <see cref="AdmissionSecurityLawTests"/>.
/// </summary>
/// <remarks>
/// Six of the seven leaves (every one except <see cref="WorldQuery.GrantAllows"/>) resolve
/// <see cref="WorldQuery.ObservationSubject"/> to <see cref="GrantSubject.All"/>, and
/// <c>Server.WorldGrants.IsLegitimateSubject</c> admits Observe/all only for <c>PrincipalKind.Console</c> or
/// <c>PrincipalKind.Seat</c> — a remote peer is neither, so no admission grant can ever satisfy that gate for those
/// six. That is deliberate security architecture (an untrusted socket peer must never hold a whole-world observe
/// wildcard), not a gap this seam owes a fix for; the six laws below prove the QUERY still round-trips the wire
/// (decodes, reaches the server, and comes back as a named Completion-lane refusal) rather than hanging, crashing, or
/// getting silently dropped. <see cref="WorldQuery.GrantAllows"/> alone carries its own subject, so a peer holding
/// Observe over a concrete body can read it — proven separately below with a non-refused answer.
/// </remarks>
public sealed class LinkQuerySeamWireLawTests {
    [Fact]
    public async Task GrantAllows_AnswersOverTheWire_WhenThePeerObservesTheConcreteSubject() {
        var answer = await RunQueryAsync(query: peer => new WorldQuery.GrantAllows(
            Principal: WorldPrincipal.Seat(slot: 0),
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: peer.Index)
        ));

        Assert.False(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "grant.allows:", actualString: answer.Text);
    }
    [Fact]
    public async Task GrantHandleMint_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.GrantHandleMint(
            Principal: WorldPrincipal.Seat(slot: 0),
            Capability: WorldCapability.Observe,
            Index: 0
        ));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task GrantHandleResolve_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var handle = new WorldHandle(
            Index: 0,
            Generation: 0,
            TablePrincipal: WorldPrincipal.Seat(slot: 0),
            TableCapability: WorldCapability.Observe
        );
        var answer = await RunQueryAsync(query: _ => new WorldQuery.GrantHandleResolve(Handle: handle));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task PopulationChannels_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.PopulationChannels());

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task ProfileCatalog_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.ProfileCatalog());

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task FindProfile_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var answer = await RunQueryAsync(query: _ => new WorldQuery.FindProfile(Name: "no-such-profile"));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }
    [Fact]
    public async Task PreferredControllerProfile_RefusesOverTheWire_ObserveAllIsConsoleAndSeatOnly() {
        var device = new InputDeviceId(Value: Guid.NewGuid(), Persistence: InputDeviceIdentityPersistence.Reconnect);
        var answer = await RunQueryAsync(query: _ => new WorldQuery.PreferredControllerProfile(Device: device));

        Assert.True(condition: answer.Refused, userMessage: answer.Text);
        Assert.Contains(expectedSubstring: "cannot observe all", actualString: answer.Text);
    }

    // Admits one peer holding Observe over ONLY its own body (never all), builds the caller's query against the
    // admitted peer's own principal, and submits it over a genuine TCP connection. The grant is deliberately narrow
    // so GrantAllows' concrete-subject success and the other six leaves' Observe/all refusal are proven under the
    // SAME admitted session, not two different setups.
    private static async Task<QueryAnswer> RunQueryAsync(Func<WorldPrincipal, WorldQuery> query) {
        var identity = GenerateIdentity(subject: "link-query-seam-peer");

        try {
            var grants = new[] { new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.Body(index: PeerBodyIndex), Budget: 100) };
            var document = BuildAdmissionDocument(entry: BuildEntry(grants: grants, identity: identity));

            using var fixture = Fixtures.FreshServer(definition: document);
            using var host = new WorldPeerHost(server: fixture.Server);

            host.Start(listen: "127.0.0.1:0");

            using var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(fixture: fixture, host: host, ct: pumpCts.Token);

            try {
                using var requestCts = Laws.SocketDeadline();
                var admitted = await ConnectAndAdmitAsync(host: host, identity: identity, ct: requestCts.Token);

                using (admitted.Client) {
                    var peer = WorldPrincipal.Peer(index: admitted.PeerIndex, generation: admitted.Generation);

                    return await SubmitQueryAsync(stream: admitted.Client.GetStream(), query: query(arg: peer), ct: requestCts.Token);
                }
            } finally {
                pumpCts.Cancel();
                await pumpTask;
            }
        } finally {
            identity.Key.Dispose();
        }
    }
}
