using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder : IWorldPassthroughWindows {
    /// <inheritdoc/>
    public ISourcePassthroughWindow? PassthroughWindowOf(string instance, out string? fault) {
        switch (FeedOf(instance: instance)) {
            case null:
                fault = $"no source instance '{instance}' is running";

                return null;
            case CaptureSlotFeed { Feed.MonitorIndex: { } monitor }:
                fault = $"'{instance}' captures monitor {monitor}, not a window";

                return null;
            case CaptureSlotFeed { Feed.Source.Window: { } window }:
                fault = null;

                return window;
            case CaptureSlotFeed slot:
                fault = $"'{instance}' has not opened its {slot.Feed.Label} yet{((slot.Fault is { } reason) ? $" ({reason})" : string.Empty)}";

                return null;
            case var feed:
                fault = $"'{instance}' shows the {feed.Descriptor.Producer} producer, not a window capture";

                return null;
        }
    }
}
