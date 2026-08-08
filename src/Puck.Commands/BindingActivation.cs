namespace Puck.Commands;

/// <summary>
/// An opaque, compiled command activation carried by an interactive binding presentation. Presentation code may
/// display its command name and hand it back to <see cref="InputRouter.Activate"/>; only the binding compiler can
/// construct one, so a radial presenter cannot invent a command, value, or phase outside the authored profile.
/// </summary>
public sealed class BindingActivation {
    /// <summary>The logical source stamped on a radial activation so handlers distinguish it from console text.</summary>
    public const string RadialSource = "binding.radial";

    internal BindingActivation(string command, CommandValue value, CommandPhase phase) {
        Command = command;
        Phase = phase;
        Value = value;
    }

    /// <summary>The authored command name, for display and diagnostics.</summary>
    public string Command { get; }

    internal CommandPhase Phase { get; }
    internal CommandValue Value { get; }
}
