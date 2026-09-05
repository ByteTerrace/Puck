using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The transfer-count ceiling every <see cref="WorldStateTransform.Transfer"/> is validated against.</summary>
public static class WorldStateTransferCapacity {
    /// <summary>The most tokens one transfer moves in a single mutation — <see cref="WorldTopologyCompilation.MaxCells"/>,
    /// the ceiling an uncapacitied <see cref="WorldStateDomain.KeysOf"/> pile row's own cell count is bounded by.</summary>
    public const int MaxTransferCount = WorldTopologyCompilation.MaxCells;
}

/// <summary>Marks a plain integer row as a guarded submission stamp: the row's own generation
/// <see cref="Sequence"/>, the sole state a <see cref="WorldPhaseGuard"/> checks and the mutation pipeline advances.
/// Nothing about who may act, in what order, or under what deadline is engine knowledge any more — a turn order, a
/// round counter, a ready or skipped bitset, and a deadline are all ordinary rows a world's own rules author and
/// advance, and eligibility is the ordinary grant/admission system over whichever rows a rule ties to this one via
/// <see cref="WorldStateRow.PhaseOf"/>. Submitting any mutation whose <see cref="WorldPhaseGuard"/> matches this
/// generation both admits the submission and, on success, advances the generation by one: the guard's presence on a
/// mutation IS the turn's completion, so a world that wants several ungated moves before a turn ends simply leaves
/// those rows untagged and reserves <see cref="WorldStateRow.PhaseOf"/> for the one row that ends it.</summary>
/// <param name="Sequence">The generation. Advanced by the mutation pipeline after a guarded mutation naming this row
/// succeeds; never written directly.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStatePhase(long Sequence = 0);

/// <summary>Admission guard for a submitted gameplay operation: reduces a turn-taking protocol to the one thing the
/// engine still enforces, a monotonic sequence a submission must match. See <see cref="WorldStatePhase"/> for what
/// a match does on success.</summary>
/// <param name="Row">The phase row.</param>
/// <param name="Sequence">The observed generation.</param>
/// <param name="Participant">World-program-only participant attribution; outside callers always use their stamp.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPhaseGuard(string Row, long Sequence, string? Participant = null);
