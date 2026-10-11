using System.Text.Json.Nodes;
using Puck.Cli.Azure;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>Laws for how a production deployment writes a Front Door rule set whose rules change order. Resource Manager
/// writes a set's rules all at once and Front Door refuses a write that leaves two rules at one order, so the live
/// rules a deployment displaces move aside first.</summary>
public sealed class FrontDoorRuleOrdersLawTests {
    private const string Portal = (Profile + "/ruleSets/portal");
    private const string Profile = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Cdn/profiles/fd";

    private static FrontDoorRuleOrders.Rule Rule(string name, int order, string set = Portal) => new(
        Name: name,
        Order: order,
        RuleSet: set
    );
    private static JsonNode Change(string changeType, string id, int? before = null, int? after = null) {
        var change = new JsonObject { ["changeType"] = changeType, ["resourceId"] = id };

        if (before is { } b) { change["before"] = new JsonObject { ["properties"] = new JsonObject { ["order"] = b } }; }
        if (after is { } a) { change["after"] = new JsonObject { ["properties"] = new JsonObject { ["order"] = a } }; }
        return change;
    }
    private static JsonNode Plan(params JsonNode[] changes) => new JsonObject { ["changes"] = new JsonArray(items: changes) };

    // The portal rule set as the last successful production deployment left it, and as main.bicep declares it now.
    private static readonly FrontDoorRuleOrders.Rule[] Before = [Rule("SpaContentSecurityPolicy", 0), Rule("SpaUrlRewriteAssets", 1), Rule("SpaUrlRewriteGeneral", 2), Rule("SpaContentEncodingOverwrite", 3)];
    private static readonly FrontDoorRuleOrders.Rule[] After = [
        Rule("SpaContentSecurityPolicy", 0), Rule("SpaCacheHashed", 1), Rule("SpaCacheMutable", 2), Rule("SpaCacheReleaseMarker", 3),
        Rule("SpaUrlRewriteAssets", 4), Rule("SpaUrlRewriteGeneral", 5), Rule("SpaContentEncodingOverwrite", 6),
    ];

