namespace Puck.HumbleGamingBrick.Post;

/// <summary>The outcome class of a single POST stage. The battery folds these into a process exit code: any
/// <see cref="Infra"/> dominates (exit 2), else any <see cref="Fail"/> (exit 1), else 0; <see cref="Skip"/> is
/// neutral.</summary>
internal enum PostVerdict {
    /// <summary>The stage ran and its checks passed.</summary>
    Pass,
    /// <summary>The stage was skipped (e.g. its test corpus is absent); neutral to the aggregate verdict.</summary>
    Skip,
    /// <summary>A check failed — a correctness divergence. Folds to process exit code 1.</summary>
    Fail,
    /// <summary>The stage could not run to completion (an exception or a missing prerequisite). Folds to exit code 2.</summary>
    Infra,
}
