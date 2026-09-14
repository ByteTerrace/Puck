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
        Case(
            id: "core.cost-bound-construction",
            run: () => Laws.Claim(
                claim: CostModelClaims.BoundConstruction,
                lawId: "core.cost-bound-construction"
            )
        ),
        Case(
            id: "core.cost-bound-status-propagation",
            run: () => Laws.Claim(
                claim: CostModelClaims.StatusPropagation,
                lawId: "core.cost-bound-status-propagation"
            )
        ),
        Case(
            id: "core.cost-bound-arithmetic-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: CostModelClaims.BoundArithmetic,
                domain: CostBoundDomain,
                lawId: "core.cost-bound-arithmetic-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "core.cost-model-construction",
            run: () => Laws.Claim(
                claim: CostModelClaims.ProfileConstruction,
                lawId: "core.cost-model-construction"
            )
        ),
        Case(
            id: "core.cost-model-rate-contract",
            run: () => Laws.Claim(
                claim: CostModelClaims.RateContract,
                lawId: "core.cost-model-rate-contract"
            )
        ),
        Case(
            id: "core.cost-model-budgets-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: CostModelClaims.Budgets,
                domain: CostBudgetDomain,
                lawId: "core.cost-model-budgets-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "core.cost-model-conversions-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: CostModelClaims.Conversions,
                domain: CostConversionDomain,
                lawId: "core.cost-model-conversions-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
    ];
}
