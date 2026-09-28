namespace Puck.Hosting;

/// <summary>The passes and pass-pixels one render of an instance records at its current grids.</summary>
/// <param name="Passes">The number of planned passes.</param>
/// <param name="Pixels">The sum of their pixel extents.</param>
public readonly record struct RenderGraphPassCost(int Passes, long Pixels);
/// <summary>Optional current-grid pricing for instances whose passes use different extents. An absent price retains
/// the instance's declared pass count times its output pixels.</summary>
public interface IRenderGraphPassCosts {
    /// <summary>Resolves an instance's current price at its demanded output extent.</summary>
    /// <param name="instance">The instance name.</param>
    /// <param name="width">The output width in pixels.</param>
    /// <param name="height">The output height in pixels.</param>
    /// <returns>The current price, or null to use the declared uniform-extent price.</returns>
    RenderGraphPassCost? CostOf(string instance, int width, int height);
}
