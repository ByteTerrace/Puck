namespace Puck.Commands;

/// <summary>An invocation admitted to an operator handler by the registry. A normal command context cannot be
/// promoted to this capability by a command module.</summary>
public sealed class OperatorContext {
    internal OperatorContext(CommandContext invocation) => Invocation = invocation;

    /// <summary>The ingress-stamped invocation. Converting back to an invocation does not retain operator authority.</summary>
    public CommandContext Invocation { get; }
}
