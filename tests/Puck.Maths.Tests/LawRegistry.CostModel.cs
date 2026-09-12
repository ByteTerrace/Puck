namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain CostBoundDomain = new(
        Key: "cost-bound-arithmetic", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CostBudgetDomain = new(
        Key: "cost-model-budgets", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CostConversionDomain = new(
        Key: "cost-model-conversions", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    private static LawCase[] CostModelCases() => [
        Case(id: "core.cost-bound-construction", run: () => Laws.Claim(
            lawId: "core.cost-bound-construction", claim: CostModelClaims.BoundConstruction)),
        Case(id: "core.cost-bound-status-propagation", run: () => Laws.Claim(
            lawId: "core.cost-bound-status-propagation", claim: CostModelClaims.StatusPropagation)),
        Case(id: "core.cost-bound-arithmetic-vs-oracle", run: () => Laws.SweptClaim(
            lawId: "core.cost-bound-arithmetic-vs-oracle", domain: CostBoundDomain,
            tier: Tier.Default, width: 1, claim: CostModelClaims.BoundArithmetic)),
        Case(id: "core.cost-model-construction", run: () => Laws.Claim(
            lawId: "core.cost-model-construction", claim: CostModelClaims.ProfileConstruction)),
        Case(id: "core.cost-model-rate-contract", run: () => Laws.Claim(
            lawId: "core.cost-model-rate-contract", claim: CostModelClaims.RateContract)),
        Case(id: "core.cost-model-budgets-vs-oracle", run: () => Laws.SweptClaim(
            lawId: "core.cost-model-budgets-vs-oracle", domain: CostBudgetDomain,
            tier: Tier.Default, width: 3, claim: CostModelClaims.Budgets)),
        Case(id: "core.cost-model-conversions-vs-oracle", run: () => Laws.SweptClaim(
            lawId: "core.cost-model-conversions-vs-oracle", domain: CostConversionDomain,
            tier: Tier.Default, width: 1, claim: CostModelClaims.Conversions)),
    ];
}
