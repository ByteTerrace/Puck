namespace Puck.Launcher.Stub;

/// <summary>What the stub does with the version its own pointer files name.</summary>
public enum StubAction {
    /// <summary>Fewer than the configured maximum consecutive unhealthy attempts have been recorded for the
    /// current version — launch it.</summary>
    LaunchCurrent,
    /// <summary>The current version exceeded its attempt ceiling and a last-good version exists whose durable-state
    /// generation the current version never advanced past — revert <c>current</c> to it and launch that instead.</summary>
    RevertToLastGood,
    /// <summary>The current version exceeded its attempt ceiling, but reverting would let an older binary read
    /// durable state a newer one already wrote in an incompatible shape (or there is no last-good version at all) —
    /// launch the failed candidate anyway rather than risk that read.</summary>
    LaunchCurrentAnyway,
}
/// <summary>
/// THE stub's entire selection policy, as one pure function over already-read file contents — no process, no I/O,
/// so <c>tests/Puck.Launcher.Tests</c> can exercise every branch (including the exact attempt-count boundary)
/// directly. <see cref="Puck.Launcher.Stub"/>'s <c>Program.cs</c> is the only caller: it reads
/// <c>current</c>/<c>last-good</c>/<c>state-generation</c>/<c>state/health.json</c>, calls <see cref="Decide"/>,
/// and acts on the result.
/// </summary>
public static class StubDecisionTable {
    /// <summary>Decides what to do with the current version.</summary>
    /// <param name="attempts">Consecutive unhealthy-or-unobserved launch attempts already recorded for the current
    /// version (flushed BEFORE each of those launches, so a candidate that hangs or dies before writing anything
    /// still counts).</param>
    /// <param name="maxAttempts">The configured ceiling; <paramref name="attempts"/> reaching it trips the gate.</param>
    /// <param name="hasLastGood">Whether a last-good version is recorded at all (false on a first install, or any
    /// install that has never yet applied a second version).</param>
    /// <param name="currentGeneration">The current version's <c>state-generation</c>.</param>
    /// <param name="lastGoodGeneration">The last-good version's <c>state-generation</c> (meaningless when
    /// <paramref name="hasLastGood"/> is <see langword="false"/>).</param>
    /// <returns>The action the stub takes.</returns>
    public static StubAction Decide(int attempts, int maxAttempts, bool hasLastGood, int currentGeneration, int lastGoodGeneration) {
        if (attempts < maxAttempts) {
            return StubAction.LaunchCurrent;
        }

        return ((hasLastGood && (currentGeneration <= lastGoodGeneration))
            ? StubAction.RevertToLastGood
            : StubAction.LaunchCurrentAnyway
        );
    }
}
