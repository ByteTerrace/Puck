using System.Globalization;
using Puck.Maths;

namespace Puck.Cli.Host;

/// <summary>A process's peaks, observed or expected, in gigabytes: private bytes (the commit it charged), private working set
/// (the residency it could not give back without paging) and whole working set (that plus the clean shareable pages of
/// mapped files and images).</summary>
/// <param name="PrivateGb">The private-byte peak.</param>
/// <param name="ResidentGb">The private working-set peak.</param>
/// <param name="WorkingSetGb">The whole working-set peak; NaN for an expectation before any observation.</param>
public readonly record struct HostPeaks(double PrivateGb, double ResidentGb, double WorkingSetGb) {
    /// <summary>Gets whether every peak is a positive finite amount.</summary>
    public bool Valid => (Positive(value: PrivateGb) && Positive(value: ResidentGb) && Positive(value: WorkingSetGb));
    private static bool Positive(double value) => (double.IsFinite(d: value) && (value > 0));
    /// <summary>Returns each peak's larger value of this and <paramref name="other"/> (a process's peaks only rise).</summary>
    /// <param name="other">The other peaks.</param>
    /// <returns>The larger peaks.</returns>
    public HostPeaks Max(HostPeaks other) => new(Math.Max(PrivateGb, other.PrivateGb), Math.Max(ResidentGb, other.ResidentGb), Math.Max(WorkingSetGb, other.WorkingSetGb));
    /// <summary>Gets whether any peak of this exceeds the same peak of <paramref name="other"/>.</summary>
    /// <param name="other">The other peaks.</param>
    /// <returns><see langword="true"/> when this raises one of them.</returns>
    public bool Raises(HostPeaks other) => ((PrivateGb > other.PrivateGb) || (ResidentGb > other.ResidentGb) || (WorkingSetGb > other.WorkingSetGb));
}

/// <summary>
/// The recent distribution of one start kind's observed peaks: the last <see cref="Capacity"/> distinct processes, each as
/// its <see cref="HostPeaks"/>, and the high percentile of each quantity that <see cref="HostStartTracker"/> expects the next
/// start of the kind to reach. One process is one observation, keyed by its id and start ticks (a recycled id is another
/// process), so every CLI process that watches the same start contributes the same entry and a repeat changes nothing. An
/// outlier (one unusually long session) holds the estimate up only until <see cref="Capacity"/> newer starts push it out of
/// the window, and not at all once five observations exist, because the percentile then falls below the largest; a
/// sustained rise (a fifth of the window or more) is the estimate within that many starts. The estimate follows the
/// ordinary start in both directions, never below what most recent starts of the kind actually reached.
/// <para>The window persists as one text file per kind, a line per observation in order of recency
/// (<c>startTicks id privateGb privateResidentGb workingSetGb</c>), merged under an exclusive guard by <see cref="Merge"/>:
/// the file is the truth and this process adds only what it observed since it last wrote.</para>
/// </summary>
internal sealed class HostPeakWindow {
    /// <summary>How many recent observations the window keeps.</summary>
    public const int Capacity = 10;
    // The nearest-rank percentile (OrderStatistics.NearestRank) at Numerator / Denominator: 8 of 10 (the fourth fifth), so a
    // single outlier is ignored from five observations on and three of ten are enough to be learned.
    private const int Numerator = 4;
    private const int Denominator = 5;

    private readonly List<(int Id, long StartTicks, HostPeaks Peaks)> m_entries = [];
    private readonly HashSet<(int Id, long StartTicks)> m_dirty = [];

    /// <summary>Gets how many observations the window holds.</summary>
    public int Count => m_entries.Count;
    /// <summary>Gets the expected private-byte peak: the window's high percentile, or NaN when it is empty.</summary>
    public double PrivateGb => Percentile(quantity: static peaks => peaks.PrivateGb);
    /// <summary>Gets the expected private working-set peak: the window's high percentile, or NaN when it is empty.</summary>
    public double ResidentGb => Percentile(quantity: static peaks => peaks.ResidentGb);
    /// <summary>Gets the expected whole working-set peak: the window's high percentile, or NaN when it is empty.</summary>
    public double WorkingSetGb => Percentile(quantity: static peaks => peaks.WorkingSetGb);

