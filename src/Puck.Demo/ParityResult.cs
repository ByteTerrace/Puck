namespace Puck.Demo;

/// <summary>
/// A mutable carrier the <see cref="ParityValidationNode"/> fills with the parity run's outcome so
/// <c>Program</c> can read the process exit code after the host loop drains. Defaults to the infra-failure code
/// so a run that never completes (e.g. the validation node threw before finishing) fails loudly rather than
/// silently passing.
/// </summary>
internal sealed class ParityResult {
    /// <summary>Gets or sets the process exit code: 0 pass, 1 gate-fail, 2 infra-fail. Starts at 2 so an
    /// incomplete run fails.</summary>
    public int ExitCode { get; set; } = 2;
}
