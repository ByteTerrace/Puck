using Puck.World;

namespace Puck.Cli.Test;

/// <summary>Which host a <c>puck test</c> leg boots a test world through.</summary>
internal enum TestHost {
    /// <summary>The real <c>Puck.World</c> executable, headless.</summary>
    Server,
    /// <summary>The browser-wasm engine host.</summary>
    Browser,
}
/// <summary>One verdict row as the export declares and resolves it.</summary>
/// <param name="Name">The verdict row's name.</param>
/// <param name="Gate">The expectation the row's <c>verdict.gate</c> claims.</param>
/// <param name="Status">The status cell's value at the export tick.</param>
/// <param name="FiredTick">The tick of the rule firing that last wrote the row, or <see langword="null"/> when the
/// export carries no firing stamp — a status that moved through some door other than a rule's own effect.</param>
/// <param name="Saw">Every other cell of the row, in declaration order, as <c>key=value</c>. The firing stamp is not
/// one of them: it is the engine's, not a value the gate saw.</param>
internal sealed record TestVerdict(string Name, string Gate, long Status, ulong? FiredTick, IReadOnlyList<string> Saw) {
    /// <summary>Gets a value indicating whether a rule firing ever wrote this row.</summary>
    public bool Fired => (FiredTick is { } tick) && (tick > 0UL);
    /// <summary>Gets the status the runner judges: the cell's own value only when a rule firing wrote the row, and
    /// <see cref="WorldVerdict.NotEvaluated"/> otherwise.</summary>
    public long Judged => (Fired
        ? Status
        : WorldVerdict.NotEvaluated
    );
}
/// <summary>One local edit verdict the run's schedule manifest recorded.</summary>
/// <param name="Rejected">Whether the edit was refused.</param>
/// <param name="Message">The server's own reason or confirmation.</param>
internal sealed record TestEcho(bool Rejected, string Message);
/// <summary>One scheduled command as the run's manifest recorded its submission.</summary>
/// <param name="Tick">The tick the command was submitted at.</param>
/// <param name="Principal">The acting identity's label.</param>
/// <param name="Command">The command line.</param>
/// <param name="Outcome">What the ingress answered.</param>
/// <param name="Detail">The refusal reason or the handler's own output, when there was one.</param>
internal sealed record TestSubmission(ulong Tick, string Principal, string Command, string Outcome, string? Detail);
/// <summary>One leg's whole reading of a world: the export bytes the run wrote and what they say.</summary>
/// <param name="ExportBytes">The canonical state-export bytes, compared across runs during reproduction
/// qualification.</param>
/// <param name="ManifestBytes">The submission-manifest bytes, compared across runs during reproduction
/// qualification.</param>
/// <param name="ExportTick">The tick the export was taken at, as the manifest recorded it.</param>
/// <param name="Truncated">Whether the run ended before the tick the document declared.</param>
/// <param name="Verdicts">Every verdict row the export declares.</param>
/// <param name="Submissions">One entry per declared scheduled row, in declaration order.</param>
/// <param name="Echoes">Every local edit verdict the run recorded.</param>
internal sealed record TestReading(
    byte[] ExportBytes,
    byte[] ManifestBytes,
    ulong ExportTick,
    bool Truncated,
    IReadOnlyList<TestVerdict> Verdicts,
    IReadOnlyList<TestSubmission> Submissions,
    IReadOnlyList<TestEcho> Echoes
);
/// <summary>One scheduled row as the world document declares it.</summary>
/// <param name="Tick">The tick the row is submitted at.</param>
/// <param name="Principal">The acting seat's label.</param>
/// <param name="Command">The command line.</param>
/// <param name="Expect">The outcome the row declares its ingress will answer.</param>
/// <param name="Refusal">Text the recorded refusal detail must contain, or <see langword="null"/> to accept
/// any.</param>
internal sealed record TestScheduleRow(ulong Tick, string Principal, string Command, WorldScheduleExpectation Expect, string? Refusal) {
    /// <summary>Gets the manifest outcome this row declares.</summary>
    public string Outcome => (Expect switch {
        WorldScheduleExpectation.Refused => WorldScheduleSection.OutcomeRefused,
        _ => WorldScheduleSection.OutcomeSubmitted,
    });
}
/// <summary>A test world's <c>schedule</c> section as the runner reads it out of the document's own text.</summary>
/// <param name="ExportTick">The tick the document declares its export at — the last row's tick plus the settle
/// margin.</param>
/// <param name="RateHz">The document's simulation rate, used to bound a failed or stalled leg.</param>
/// <param name="Rows">Every declared row, in declaration order, reconciled against the manifest the run
/// wrote.</param>
internal sealed record TestSchedule(ulong ExportTick, int RateHz, IReadOnlyList<TestScheduleRow> Rows);
