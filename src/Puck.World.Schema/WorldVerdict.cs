namespace Puck.World;

/// <summary>
/// The <c>verdict</c> trait on a <c>state</c> row: the row is a test expectation's answer. The row's own name is
/// the verdict's name, <paramref name="Gate"/> is the expectation in the author's words, the cell
/// <paramref name="Status"/> names carries <see cref="WorldVerdict.NotEvaluated"/>/<see cref="WorldVerdict.Pass"/>/
/// <see cref="WorldVerdict.Fail"/>, and every other cell of the row is a value the gate saw, written by the same
/// rule effect that decided the status.
/// </summary>
/// <remarks>Only a rule's own effect writes a verdict row. The effect door stamps
/// <see cref="WorldVerdict.FiredTickKey"/> beside the cells it writes, and every other door — a cell mutation, a
/// submitted state operation, a scheduled command — refuses the write by name
/// (<see cref="WorldVerdict.RefuseWrite"/>). The row is an ordinary keyed Int row in every other respect: the arena
/// stores, hashes, checkpoints, and exports it, stamp included, like any row.</remarks>
/// <param name="Gate">The expectation, in the author's words — what a failing verdict names. Non-empty, at most
/// <see cref="WorldVerdict.MaxGateLength"/> characters. A <c>.puck</c> <c>expect</c> clause lowers its own source
/// text here.</param>
/// <param name="Status">The cell key carrying the status code. Must be a declared cell of the same row.</param>
public sealed record WorldVerdictTrait(string Gate, CellName Status);
/// <summary>The status codes a <see cref="WorldVerdictTrait.Status"/> cell carries, and the trait's ceilings.</summary>
public static class WorldVerdict {
    /// <summary>The status code a rule writes when its gate did not hold. A verdict fails on this value.</summary>
    public const long Fail = 2L;
    /// <summary>The reserved cell key carrying the simulation tick of the rule firing that last wrote the row — the
    /// engine's own stamp, minted by the effect door and never authored. A status carrying a code with no stamp
    /// beside it moved through some door other than a rule's firing, which is what makes "no rule ever evaluated
    /// this" checkable.</summary>
    public static readonly CellName FiredTickKey = CellName.Parse(candidate: "$firedTick");
    /// <summary>The largest admitted <see cref="WorldVerdictTrait.Gate"/> length, in characters.</summary>
    public const int MaxGateLength = 240;
    /// <summary>The largest admitted verdict-row count in one document.</summary>
    public const int MaxRows = 64;
    /// <summary>The status code an unevaluated verdict carries — the value a verdict row's status cell is authored
    /// at. A verdict still reading it at the export tick fails by name rather than passing silently, so nothing
    /// about it may look like a pass.</summary>
    public const long NotEvaluated = 0L;
    /// <summary>The status code a rule writes when its gate held.</summary>
    public const long Pass = 1L;

    /// <summary>Returns the status code's label — the word <c>puck test</c> prints.</summary>
    /// <param name="status">The status cell's value.</param>
    /// <returns><c>pass</c>, <c>fail</c>, <c>never evaluated</c>, or <c>status &lt;n&gt;</c> for a value outside
    /// the three codes.</returns>
    public static string Describe(long status) => status switch {
        Fail => "fail",
        NotEvaluated => "never evaluated",
        Pass => "pass",
        _ => $"status {status}",
    };
    /// <summary>Determines whether a status cell's value is a passing verdict.</summary>
    /// <param name="status">The status cell's value.</param>
    /// <returns><see langword="true"/> only for <see cref="Pass"/>; every other value, including a code outside
    /// the three, fails.</returns>
    public static bool IsPass(long status) => (status == Pass);
    /// <summary>Returns the refusal every door but a rule's own effect gives a write to a verdict row.</summary>
    /// <param name="row">The verdict row's name.</param>
    /// <returns>The refusal reason, in the author's own vocabulary.</returns>
    /// <remarks>One text, so the boot loader, the cell-mutation door, the submitted state operation and a scheduled
    /// command all refuse the same write the same way.</remarks>
    public static string RefuseWrite(CellName row) => $"state row '{row}' carries the verdict trait — only a rule's own effect writes a verdict, so the firing that wrote it stays named by '{FiredTickKey}'; no mutation, submitted operation or scheduled command may write it";
}
