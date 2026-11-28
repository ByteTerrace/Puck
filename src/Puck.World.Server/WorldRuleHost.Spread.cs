using System.Globalization;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private RuleTickAllowance? m_allowance;

    // The tick's firing allowance for the compilation just installed: the slowest device's budget less what recurs
    // unmetered (WorldRuleTimePricing.FiringAllowancePicoseconds), each rule and interaction priced at one firing on that
    // device from its own line of the sheet. Every figure is a document constant, so every machine and every authority
    // carries the same work on the same tick. A rate's worth of carrying ticks in a row is a sustained overrun.
    private static RuleTickAllowance? Allowance(WorldRuleCompilation compilation) {
        var pricing = compilation.Pricing;
        var lines = compilation.WorkBudget.RuleLines;
        var rules = compilation.Order.Rules;
        var interactions = compilation.Interactions;

        if (lines.Count != (rules.Length + interactions.Length)) {
            return null;
        }

        var calibration = pricing.Slowest.Calibration;
        var prices = new Dictionary<CompiledRule, long>(capacity: lines.Count, comparer: ReferenceEqualityComparer.Instance);

        for (var index = 0; (index < rules.Length); index++) {
            prices[rules[index]] = calibration.FiringPicoseconds(line: lines[index]);
        }
        for (var index = 0; (index < interactions.Length); index++) {
            prices[interactions[index]] = calibration.FiringPicoseconds(line: lines[rules.Length + index]);
        }

        return new RuleTickAllowance(allowance: pricing.FiringAllowancePicoseconds, prices: prices, sustainedTicks: pricing.RateHz);
    }
    // Opens the tick's account before the rule pass and counts what the tick before it carried.
    private void BeginTickAllowance() {
        if ((m_allowance is not { } allowance) || !allowance.BeginTick()) {
            return;
        }
        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.RuleCarriedTicks);
        if (allowance.Sustained) {
            WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.RuleSustainedOverrun);
        }
    }

    /// <summary>Describes the tick's firing allowance for <c>world.budget.rules</c>: the allowance, what the last tick
    /// charged and the most any tick charged, how many ticks in a row and in all carried due work, and, once a rate's
    /// worth of ticks in a row has carried, the sustained overrun.</summary>
    /// <returns>The line, or <see langword="null"/> when no allowance is installed.</returns>
    public string? DescribeSpread() {
        if (m_allowance is not { } allowance) {
            return null;
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"spread: firing allowance {RuleTime.Milliseconds(picoseconds: allowance.Allowance)} a tick; last tick charged {RuleTime.Milliseconds(picoseconds: allowance.Spent)}{(allowance.Exhausted ? " and carried" : string.Empty)}, peak {RuleTime.Milliseconds(picoseconds: allowance.PeakSpent)}; carried on {allowance.CarryingTicks} tick(s) in a row, {allowance.CarriedTicks} in all{(allowance.Sustained ? $"; sustained overrun: due work has carried for {allowance.CarryingTicks} ticks in a row (at least {allowance.SustainedTicks}), so the world's spend is not keeping up with its allowance" : string.Empty)}");
    }
}
