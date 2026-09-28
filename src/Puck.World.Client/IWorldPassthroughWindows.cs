using Puck.Input;

namespace Puck.World.Client;

/// <summary>Finds the window a running source instance captures, the one a passthrough source delivers to. The screen
/// binder that owns the instances' capture feeds answers it.</summary>
public interface IWorldPassthroughWindows {
    /// <summary>Finds the window a running source instance captures.</summary>
    /// <param name="instance">The source instance's name.</param>
    /// <param name="fault">Why there is no window, when this returns <see langword="null"/>.</param>
    /// <returns>The window its capture feed shows now, or <see langword="null"/> when the instance is not running, is no
    /// window capture, captures a whole monitor, or has not opened its window yet. A capture that reopens onto a window
    /// answers a different object.</returns>
    ISourcePassthroughWindow? PassthroughWindowOf(string instance, out string? fault);
}
