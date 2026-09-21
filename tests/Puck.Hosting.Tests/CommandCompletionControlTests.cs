using Puck.Commands;

namespace Puck.Hosting.Tests;

public sealed class CommandCompletionControlTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ExecWaitsForTheAuthoritativeVerdict(bool rejected) {
        var pending = new CommandSettlement();
        var source = new TextCommandSource(new CommandRegistry([new Module(pending: pending)]));
        using var session = new ConsoleControlSession(source, _ => throw new NotSupportedException());
        var response = session.ExecuteAsync(new ControlRequest(Command: "edit", Id: 1, Operation: "exec", TimeoutMilliseconds: 5000), TestContext.Current.CancellationToken);

        source.Collect();
        Assert.False(condition: response.IsCompleted);
        pending.Settle((rejected ? CommandResult.Error(output: "authority refused") : new CommandResult("authority applied")));
        var result = await response;

        Assert.Equal((rejected ? "refused" : "completed"), result.Status);
        Assert.Equal(rejected, result.IsError);
        Assert.Equal((rejected ? "authority refused" : "authority applied"), result.Output);
    }

    private sealed class Module(CommandSettlement pending) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(name: "edit", description: "Deferred authority edit.",
                handler: (_, _) => CommandResult.Settling(pending), bindability: CommandBindability.Unbindable);
        }
    }
}
