using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A stateless, do-nothing <see cref="IClockedComponent"/>. It stands in as the cartridge's clocked facet when the
/// inserted cartridge has no timed hardware, so the component clock can register one forwarder for the cartridge slot
/// regardless of which mapper is loaded — only an MBC3 with a real-time clock actually advances on a tick. Because it
/// holds no state, one shared instance is safe across machines and never appears in a snapshot.
/// </summary>
public sealed class NullClockedComponent : IClockedComponent {
    /// <summary>The shared instance; it carries no state, so it is safe to reuse everywhere.</summary>
    public static readonly NullClockedComponent Instance = new();

    private NullClockedComponent() { }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Lcd;

    /// <inheritdoc/>
    public void Tick() {
        // Nothing to advance: an untimed cartridge has no clock.
    }
}
