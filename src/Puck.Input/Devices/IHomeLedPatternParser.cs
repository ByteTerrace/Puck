using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// Implemented by a parser whose controller plays Home LED brightness patterns itself (the Switch Pro). Implementing
/// it advertises <see cref="GamepadOutputCapabilities.HomeLedPattern"/>.
/// </summary>
public interface IHomeLedPatternParser {
    /// <summary>Plays a pattern on the Home LED, replacing the current one.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A task that completes when the write has been issued or queued behind the controller's spacing.</returns>
    ValueTask SetHomeLedPatternAsync(HomeLedPattern pattern, CancellationToken cancellationToken = default);
}
