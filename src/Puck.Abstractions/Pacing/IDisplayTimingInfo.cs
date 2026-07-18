namespace Puck.Abstractions.Pacing;

/// <summary>
/// Reports the active display path's signal timing and independently advertised variable-refresh capabilities. Signal
/// timing is not a VRR range: providers must leave <see cref="DisplayTimingSnapshot.VariableRefresh"/> unknown unless
/// the display explicitly advertises Adaptive-Sync, HDMI VRR, or a recognized vendor VRR protocol.
/// </summary>
public interface IDisplayTimingInfo {
    /// <summary>Queries the display path occupied by the window.</summary>
    /// <returns>The latest signal and variable-refresh capability snapshot.</returns>
    DisplayTimingSnapshot QueryDisplayTiming();

    /// <summary>
    /// A monotonically increasing version that changes when the window moves to another display or display timing may
    /// have changed. Consumers re-query only when this advances.
    /// </summary>
    ulong DisplayConfigurationVersion { get; }
}

/// <summary>The active display path's physical signal timing.</summary>
public readonly record struct DisplaySignalTiming {
    /// <summary>An unavailable signal timing.</summary>
    public static DisplaySignalTiming Unknown => default;

    /// <summary>Creates a known physical signal timing.</summary>
    /// <param name="hertz">The active physical vertical scan frequency, in Hz.</param>
    public DisplaySignalTiming(double hertz) {
        if (!double.IsFinite(d: hertz) || (hertz <= 0.0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(hertz), actualValue: hertz, message: "A display signal frequency must be positive and finite.");
        }

        Hertz = hertz;
    }

    /// <summary>The active physical vertical scan frequency, in Hz; zero only for <see cref="Unknown"/>.</summary>
    public double Hertz { get; }

    /// <summary>Whether the physical signal frequency is known.</summary>
    public bool IsKnown => (Hertz > 0.0);
}

/// <summary>Whether variable refresh is positively known to be supported by the display.</summary>
public enum VariableRefreshSupport {
    /// <summary>The platform cannot establish support. This is not evidence of non-support.</summary>
    Unknown,

    /// <summary>The platform positively reports that variable refresh is unsupported.</summary>
    Unsupported,

    /// <summary>The display explicitly advertises a recognized variable-refresh protocol.</summary>
    Supported,
}

/// <summary>The display-identification declaration from which a variable-refresh range was obtained.</summary>
[Flags]
public enum VariableRefreshSource {
    /// <summary>No recognized declaration was available.</summary>
    None = 0,

    /// <summary>A VESA DisplayID 2.1 Adaptive-Sync data block.</summary>
    DisplayIdAdaptiveSync = (1 << 0),

    /// <summary>An HDMI Forum vendor-specific data block carrying HDMI VRR bounds.</summary>
    HdmiForum = (1 << 1),

    /// <summary>An AMD vendor-specific data block carrying FreeSync bounds.</summary>
    AmdFreeSync = (1 << 2),
}

/// <summary>An explicitly advertised variable-refresh interval.</summary>
public readonly record struct VariableRefreshRange {
    /// <summary>Creates a validated variable-refresh interval.</summary>
    /// <param name="minimumHertz">The advertised minimum refresh rate, in Hz.</param>
    /// <param name="maximumHertz">
    /// The advertised maximum refresh rate, in Hz, or <see langword="null"/> when the protocol defines the current
    /// video mode's signal rate as the maximum.
    /// </param>
    public VariableRefreshRange(double minimumHertz, double? maximumHertz) {
        if (!double.IsFinite(d: minimumHertz) || (minimumHertz <= 0.0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(minimumHertz), actualValue: minimumHertz, message: "A VRR minimum must be positive and finite.");
        }

        if (
            (maximumHertz is { } maximum) &&
            (!double.IsFinite(d: maximum) || (maximum <= minimumHertz))
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(maximumHertz), actualValue: maximumHertz, message: "A VRR maximum must be finite and greater than its minimum.");
        }

        MinimumHertz = minimumHertz;
        MaximumHertz = maximumHertz;
    }

    /// <summary>The advertised minimum refresh rate, in Hz.</summary>
    public double MinimumHertz { get; }

    /// <summary>The advertised maximum, or null when the active video mode supplies that bound.</summary>
    public double? MaximumHertz { get; }
}

/// <summary>Variable-refresh support, its advertised interval, and the declaration that established it.</summary>
public readonly record struct VariableRefreshCapabilities {
    private VariableRefreshCapabilities(VariableRefreshSupport support, VariableRefreshRange? range, VariableRefreshSource source) {
        Support = support;
        Range = range;
        Source = source;
    }

    /// <summary>No reliable variable-refresh capability information.</summary>
    public static VariableRefreshCapabilities Unknown => default;

    /// <summary>A positive platform report that variable refresh is unsupported.</summary>
    public static VariableRefreshCapabilities Unsupported => new(
        support: VariableRefreshSupport.Unsupported,
        range: null,
        source: VariableRefreshSource.None
    );

    /// <summary>Creates capabilities from one or more explicit display-identification declarations.</summary>
    /// <param name="range">The advertised variable-refresh interval.</param>
    /// <param name="source">The recognized declaration or declarations that supplied the interval.</param>
    /// <returns>Validated supported capabilities.</returns>
    public static VariableRefreshCapabilities CreateSupported(VariableRefreshRange range, VariableRefreshSource source) {
        const VariableRefreshSource knownSources = (
            VariableRefreshSource.DisplayIdAdaptiveSync |
            VariableRefreshSource.HdmiForum |
            VariableRefreshSource.AmdFreeSync
        );

        if ((source == VariableRefreshSource.None) || ((source & ~knownSources) != 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(source), actualValue: source, message: "Supported VRR capabilities require an explicit source.");
        }

        if (
            !double.IsFinite(d: range.MinimumHertz) ||
            (range.MinimumHertz <= 0.0) ||
            ((range.MaximumHertz is { } maximum) && (!double.IsFinite(d: maximum) || (maximum <= range.MinimumHertz)))
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(range), actualValue: range, message: "Supported VRR capabilities require a valid, positive-width interval.");
        }

        return new VariableRefreshCapabilities(
            support: VariableRefreshSupport.Supported,
            range: range,
            source: source
        );
    }

    /// <summary>The support state. Unknown and unsupported are deliberately distinct.</summary>
    public VariableRefreshSupport Support { get; }

    /// <summary>The explicit advertised range when <see cref="Support"/> is <see cref="VariableRefreshSupport.Supported"/>.</summary>
    public VariableRefreshRange? Range { get; }

    /// <summary>The declaration or declarations that established support.</summary>
    public VariableRefreshSource Source { get; }
}

/// <summary>A display path's independent physical-signal and variable-refresh facts.</summary>
/// <param name="Signal">The active physical signal timing.</param>
/// <param name="VariableRefresh">Explicit variable-refresh capabilities, if discoverable.</param>
public readonly record struct DisplayTimingSnapshot(DisplaySignalTiming Signal, VariableRefreshCapabilities VariableRefresh) {
    /// <summary>A snapshot in which neither signal timing nor VRR capability is available.</summary>
    public static DisplayTimingSnapshot Unknown => default;

    /// <summary>Whether at least one display fact was discovered.</summary>
    public bool IsKnown => Signal.IsKnown || (VariableRefresh.Support != VariableRefreshSupport.Unknown);
}
