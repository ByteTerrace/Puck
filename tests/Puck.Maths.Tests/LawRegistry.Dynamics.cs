namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain Dynamics = new(
        Key: "dynamics",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] DynamicsCases() => [
        Case(
            id: "dynamics.create-constants-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.DynamicsCreateConstantsVsOracle,
                domain: Dynamics,
                lawId: "dynamics.create-constants-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "dynamics.step-vs-evaluate-close-agreement",
            run: () => Laws.SweptClaim(
                claim: Subjects.DynamicsStepVsEvaluate,
                domain: Dynamics,
                lawId: "dynamics.step-vs-evaluate-close-agreement",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "dynamics.critical-and-overdamped-never-overshoot",
            run: () => Laws.Claim(
                claim: Subjects.DynamicsCriticalAndOverdampedNeverOvershoot,
                lawId: "dynamics.critical-and-overdamped-never-overshoot"
            )
        ),
        Case(
            id: "dynamics.steady-state-exact",
            run: () => Laws.Claim(
                claim: Subjects.DynamicsSteadyStateExact,
                lawId: "dynamics.steady-state-exact"
            )
        ),
        Case(
            id: "dynamics.initial-response-sign",
            run: () => Laws.Claim(
                claim: Subjects.DynamicsInitialResponseSign,
                lawId: "dynamics.initial-response-sign"
            )
        ),
        Case(
            id: "dynamics.refusals-and-overflow",
            run: () => Laws.Claim(
                claim: Subjects.DynamicsRefusalsAndOverflow,
                lawId: "dynamics.refusals-and-overflow"
            )
        ),
        Case(
            id: "dynamics.vector-lanes-independent",
            run: () => Laws.SweptClaim(
                claim: Subjects.DynamicsVectorLanesIndependent,
                domain: Dynamics,
                lawId: "dynamics.vector-lanes-independent",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "dynamics.guard-scale-ties-vs-half-up",
            run: () => Laws.Claim(
                claim: GuardScaleTieDisciplineClaims.RoundToGuardScaleTiesVsHalfUpSurface,
                lawId: "dynamics.guard-scale-ties-vs-half-up"
            )
        ),
        Case(
            id: "dynamics.guard-scale-public-divergence-search",
            run: () => Laws.Claim(
                claim: GuardScaleTieDisciplineClaims.GuardScalePublicDivergenceSearchSurface,
                lawId: "dynamics.guard-scale-public-divergence-search"
            )
        ),
    ];
}
