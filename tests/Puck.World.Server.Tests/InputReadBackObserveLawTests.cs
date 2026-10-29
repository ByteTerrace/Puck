using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a read-back that names a body's live input (its mode, owners, sources, holds and raw values) crosses
/// <see cref="WorldCapability.Observe"/> over that body for the principal that asks, so a verb open to every caller
/// shows a peer holding no grant a refusal, and the same question from an observer is answered.
/// </summary>
public sealed class InputReadBackObserveLawTests {
    [Fact]
    public void AnInputReadBackIsRefusedToAPrincipalWithoutObserveAndAnsweredToTheConsole() {
        using var fixture = Fixtures.FreshServer();

        var transport = new LoopbackTransport(server: fixture.Server);
        var peer = Principal.Peer(
            generation: 1,
            index: 5
        );

        foreach (var query in new WorldQuery[] {
            new WorldQuery.PlayerInput(Index: 0),
            new WorldQuery.PlayerAffordances(Index: 0),
            new WorldQuery.PlayerChannels(Index: 0),
        }) {
            QueryAnswer? denied = null;
            QueryAnswer? granted = null;

            transport.Query(
                completion: answer => denied = answer,
                principal: peer,
                query: query
            );
            transport.Query(
                completion: answer => granted = answer,
                principal: Principal.Console,
                query: query
            );

            Assert.True(
                condition: denied!.Value.Refused,
                userMessage: $"{query.GetType().Name} answered a peer without Observe: {denied.Value.Text}"
            );
            Assert.StartsWith(
                expectedStartString: "[query refused:",
                actualString: denied.Value.Text
            );
            Assert.DoesNotContain(
                expectedSubstring: "[query refused:",
                actualString: granted!.Value.Text
            );
        }
    }
}
