namespace Puck.Maths.Tests;

/// <summary>The three execution tiers. Plain <c>dotnet test</c> runs <see cref="Default"/>; <see cref="Deep"/> and
/// <see cref="Exhaustive"/> rows are explicit, and a run opts into one with
/// <c>--explicit on --filter-trait tier=&lt;tier&gt;</c> (see the project README). No tier measures time: cost is <c>puck bench</c>'s.</summary>
internal enum Tier {
    /// <summary>Everything but <see cref="Deep"/> and <see cref="Exhaustive"/>; the plain-run set, which reaches the suite's line coverage of <c>Puck.Maths</c>.</summary>
    Default,
    /// <summary>Domains Default does not reach: the exhaustive four-operand edge cross product, finite carriers swept whole, and longer modulus and degree ladders. Opt in with <c>--explicit on --filter-trait tier=Deep</c>.</summary>
    Deep,
    /// <summary>
    /// Full-carrier sweeps and expensive finite combinatorial-prime checks. Opt in with
    /// <c>--explicit on --filter-trait tier=Exhaustive</c>; expect tens of minutes and run it on demand or nightly, never in a change loop.
    /// </summary>
    /// <remarks>
    /// A finite prime check names its operands and evidence envelope; its tier does not claim full-carrier proof.
    /// This is the only tier whose statements do NOT come from a <see cref="Domain"/>. A domain hands out an edge
    /// battery, a bounded random batch and a stratified block — a SAMPLE, by construction — which cannot express "every
    /// 32-bit value". Exhaustive cases therefore supply their own basis through <c>Laws.Claim</c>, and that is a
    /// requirement rather than a convention: a sampled domain cannot establish a full-carrier claim.
    /// </remarks>
    Exhaustive,
}