    private double Percentile(Func<HostPeaks, double> quantity) {
        if (m_entries.Count == 0) { return double.NaN; }

        return OrderStatistics.NearestRank(ascending: m_entries.Select(selector: entry => quantity(arg: entry.Peaks)).Order().ToArray(), numerator: Numerator, denominator: Denominator);
    }
    private static void Truncate(List<(int Id, long StartTicks, HostPeaks Peaks)> entries) {
        if (entries.Count > Capacity) { entries.RemoveRange(index: 0, count: (entries.Count - Capacity)); }
    }

    /// <summary>Gets whether the process is in the window.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="startTicks">The process's start ticks.</param>
    /// <returns><see langword="true"/> when the process has an observation.</returns>
    public bool Contains(int id, long startTicks) => m_entries.Exists(match: entry => ((entry.Id == id) && (entry.StartTicks == startTicks)));
    /// <summary>Records a process's peaks: a new process is the newest observation, a known one keeps the larger of each peak
    /// (a process's peak only rises). Peaks that are not all positive and finite are ignored.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="startTicks">The process's start ticks.</param>
    /// <param name="peaks">The process's peaks.</param>
    public void Observe(int id, long startTicks, HostPeaks peaks) {
        if (!peaks.Valid) { return; }

        var at = m_entries.FindIndex(match: entry => ((entry.Id == id) && (entry.StartTicks == startTicks)));

        if (at < 0) {
            m_entries.Add(item: (id, startTicks, peaks));
            _ = m_dirty.Add(item: (id, startTicks));
            Truncate(entries: m_entries);
        } else if (peaks.Raises(other: m_entries[at].Peaks)) {
            m_entries[at] = (id, startTicks, peaks.Max(other: m_entries[at].Peaks));
            _ = m_dirty.Add(item: (id, startTicks));
        }
    }
    /// <summary>Replaces the window with what is stored, then applies this process's own observations since its last write
    /// (a new process goes last, a known one keeps the larger peaks), so a concurrent CLI's observations are kept and one
    /// this process had already seen pushed out is not brought back.</summary>
    /// <param name="stored">The stored text, or <see langword="null"/> when there is none. A line that does not parse is
    /// dropped.</param>
    /// <returns>The window's text to store, empty when it holds nothing.</returns>
    public string Merge(string? stored) {
        var merged = new List<(int Id, long StartTicks, HostPeaks Peaks)>();

        foreach (var line in (stored ?? string.Empty).Split(separator: '\n', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
            var fields = line.Split(separator: ' ');

            if ((fields.Length == 5)
                && long.TryParse(provider: CultureInfo.InvariantCulture, s: fields[0], result: out var ticks)
                && int.TryParse(provider: CultureInfo.InvariantCulture, s: fields[1], result: out var id)
                && double.TryParse(provider: CultureInfo.InvariantCulture, s: fields[2], result: out var privateGb)
                && double.TryParse(provider: CultureInfo.InvariantCulture, s: fields[3], result: out var residentGb)
                && double.TryParse(provider: CultureInfo.InvariantCulture, s: fields[4], result: out var workingSetGb)
                && new HostPeaks(PrivateGb: privateGb, ResidentGb: residentGb, WorkingSetGb: workingSetGb) is { Valid: true } peaks
                && !merged.Exists(match: entry => ((entry.Id == id) && (entry.StartTicks == ticks)))) {
                merged.Add(item: (id, ticks, peaks));
            }
        }
        foreach (var own in m_entries.Where(predicate: entry => m_dirty.Contains(item: (entry.Id, entry.StartTicks)))) {
            var at = merged.FindIndex(match: entry => ((entry.Id == own.Id) && (entry.StartTicks == own.StartTicks)));

            if (at < 0) { merged.Add(item: own); }
            else { merged[at] = (own.Id, own.StartTicks, own.Peaks.Max(other: merged[at].Peaks)); }
        }

        Truncate(entries: merged);
        m_entries.Clear();
        m_entries.AddRange(collection: merged);

        return string.Concat(values: m_entries.Select(selector: static entry => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{entry.StartTicks} {entry.Id} {entry.Peaks.PrivateGb:R} {entry.Peaks.ResidentGb:R} {entry.Peaks.WorkingSetGb:R}\n")));
    }
    /// <summary>Marks every observation as stored, after <see cref="Merge"/>'s text has been written.</summary>
    public void Written() => m_dirty.Clear();
}
