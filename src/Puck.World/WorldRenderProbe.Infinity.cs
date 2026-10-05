using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldRenderProbe {
    private readonly Dictionary<object, (string Name, WorldInfinityViewPlan Plan)> m_infinityPlans = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Publishes the binder's current finite infinity plan on the console/frame owner thread.
    /// Other cameras of that root share this entry and do not multiply its count or cap.</summary>
    /// <param name="owner">The binder root whose lifetime owns the plan.</param>
    /// <param name="name">The root's existing instance name.</param>
    /// <param name="plan">The admitted scene plan, including named depth and capacity fallbacks.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void RegisterInfinityPlan(object owner, string name, WorldInfinityViewPlan plan) {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(plan);
        m_infinityPlans[owner] = (name, plan);
    }
    /// <summary>Withdraws one retired binder root without dropping another root's plan.
    /// Read and write only on the console/frame owner thread.</summary>
    /// <param name="owner">The exact root being released.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    public void UnregisterInfinityPlan(object owner) {
        ArgumentNullException.ThrowIfNull(owner);
        m_infinityPlans.Remove(key: owner);
    }
    /// <summary>Formats each live root's planned infinity count against its cap and its named fallbacks.
    /// GPU allocations and completed dispatch counts remain in the ordinary live render budget.</summary>
    /// <returns>The current plans, or an explicit empty inventory before any plan is registered.</returns>
    public string DescribeInfinityBudget() => ((m_infinityPlans.Count == 0) ? "infinity views: none" :
        string.Join(separator: " | ", values: m_infinityPlans.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(selector: entry => $"{entry.Name}: {string.Join(separator: "; ", values: entry.Plan.Describe())}")));
}
