using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.World.Client;

/// <summary>One explicit CPU reference evaluation of a captured indirect pick. A refusal or unresolved estimate
/// has no numeric GPU divergence. This result is retained for inspection, never recomputed by the HUD.</summary>
/// <param name="Estimate">The independent normalized irradiance and path counts, or null when unsupported.</param>
/// <param name="Refusal">The named unsupported source or unresolved condition, or null for a complete estimate.</param>
/// <param name="FieldQueries">The reference's actual point and gradient queries.</param>
/// <param name="Casts">The reference's actual rays and segments.</param>
/// <param name="FeedbackBounces">The visible publication's feedback depth.</param>
/// <param name="SourceSequence">The immutable solve source's sequence.</param>
/// <param name="Difference">Actual GPU incident RGB minus the supported resolved reference, or null.</param>
public sealed record WorldIndirectReferenceResult(IrradianceEstimate? Estimate, string? Refusal, long FieldQueries,
    long Casts, int FeedbackBounces, ulong SourceSequence, Vector3? Difference);
