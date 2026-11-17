namespace Puck.Commands;

/// <summary>Resolves operator authority from the live world addressed by an invocation.</summary>
public interface ICommandAuthority {
    /// <summary>Whether this invocation's principal currently operates its addressed world.</summary>
    bool IsOperator(CommandContext context);
}
