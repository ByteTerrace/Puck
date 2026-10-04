namespace Puck.SignedDistance.Illumination;

/// <summary>A source-attributed estimate using the ordinary reference's same independent Halton paths.
/// Any unresolved path prevents this from being a reference answer.</summary>
/// <param name="Contributions">Independent first-hit categories and later feedback, before receiver response.</param>
/// <param name="Paths">The number of averaged paths.</param>
/// <param name="Unresolved">The number of unresolved paths.</param>
public readonly record struct IrradianceSourceEstimate(IrradianceContributions Contributions, int Paths, int Unresolved);
