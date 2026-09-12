using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Named machine execution read-back. Declarations use the ordinary world.row verbs.</summary>
/// <param name="authority">The console session's authority resolver.</param>
public sealed class WorldMachineCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.state",
            description: "Reports named machine execution: machine.state [instance]. Includes generation, running state, hardware status, completed segments, backlog, and fault. Author declarations through world.row.set machines <json>.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error("[machine.state: expected an optional instance name]");
                }
                if (!authority.TryResolveServer(context, "machine.state", out var server, out var error)) {
                    return error;
                }
                var names = args.Count == 1 ? [args[0].ToString()] : server.Machines.InstanceNames;
                var echo = CommandEcho.Open("machine.state");
                var found = false;
                foreach (var name in names) {
                    if (server.Machines.InstanceState(name) is not { } state) {
                        return CommandResult.Error($"[machine.state: no machine '{name}']");
                    }
                    found = true;
                    echo = echo.Head(name).Field("generation", state.Generation).Field("engine", state.Engine)
                        .Field("running", state.Running).Field("status", state.Status.ToString())
                        .Field("segments", state.FramesStepped).Field("pending", state.PendingSteps)
                        .Field("fault", state.Fault ?? "none");
                    var declaration = server.Definition.Machines.First(row => row.Name == name);
                    foreach (var binding in declaration.Memory ?? []) {
                        var observed = server.MachineBindingState(name, binding.Name);
                        echo = echo.Segment().Head(name).Field("binding", binding.Name)
                            .Field("access", observed?.Status.ToString() ?? "Unavailable")
                            .Field("value", observed?.LastValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved")
                            .Field("reason", observed?.Reason ?? "none");
                    }
                }
                return new CommandResult(Output: (found ? echo : echo.Head("none")).Close());
            }
        );
    }
}
