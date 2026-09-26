using Puck.Input;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    /// <summary>Finds the window a running source instance captures, the one a passthrough source delivers to.</summary>
    /// <param name="instance">The source instance's name.</param>
    /// <param name="fault">Why there is no window, when this returns <see langword="null"/>.</param>
    /// <returns>The window its capture feed shows now, or <see langword="null"/> when the instance is not running, is no
    /// window capture, captures a whole monitor, or has not opened its window yet.</returns>
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
