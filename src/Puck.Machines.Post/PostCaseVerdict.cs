namespace Puck.Machines.Post;

/// <summary>The outcome class of one case inside a stage — a corpus ROM, a vector family, a boot — as it compares to
/// what the stage requires of it. A stage folds its cases into its own <see cref="PostVerdict"/>; the case rows
/// themselves reach the JUnit report so a failure is named individually.</summary>
public enum PostCaseVerdict {
    /// <summary>The case did what a passing case does.</summary>
    Pass,
    /// <summary>The case was not run by construction (an unrunnable case, an absent asset).</summary>
    Skip,
    /// <summary>The case ran and did not pass: a failing ROM, a diverging vector, a boot that differs. Folds to a stage failure.</summary>
    Mismatch,
    /// <summary>The case could not be measured: it threw. Folds to a stage failure.</summary>
    Error,
}
