namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain Dynamics = new(
        Key: "dynamics",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] DynamicsCases() => [
        SweptCase(
            claim: Subjects.DynamicsCreateConstantsVsOracle,
            domain: Dynamics,
            id: "dynamics.create-constants-vs-oracle",
            width: 3
        ),
        SweptCase(
            claim: Subjects.DynamicsStepVsEvaluate,
            domain: Dynamics,
            id: "dynamics.step-vs-evaluate-close-agreement",
            width: 3
        ),
        ClaimCase(
            claim: Subjects.DynamicsCriticalAndOverdampedNeverOvershoot,
            id: "dynamics.critical-and-overdamped-never-overshoot"
        ),
        ClaimCase(
            claim: Subjects.DynamicsSteadyStateExact,
            id: "dynamics.steady-state-exact"
        ),
        ClaimCase(
            claim: Subjects.DynamicsInitialResponseSign,
            id: "dynamics.initial-response-sign"
        ),
        ClaimCase(
            claim: Subjects.DynamicsRefusalsAndOverflow,
            id: "dynamics.refusals-and-overflow"
        ),
        SweptCase(
            claim: Subjects.DynamicsVectorLanesIndependent,
            domain: Dynamics,
            id: "dynamics.vector-lanes-independent",
            width: 6
        ),
        ClaimCase(
            claim: GuardScaleTieDisciplineClaims.RoundToGuardScaleTiesVsHalfUpSurface,
            id: "dynamics.guard-scale-ties-vs-half-up"
        ),
        ClaimCase(
            claim: GuardScaleTieDisciplineClaims.GuardScalePublicDivergenceSearchSurface,
            id: "dynamics.guard-scale-public-divergence-search"
        ),
    ];
}
