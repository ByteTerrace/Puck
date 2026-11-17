using Xunit;

namespace Puck.Commands.Tests;

public sealed class HostAudienceLawTests {
    [Fact]
    public void OnlyTheConsoleReachesAHostHandler() {
        var module = new HostModule();
        var registry = new CommandRegistry([module]);
        var source = new TextCommandSource(registry);
        foreach (var principal in new[] {
            Principal.Seat(0), Principal.Peer(4, 1), Principal.Session(0, 1),
            Principal.Addon("guest"), Principal.World,
        }) {
            var answer = CommandResult.None;
            var session = source.CreateSession(principal, onResult: (_, result) => answer = result);
            session.Enqueue("host.probe");
            source.Collect();
            Assert.True(answer.IsError, answer.Output);
            Assert.Contains("a host verb", answer.Output);
            Assert.Equal(0, module.Calls);
        }
        Assert.False(registry.Submit("host.probe").IsError);
        Assert.Equal(1, module.Calls);
    }

    [Fact]
    public void AuthoredCommandsCannotWrapHostVerbs() {
        var registry = new CommandRegistry([new HostModule()]);
        var metadata = Assert.Single(registry.Definitions, row => row.Name == "host.probe");
        Assert.Contains("host verb", AuthoredCommandSet.RefusesCall(metadata));
        Assert.Equal(CommandAudience.Host, Assert.Single(registry.Definitions, row => row.Name == "wire.ack").Audience);
        Assert.Equal(CommandAudience.Host, Assert.Single(registry.Definitions, row => row.Name == "wire.errors").Audience);
    }

    private sealed class HostModule : ICommandModule {
        public int Calls { get; private set; }
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                audience: CommandAudience.Host,
                bindability: CommandBindability.Bindable,
                name: "host.probe",
                description: "Counts host invocations.",
                valueKind: CommandValueKind.Digital,
                handler: _ => { Calls++; return CommandResult.None; });
        }
    }
}
