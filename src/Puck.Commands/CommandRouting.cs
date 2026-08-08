namespace Puck.Commands;

/// <summary>
/// Specifies how a command's invocation is routed when it is submitted as text (the console / STDIN path).
/// </summary>
/// <remarks>
/// This is the command's <em>determinism class</em>, an axis distinct from the modality <see cref="CommandMaps">command
/// map</see>: it answers whether the command's effect mutates the deterministic simulation. A
/// <see cref="Simulation"/> command must be tick-aligned, so a submitted line is folded into the per-tick
/// <see cref="CommandSnapshot"/> rather than run inline — a world tape records the server's input stream and
/// re-runs it on replay, so the command's effect reproduces without the snapshot itself being recorded; an
/// <see cref="Immediate"/> command (help, quit, console editing, graphics toggles) has no simulation effect and
/// runs the instant it is submitted.
/// </remarks>
public enum CommandRouting {
    /// <summary>The command runs inline when submitted; it does not affect the deterministic simulation.</summary>
    Immediate = 0,
    /// <summary>The command is injected into the per-tick <see cref="CommandSnapshot"/>, so it is tick-aligned and applied like any other deterministic input.</summary>
    Simulation = 1,
}
