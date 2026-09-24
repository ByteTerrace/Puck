namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain CostBoundDomain = new(
        Key: "cost-bound-arithmetic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CostBudgetDomain = new(
        Key: "cost-model-budgets",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CostConversionDomain = new(
        Key: "cost-model-conversions",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] CostModelCases() => [
        ClaimCase(
            claim: CostModelClaims.BoundConstruction,
            id: "core.cost-bound-construction"
        ),
        ClaimCase(
            claim: CostModelClaims.StatusPropagation,
            id: "core.cost-bound-status-propagation"
        ),
        SweptCase(
            claim: CostModelClaims.BoundArithmetic,
            domain: CostBoundDomain,
            id: "core.cost-bound-arithmetic-vs-oracle",
            width: 1
        ),
        ClaimCase(
            claim: CostModelClaims.ProfileConstruction,
            id: "core.cost-model-construction"
        ),
        ClaimCase(
            claim: CostModelClaims.RateContract,
            id: "core.cost-model-rate-contract"
        ),
        SweptCase(
            claim: CostModelClaims.Budgets,
            domain: CostBudgetDomain,
            id: "core.cost-model-budgets-vs-oracle",
            width: 3
        ),
        SweptCase(
            claim: CostModelClaims.Conversions,
            domain: CostConversionDomain,
            id: "core.cost-model-conversions-vs-oracle",
            width: 1
        ),
    ];
}
