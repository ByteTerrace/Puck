namespace Puck.Abstractions.Presentation;

/// <summary>Whether a presentation datum exists and has been measured.</summary>
public enum PresentationReadingState {
    /// <summary>The source does not exist in this host or scope.</summary>
    Absent,
    /// <summary>The source exists but has no measurement yet.</summary>
    Unmeasured,
    /// <summary>The value is available, including a measured zero.</summary>
    Present,
}

/// <summary>A read-only presentation value; never simulation state.</summary>
/// <param name="State">The source's availability.</param>
/// <param name="Value">The numeric value in the registered source's units.</param>
/// <param name="Text">Optional text instead of a numeric display.</param>
/// <param name="Sequence">The source sample's index, or zero for an unindexed source.</param>
public readonly record struct PresentationReading(PresentationReadingState State, double Value = 0, string? Text = null, long Sequence = 0) {
    /// <summary>Whether a present value is nonzero, or its text is nonempty.</summary>
    public bool Holds => State == PresentationReadingState.Present && (Text is { Length: > 0 } || (double.IsFinite(Value) && Value != 0));
}

/// <summary>Copies the newest values in chronological order into caller-owned storage, returning the count.
/// A partial window is right-aligned by the consumer, not padded by the source.</summary>
/// <param name="destination">The maximum requested window; borrowed only for this call.</param>
/// <returns>The number of entries written, at most the destination length.</returns>
public delegate int PresentationSeriesReader(Span<PresentationReading> destination);

/// <summary>Named read-only presentation sources. Owners register once at composition and readers borrow values
/// on the host pump. Series remain in their owner's storage; this registry keeps no second sampling ring.</summary>
public sealed class PresentationReadings {
    private readonly Dictionary<string, (Func<PresentationReading> Read, PresentationSeriesReader? Recent)> m_sources = new(StringComparer.Ordinal);

    /// <summary>Registers a unique name and its live reader, optionally with a recent-series reader.</summary>
    /// <param name="name">An ordinal source name, independent of HUD binding syntax.</param>
    /// <param name="read">Reads the current datum without changing its owner.</param>
    /// <param name="recent">Copies recent samples, or null for a scalar/text-only source.</param>
    /// <exception cref="ArgumentException">The name is blank or already registered.</exception>
    /// <exception cref="ArgumentNullException">The scalar reader is null.</exception>
    public void Register(string name, Func<PresentationReading> read, PresentationSeriesReader? recent = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        m_sources.Add(name, (read, recent));
    }

    /// <summary>Reads a registered source; an unknown name is explicitly absent.</summary>
    /// <param name="name">The registered name.</param>
    /// <returns>The live datum.</returns>
    public PresentationReading Read(string name) => m_sources.TryGetValue(name, out var source) ? source.Read() : default;

    /// <summary>Copies a source's recent values into caller-owned storage.</summary>
    /// <param name="name">The registered name.</param>
    /// <param name="destination">The requested window.</param>
    /// <returns>The count written, or zero when the source has no recent series.</returns>
    public int CopyRecent(string name, Span<PresentationReading> destination) => m_sources.TryGetValue(name, out var source) && source.Recent is { } recent ? recent(destination) : 0;
}
