namespace Puck.Maths.Tests;

/// <summary>A bound law with its tier, covered members, evidence legs and executable assertion.</summary>
/// <param name="Id">The stable case id (also the test display name).</param>
/// <param name="Tier">The execution tier.</param>
/// <param name="Members">The public members this case covers.</param>
/// <param name="Legs">What this case's statements stand on. Required: a case declaring no leg does not compile.</param>
/// <param name="Run">The action that runs the case.</param>
internal sealed record LawCase(string Id, Tier Tier, IReadOnlyList<CoverRef> Members, IReadOnlyList<Leg> Legs, Action Run);
