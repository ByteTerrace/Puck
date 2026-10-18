using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The window a running instance's feed captures, or why it is none: a monitor capture, a capture that has not opened
    // its window yet, or another producer's feed.
    private static ISourcePassthroughWindow? PassthroughWindowOf(IWorldImageFeed feed, string instance, out string? fault) {
        switch (feed) {
            case CaptureSlotFeed { Feed.MonitorIndex: { } monitor }:
                fault = $"'{instance}' captures monitor {monitor}, not a window";

                return null;
            case CaptureSlotFeed { Feed.Source.Window: { } window }:
                fault = null;

                return window;
            case CaptureSlotFeed slot:
                fault = $"'{instance}' has not opened its {slot.Feed.Label} yet{((slot.Fault is { } reason) ? $" ({reason})" : string.Empty)}";

                return null;
            default:
                fault = $"'{instance}' shows the {feed.Descriptor.Producer} producer, not a window capture";

                return null;
        }
    }
}