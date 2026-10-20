using Puck.Input;

namespace Puck.World;

/// <summary>
/// Offers one raw window input event to each registered filter in order until one consumes it: the single instance
/// <see cref="WorldBootComposition"/> contributes as the <see cref="IWindowInputFilter"/> capability, since
/// <c>IHostContext.HoldsCapability</c> resolves exactly one instance per capability type.
/// </summary>
/// <param name="filters">The filters, offered every event in this order.</param>
public sealed class WorldWindowInputFilters(params IWindowInputFilter[] filters) : IWindowInputFilter {
    /// <inheritdoc/>
    public bool Intercept(in WindowInputEvent inputEvent) {
        foreach (var filter in filters) {
            if (filter.Intercept(inputEvent: in inputEvent)) {
                return true;
            }
        }

        return false;
    }
}
