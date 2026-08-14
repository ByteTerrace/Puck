using Puck.Commands;

namespace Puck.Launcher;

/// <summary>Contributes a host-specific hold condition to the launcher's <see cref="TextCommandSource"/>.</summary>
public interface ITextCommandHoldGate {
    /// <summary>Returns whether queued text commands must remain held.</summary>
    /// <returns><see langword="true"/> while the source must not drain queued lines; otherwise <see langword="false"/>.</returns>
    bool IsHolding();
}
