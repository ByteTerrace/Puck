using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    /// <inheritdoc/>
    /// <remarks>The extent is the one the capture last received a frame at, which follows the window as it resizes.</remarks>
    public bool TrySourceExtent(string instance, out int width, out int height) {
        if (
            (FeedOf(instance: instance) is CaptureSlotFeed { Feed.Source: { } source }) &&
            (source.SourceWidth > 0) &&
            (source.SourceHeight > 0)
        ) {
            width = source.SourceWidth;
            height = source.SourceHeight;

            return true;
        }

        width = 0;
        height = 0;

        return false;
    }
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