using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder : IWorldPassthroughWindows {
    /// <inheritdoc/>
    public ISourcePassthroughWindow? PassthroughWindowOf(string instance, out string? fault) {
        fault = $"no source instance '{instance}' is running";

        return ((FeedOf(instance: instance) is { } feed)
            ? PassthroughWindowOf(
                fault: out fault,
                feed: feed,
                instance: instance
            )
            : null);
    }
}