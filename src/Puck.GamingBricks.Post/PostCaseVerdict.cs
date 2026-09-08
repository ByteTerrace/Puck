namespace Puck.GamingBricks.Post;

/// <summary>The outcome class of one case inside a stage — a ledger row, a vector family, a boot — as it compares to
/// what the stage expected of it. A stage folds its cases into its own <see cref="PostVerdict"/>; the case rows
/// themselves reach the JUnit report so a failure is named individually.</summary>
public enum PostCaseVerdict {
    /// <summary>The case did what a passing case does, and that is what was expected of it.</summary>
    Pass,
    /// <summary>The case failed or stayed inconclusive, and that is exactly what was recorded for it: a known defect,
    /// still known. Neutral to the stage verdict; reported as a pass with a note.</summary>
    ExpectedFail,
    /// <summary>The case was not run by construction (an unrunnable case, an absent asset).</summary>
    Skip,
    /// <summary>The case's outcome differs from what was expected of it, in either direction: a recorded pass that
    /// failed, a recorded fail that now passes, a changed pixel count. Folds to a stage failure.</summary>
    Mismatch,
    /// <summary>The case could not be measured: it threw, exceeded its wall-clock budget, or its inputs no longer
    /// match their recorded hashes. Folds to a stage failure and blocks accepting the run's candidate ledger.</summary>
    Error,
}
