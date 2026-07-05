namespace Puck.Demo;

/// <summary>
/// A mutable carrier a self-check gate node (e.g. <see cref="Overworld.OverworldDeterminismNode"/>) fills with its
/// outcome so <c>Program</c> can read the process exit code after the host loop drains. Defaults to the
/// infra-failure code so a run that never completes (e.g. the gate node threw before finishing) fails loudly rather
/// than silently passing.
/// </summary>
internal sealed class ParityResult {
    /// <summary>Gets or sets the process exit code: 0 pass, 1 gate-fail, 2 infra-fail. Starts at 2 so an
    /// incomplete run fails.</summary>
    public int ExitCode { get; set; } = 2;
}
