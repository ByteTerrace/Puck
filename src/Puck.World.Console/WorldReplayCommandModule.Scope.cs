using Puck.Commands;

namespace Puck.World;

public sealed partial class WorldReplayCommandModule {
    private CommandResult Scope(WireArgs args, Principal principal) {
        if (args.Count == 0) { return new CommandResult(Output: string.Join(separator: '\n', values: m_tape.Scopes.Select(selector: scope => scope.Describe()))); }
        var name = args[0].ToString();
        var scope = m_tape.Scopes.FirstOrDefault(predicate: scope => (scope.Name == name));

        if (scope is null) { return CommandResult.Error(output: $"[replay.scope: no scope '{name}']"); }
        if (args.Count == 1) { return new CommandResult(Output: scope.Describe()); }
        if (args.Count != 2) { return CommandResult.Error(output: "[replay.scope: usage — replay.scope [<name> [capture|drive|cancel|reset]]]"); }
        var request = args[1].ToString() switch { "capture" => 4, "drive" => 1, "cancel" => 2, "reset" => 3, _ => 0 };

        if (request == 0) { return CommandResult.Error(output: "[replay.scope: expected capture, drive, cancel or reset]"); }
        m_tape.RequestScope(principal: principal, request: request, scope: scope);
        return new CommandResult(Output: $"[replay.scope: {name} requested {args[1]}]");
    }
}