    // Applies each write as Front Door does: a write that leaves two rules at one order fails and changes nothing.
    private static bool Writes(List<FrontDoorRuleOrders.Rule> set, FrontDoorRuleOrders.Rule write) {
        var others = set.Where(predicate: rule => !string.Equals(a: rule.Name, b: write.Name, comparisonType: StringComparison.OrdinalIgnoreCase)).ToList();

        if (others.Any(predicate: rule => (rule.Order == write.Order))) { return false; }
        others.Add(item: write);
        set.Clear();
        set.AddRange(collection: others);
        return true;
    }
    private static IEnumerable<T[]> Permutations<T>(T[] items) {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; (i < items.Length); i++) {
            var rest = items.Where(predicate: (_, index) => (index != i)).ToArray();

            foreach (var tail in Permutations(items: rest)) { yield return [items[i], .. tail]; }
        }
    }

    [Fact]
    public void ThePlanDeclaresEachRuleItLeavesAtTheOrderItEndsWith() {
        var declared = FrontDoorRuleOrders.Declared(plan: Plan(
            Change("Create", (Portal + "/rules/SpaCacheHashed"), after: 1),
            Change("Modify", (Portal + "/rules/SpaUrlRewriteAssets"), before: 1, after: 4),
            Change("NoChange", (Portal + "/rules/SpaContentSecurityPolicy"), before: 0, after: 0),
            Change("NoChange", (Portal + "/rules/SpaUrlRewriteGeneral"), before: 5),
            Change("NoChange", Portal),
            Change("Ignore", (Portal + "/rules/Ignored"), after: 9),
            Change("Modify", (Profile + "/originGroups/api"), before: 1, after: 2)
        ));

        Assert.Equal(
            actual: declared,
            expected: [Rule("SpaCacheHashed", 1), Rule("SpaUrlRewriteAssets", 4), Rule("SpaContentSecurityPolicy", 0), Rule("SpaUrlRewriteGeneral", 5)]
        );
    }
    [Fact]
    public void InsertingRulesAheadOfLiveOnesMovesTheLiveRulesTheyDisplaceAboveEveryOrder() {
        var moves = FrontDoorRuleOrders.Park(
            declared: After,
            live: Before
        );

        Assert.Equal(
            actual: moves.Select(selector: move => (move.Rule.Name, move.Rule.Order, move.To)),
            expected: [("SpaUrlRewriteAssets", 1, 7), ("SpaUrlRewriteGeneral", 2, 8), ("SpaContentEncodingOverwrite", 3, 9)]
        );
    }
    [Fact]
    public void ADeploymentWritesInAnyOrderOnceTheMovesAreMade() {
        var live = Before.ToList();

        foreach (var move in FrontDoorRuleOrders.Park(
            declared: After,
            live: Before
        )) { Assert.True(condition: Writes(set: live, write: (move.Rule with { Order = move.To }))); }
        foreach (var order in Permutations(items: After)) {
            var written = live.ToList();

            foreach (var write in order) { Assert.True(condition: Writes(set: written, write: write), userMessage: $"{write.Name} at {write.Order} collided after the moves."); }
            Assert.Equal(
                actual: written.OrderBy(keySelector: rule => rule.Order),
                expected: After
            );
        }
    }
    [Fact]
    public void WithoutTheMovesSomeWriteOrderCollides() {
        var live = Before.ToList();

        Assert.False(condition: Writes(
            set: live,
            write: After[1]
        ));
    }
    [Fact]
    public void ASetAlreadyAtItsDeclaredOrdersMovesNothing() =>
        Assert.Empty(collection: FrontDoorRuleOrders.Park(
            declared: After,
            live: After
        ));
    [Fact]
    public void ARuleOutsideTheDeclaredOrdersMovesNothing() =>
        Assert.Empty(collection: FrontDoorRuleOrders.Park(
            declared: After,
            live: [Rule("SpaContentSecurityPolicy", 0), Rule("SpaUrlRewriteAssets", 4), Rule("Retired", 20)]
        ));
    [Fact]
    public void ARuleTheTemplateNoLongerNamesIsMovedWhenItHoldsADeclaredOrder() {
        var moves = FrontDoorRuleOrders.Park(
            declared: After,
            live: [Rule("Retired", 2), Rule("SpaContentSecurityPolicy", 0)]
        );

        Assert.Equal(
            actual: moves.Select(selector: move => (move.Rule.Name, move.To)),
            expected: [("Retired", 7)]
        );
    }
    [Fact]
    public void SwappedOrdersMoveBothRulesSoNeitherWriteCollides() {
        var live = new[] { Rule("A", 1), Rule("B", 2) };
        var declared = new[] { Rule("A", 2), Rule("B", 1) };
        var set = live.ToList();

        foreach (var move in FrontDoorRuleOrders.Park(
            declared: declared,
            live: live
        )) { Assert.True(condition: Writes(set: set, write: (move.Rule with { Order = move.To }))); }
        foreach (var write in declared) { Assert.True(condition: Writes(set: set, write: write)); }
        Assert.Equal(
            actual: set.OrderBy(keySelector: rule => rule.Order),
            expected: [declared[1], declared[0]]
        );
    }
    [Fact]
    public void RuleSetsAreSeparateAndCompareWithoutRegardToCase() {
        var other = (Profile + "/ruleSets/blob");
        var moves = FrontDoorRuleOrders.Park(
            declared: [Rule("X", 1), Rule(name: "Y", order: 1, set: other)],
            live: [Rule(name: "x", order: 1, set: Portal.ToUpperInvariant()), Rule(name: "Z", order: 1, set: other)]
        );

        Assert.Equal(
            actual: moves.Select(selector: move => (move.Rule.Name, move.To)),
            expected: [("Z", 2)]
        );
    }
}
