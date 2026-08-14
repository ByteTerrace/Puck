namespace Puck.Commands;

/// <summary>How a command invocation entered the command pipeline.</summary>
public enum CommandOrigin : byte {
    /// <summary>No ingress stamped the command. A dispatched command must never carry this value.</summary>
    Unspecified = 0,

    /// <summary>The command came from submitted console text.</summary>
    Text = 1,

    /// <summary>The command came from an authored binding, whether physical or presentation-driven.</summary>
    Binding = 2,
}
