using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>The transfer-count ceiling every <see cref="StateTransform.Transfer"/> is validated against.</summary>
public static class StateTransferCapacity {
    /// <summary>The most tokens one transfer moves in a single mutation — <see cref="TopologyCompilation.MaxCells"/>,
    /// the ceiling an uncapacitied <see cref="StateDomain.KeysOf"/> pile row's own cell count is bounded by.</summary>
    public const int MaxTransferCount = TopologyCompilation.MaxCells;
}

/// <summary>Marks a plain integer row as a guarded submission stamp: the row's own generation
/// <see cref="Sequence"/>, the sole state a <see cref="PhaseGuard"/> checks and the mutation pipeline advances.
/// Nothing about who may act, in what order, or under what deadline is engine knowledge any more — a turn order, a
/// round counter, a ready or skipped bitset, and a deadline are all ordinary rows a world's own rules author and
/// advance, and eligibility is the ordinary grant/admission system over whichever rows a rule ties to this one via
/// <see cref="StateRow.PhaseOf"/>. Submitting any mutation whose <see cref="PhaseGuard"/> matches this
/// generation both admits the submission and, on success, advances the generation by one: the guard's presence on a
/// mutation IS the turn's completion, so a world that wants several ungated moves before a turn ends simply leaves
/// those rows untagged and reserves <see cref="StateRow.PhaseOf"/> for the one row that ends it.</summary>
/// <param name="Sequence">The generation. Advanced by the mutation pipeline after a guarded mutation naming this row
/// succeeds; never written directly.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePhase(long Sequence = 0);

/// <summary>Admission guard for a submitted gameplay operation: reduces a turn-taking protocol to the one thing the
/// engine still enforces, a monotonic sequence a submission must match. See <see cref="StatePhase"/> for what
/// a match does on success.</summary>
/// <param name="Row">The phase row.</param>
/// <param name="Sequence">The observed generation.</param>
/// <param name="Participant">World-program-only participant attribution; outside callers always use their stamp.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PhaseGuard(string Row, long Sequence, string? Participant = null);
