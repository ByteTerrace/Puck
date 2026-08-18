namespace Puck.Maths.Tests;

/// <summary>One declared law case: a stable id, its tier, the public members it exercises (for the coverage manifest),
/// the legs it stands on (for the leg ledger), and the action that runs it. Facts are generated from this registry, and
/// both the coverage manifest and the leg ledger read these same declarations — so both are derived mechanically from
/// the law instantiations, not by hand.</summary>
/// <param name="Id">The stable case id (also the test display name).</param>
/// <param name="Tier">The execution tier.</param>
/// <param name="Members">The public members this case covers.</param>
/// <param name="Legs">What this case's statements stand on. Required: a case declaring no leg does not compile.</param>
/// <param name="Run">The action that runs the case.</param>
internal sealed record LawCase(string Id, Tier Tier, IReadOnlyList<CoverRef> Members, IReadOnlyList<Leg> Legs, Action Run);
