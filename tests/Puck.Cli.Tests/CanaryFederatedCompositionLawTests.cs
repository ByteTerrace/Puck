using System.Text.Json.Nodes;
using Puck.Cli.Canary;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a federated canary leg whose authority is a composition source stages every world the
/// source declares together, and the authority boots the world the leg enters, patched as a document authority is: it
/// listens on the runner's endpoint and admits the client's key, and its neighbour stands beside it under its own
/// document name. The client side still boots the pristine source.</summary>
public sealed class CanaryFederatedCompositionLawTests {
    private const string Endpoint = "127.0.0.1:1";
    private const string Source = "tests/Puck.World.Canaries/portal-walk/portal-walk.puck";

    private static (string ClientWorld, string AuthorityWorld, CanaryCommand.FederationIdentity Client) Prepare(string runDirectory, string? entry) {
        var world = Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: Source
        );
        var client = CanaryCommand.GenerateFederationIdentity();
        var (clientWorld, authorityWorld) = CanaryCommand.PrepareFederatedWorlds(
            authorityIdentity: CanaryCommand.GenerateFederationIdentity(),
            clientIdentity: client,
            endpoint: Endpoint,
            leg: new CanaryLeg(
                Assertions: [],
                Authorities: [],
                AuthorityWorldPath: world,
                Commands: [],
                Connect: true,
                Entry: entry,
                Name: "positive",
                ScriptPath: "positive.script.txt",
                WorldPath: world
            ),
            runDirectory: runDirectory
        );

        return (clientWorld, authorityWorld, client);
    }

    [InlineData(null, "lobby", "garden")]
    [InlineData("garden", "garden", "lobby")]
    [Theory]
    public void AnAuthorityCompositionStagesEveryWorldAndBootsThePatchedEntry(string? entry, string booted, string neighbour) {
        using var run = new TemporaryDirectory(prefix: "puck-canary-federated-composition-");
        var (clientWorld, authorityWorld, client) = Prepare(
            entry: entry,
            runDirectory: run.RootPath
        );

        Assert.Equal(
            actual: clientWorld,
            expected: Path.Combine(
                path1: RepositoryPaths.RequireRoot(),
                path2: Source
            )
        );
        Assert.Equal(
            actual: Path.GetFileName(path: authorityWorld),
            expected: $"{booted}.world.json"
        );
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: Path.GetDirectoryName(path: authorityWorld)!,
            path2: $"{neighbour}.world.json"
        )));

        var patched = JsonNode.Parse(json: File.ReadAllText(path: authorityWorld))!.AsObject();

        Assert.Equal(
            actual: patched["host"]!["listen"]!.GetValue<string>(),
            expected: Endpoint
        );
        Assert.Equal(
            actual: patched["host"]!["authority"]!.GetValue<string>(),
            expected: Endpoint
        );
        Assert.Contains(
            collection: patched["admission"]!.AsArray(),
            filter: row => (row!["domain"]!.GetValue<string>() == client.Domain)
        );
    }
    [Fact]
    public void AnEntryTheCompositionDoesNotDeclareRefusesTheLeg() {
        using var run = new TemporaryDirectory(prefix: "puck-canary-federated-composition-");
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => Prepare(
            entry: "cellar",
            runDirectory: run.RootPath
        ));

        Assert.Contains(
            expectedSubstring: "'cellar' names no world",
            actualString: refusal.Message
        );
    }
}
