using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class OperatorGrantLawTests {
    [Fact]
    public void OnlyAnOperatorDispatchMintsAContextAndRetainedAuthorityChecksRevocation() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;
        var module = new OperatorAcquisitionModule();
        var registry = new CommandRegistry([module], authority: new LiveAuthority(server));
        var source = new TextCommandSource(registry);
        var result = CommandResult.None;
        using var session = source.CreateSession(Seat, onResult: (_, answer) => result = answer);
        void Invoke() { session.Enqueue("operator.acquire"); source.Collect(); }
        Invoke();
        Assert.True(result.IsError);
        Assert.Null(module.Invocation);
        Assert.Empty(typeof(OperatorContext).GetConstructors());
        Assert.Empty(typeof(WorldOperatorAuthority).GetConstructors());
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), Principal.Console, out var reason), reason);
        Invoke();
        Assert.False(result.IsError, result.Output);
        var invocation = Assert.IsType<OperatorContext>(module.Invocation);
        var authority = server.AcquireOperatorAuthority(invocation);
        Assert.Same(server.Definition, authority.Definition);
        Assert.True(server.GrantTable.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.Throws<InvalidOperationException>(() => authority.Definition);
        Assert.Throws<InvalidOperationException>(() => authority.Arena);
        Assert.Throws<InvalidOperationException>(() => authority.ExportDefinition());
        Assert.Throws<InvalidOperationException>(() => server.AcquireOperatorAuthority(invocation));
        module.Invocation = null;
        Invoke();
        Assert.True(result.IsError);
        Assert.Null(module.Invocation);
    }

    private sealed class OperatorAcquisitionModule : ICommandModule {
        public OperatorContext? Invocation { get; set; }
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.OperatorWithWireArgs(name: "operator.acquire",
                description: "Exercises operator capability acquisition.", bindability: CommandBindability.Unbindable,
                handler: (context, _) => { Invocation = context; return CommandResult.None; });
        }
    }
}
