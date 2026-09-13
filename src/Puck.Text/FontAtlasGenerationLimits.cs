namespace Puck.Text;

/// <summary>Whole-job resource ceilings for managed font generation, independent of cache retention.</summary>
/// <remarks>Geometry counts retained points and segments, including composite copies and flattened boundaries.
/// Work counts font execution, boundary operations, kerning candidates and raster edge visits, not elapsed time.
/// These limits supplement the fixed per-glyph parser limits and the image limits in generation options.</remarks>
public sealed record FontAtlasGenerationLimits {
    /// <summary>Gets the maximum input font byte count. Defaults to 64 MiB.</summary>
    public int MaxFontBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Gets the maximum retained geometry element count across all glyphs. Defaults to one million.</summary>
    public int MaxGeometryElements { get; init; } = 1_000_000;
    /// <summary>Gets the maximum distinct mapped glyph count. Defaults to 65,535.</summary>
    public int MaxGlyphs { get; init; } = 65_535;
    /// <summary>Gets the maximum kerning entries processed or emitted, including zero-valued GPOS matches.
    /// Defaults to one million; Unicode alias expansion is charged as well.</summary>
    public int MaxKerningPairs { get; init; } = 1_000_000;
    /// <summary>Gets the total work-unit ceiling. Defaults to two billion.</summary>
    public long MaxWork { get; init; } = 2_000_000_000;
}

internal sealed class FontGenerationBudget {
    private long m_work;
    private int m_geometry;
    private int m_glyphs;
    private int m_pairs;
    private readonly FontAtlasGenerationLimits m_limits;
    public CancellationToken CancellationToken { get; }

    public FontGenerationBudget(FontAtlasGenerationLimits limits, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxFontBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxGeometryElements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxGlyphs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxKerningPairs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxWork);
        m_limits = limits;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
    }
    public void Work(long amount = 1) {
        CancellationToken.ThrowIfCancellationRequested();
        if (amount < 0 || amount > m_limits.MaxWork - m_work) { throw new InvalidDataException("Font generation exceeds the whole-job work limit."); }
        m_work += amount;
    }
    public void Geometry(int amount) {
        Work(amount);
        if (amount > m_limits.MaxGeometryElements - m_geometry) { throw new InvalidDataException("Font generation exceeds the whole-job geometry limit."); }
        m_geometry += amount;
    }
    public void Glyph() {
        Work();
        if (++m_glyphs > m_limits.MaxGlyphs) { throw new InvalidDataException("Font generation exceeds the whole-job glyph limit."); }
    }
    public void KerningPairs(int amount = 1) {
        Work(amount);
        if (amount > m_limits.MaxKerningPairs - m_pairs) { throw new InvalidDataException("Font generation exceeds the whole-job kerning-pair limit."); }
        m_pairs += amount;
    }
}
