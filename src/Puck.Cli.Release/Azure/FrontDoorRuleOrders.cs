using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

/// <summary>Plans how a production deployment writes a Front Door rule set whose rules change order. Azure Resource
/// Manager writes each rule as its own child deployment, all at once, and Front Door refuses a write that leaves two rules
/// of one set at the same order. A deployment that gives a new or moved rule an order a live rule still holds therefore
/// fails with "The delivery rules order values cannot have duplicates", and a swap of two rules' orders can never
/// succeed. <see cref="Park"/> names the live rules to move aside first: each goes above every order the set holds or
/// will hold, where the deployment's own write returns it to its declared order.</summary>
public static partial class FrontDoorRuleOrders {
    /// <summary>One rule of one Front Door rule set.</summary>
    /// <param name="RuleSet">The rule set's resource id, up to its name, compared without regard to case.</param>
    /// <param name="Name">The rule's name, compared without regard to case.</param>
    /// <param name="Order">The rule's order in its set.</param>
    public readonly record struct Rule(string RuleSet, string Name, int Order);

    /// <summary>A live rule to move to a temporary order before the deployment writes.</summary>
    /// <param name="Rule">The rule as it is live now.</param>
    /// <param name="To">The temporary order, above every order the set holds or will hold.</param>
    public readonly record struct Move(Rule Rule, int To);

    [GeneratedRegex(pattern: @"^(?<set>/.*/providers/Microsoft\.Cdn/profiles/[^/]+/ruleSets/[^/]+)/rules/(?<rule>[^/]+)$", options: RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuleId();

    /// <summary>Reads the rules a what-if plan says the deployment will leave in place: each rule it creates, modifies or
    /// leaves unchanged, at the order the plan says it ends with.</summary>
    /// <param name="plan">The <c>az deployment group what-if</c> result.</param>
    /// <returns>The declared rules, in the plan's order.</returns>
    public static IReadOnlyList<Rule> Declared(JsonNode plan) {
        var rules = new List<Rule>();

        foreach (var change in (plan["changes"]?.AsArray() ?? [])) {
            if (change?["changeType"]?.GetValue<string>() is not ("Create" or "Modify" or "NoChange" or "Deploy")) { continue; }
            if (change["resourceId"]?.GetValue<string>() is not { } id) { continue; }
            var match = RuleId().Match(input: id);

            if (!match.Success) { continue; }
            var order = (change["after"]?["properties"]?["order"] ?? change["before"]?["properties"]?["order"]);

            if (order is null) { continue; }
            rules.Add(item: new Rule(
                Name: match.Groups["rule"].Value,
                Order: order.GetValue<int>(),
                RuleSet: match.Groups["set"].Value
            ));
        }
        return rules;
    }

    /// <summary>Names the live rules that must move before the deployment writes. A live rule moves when another rule of
    /// its set is declared at the order it holds: the deployment may write that rule before this one leaves. Every rule
    /// that holds a contested order moves, since the writes run in no fixed order. The moves go in ascending live
    /// order, each to an order above every order the set holds now or will hold.</summary>
    /// <param name="declared">The rules the deployment will leave, from <see cref="Declared"/>.</param>
    /// <param name="live">Every rule the declared rule sets hold now.</param>
    /// <returns>The moves, empty when the deployment can write without a collision.</returns>
    public static IReadOnlyList<Move> Park(IReadOnlyList<Rule> declared, IReadOnlyList<Rule> live) => [];
}