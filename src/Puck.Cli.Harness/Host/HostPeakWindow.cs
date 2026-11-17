using System.Globalization;
using Puck.Maths;

namespace Puck.Cli.Host;

/// <summary>
/// The recent distribution of one start kind's observed peaks: the last <see cref="Capacity"/> distinct processes, each as
/// its private-byte peak and working-set peak, and the high percentile of them that <see cref="HostStartTracker"/> expects
/// the next start of the kind to reach. One process is one observation, keyed by its id and start ticks (a recycled id is
/// another process), so every CLI process that watches the same start contributes the same entry and a repeat changes
/// nothing. An outlier (one unusually long session) holds the estimate up only until <see cref="Capacity"/> newer starts
/// push it out of the window, and not at all once five observations exist, because the percentile then falls below the
/// largest; a sustained rise (a fifth of the window or more) is the estimate within that many starts. The estimate follows
/// the ordinary start in both directions, never below what most recent starts of the kind actually reached.
/// <para>The window persists as one text file per kind, a line per observation in order of recency
/// (<c>startTicks id privateGb workingSetGb</c>), merged under an exclusive guard by <see cref="Merge"/>: the file is the
/// truth and this process adds only what it observed since it last wrote.</para>
/// </summary>
internal sealed class HostPeakWindow {
    /// <summary>How many recent observations the window keeps.</summary>
    public const int Capacity = 10;
    // The nearest-rank percentile (OrderStatistics.NearestRank) at Numerator / Denominator: 8 of 10 (the fourth fifth), so a
    // single outlier is ignored from five observations on and three of ten are enough to be learned.
    private const int Numerator = 4;
    private const int Denominator = 5;

    private readonly List<(int Id, long StartTicks, double PrivateGb, double ResidentGb)> m_entries = [];
    private readonly HashSet<(int Id, long StartTicks)> m_dirty = [];

    /// <summary>Gets how many observations the window holds.</summary>
    public int Count => m_entries.Count;
    /// <summary>Gets the expected private-byte peak: the window's high percentile, or NaN when it is empty.</summary>
    public double PrivateGb => Percentile(resident: false);
    /// <summary>Gets the expected working-set peak: the window's high percentile, or NaN when it is empty.</summary>
    public double ResidentGb => Percentile(resident: true);

    private double Percentile(bool resident) {
        if (m_entries.Count == 0) { return double.NaN; }

        return OrderStatistics.NearestRank(ascending: m_entries.Select(selector: entry => (resident ? entry.ResidentGb : entry.PrivateGb)).Order().ToArray(), numerator: Numerator, denominator: Denominator);
    }
    private static void Truncate(List<(int Id, long StartTicks, double PrivateGb, double ResidentGb)> entries) {
        if (entries.Count > Capacity) { entries.RemoveRange(index: 0, count: (entries.Count - Capacity)); }
    }

    /// <summary>Gets whether the process is in the window.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="startTicks">The process's start ticks.</param>
    /// <returns><see langword="true"/> when the process has an observation.</returns>
    public bool Contains(int id, long startTicks) => m_entries.Exists(match: entry => ((entry.Id == id) && (entry.StartTicks == startTicks)));
    /// <summary>Records a process's peaks: a new process is the newest observation, a known one keeps the larger of each peak
    /// (a process's peak only rises).</summary>
    /// <param name="id">The process id.</param>
    /// <param name="startTicks">The process's start ticks.</param>
    /// <param name="privateGb">The process's private-byte peak, in gigabytes.</param>
    /// <param name="residentGb">The process's working-set peak, in gigabytes.</param>
    public void Observe(int id, long startTicks, double privateGb, double residentGb) {
        if (!double.IsFinite(privateGb) || !double.IsFinite(residentGb) || (privateGb <= 0) || (residentGb <= 0)) { return; }

        var at = m_entries.FindIndex(match: entry => ((entry.Id == id) && (entry.StartTicks == startTicks)));

        if (at < 0) {
            m_entries.Add(item: (id, startTicks, privateGb, residentGb));
            _ = m_dirty.Add(item: (id, startTicks));
            Truncate(entries: m_entries);
        } else {
            var known = m_entries[at];

            if ((privateGb > known.PrivateGb) || (residentGb > known.ResidentGb)) {
                m_entries[at] = (id, startTicks, Math.Max(privateGb, known.PrivateGb), Math.Max(residentGb, known.ResidentGb));
                _ = m_dirty.Add(item: (id, startTicks));
            }
        }
    }
    /// <summary>Replaces the window with what is stored, then applies this process's own observations since its last write
    /// (a new process goes last, a known one keeps the larger peaks), so a concurrent CLI's observations are kept and one
    /// this process had already seen pushed out is not brought back.</summary>
    /// <param name="stored">The stored text, or <see langword="null"/> when there is none. A line that does not parse is
    /// dropped.</param>
    /// <returns>The window's text to store, empty when it holds nothing.</returns>
    public string Merge(string? stored) {
        var merged = new List<(int Id, long StartTicks, double PrivateGb, double ResidentGb)>();

        foreach (var line in (stored ?? string.Empty).Split(separator: '\n', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
            var fields = line.Split(separator: ' ');

            if ((fields.Length == 4)
                && int.TryParse(provider: CultureInfo.InvariantCulture, s: fields[1], result: out var id)
                && long.TryParse(provider: CultureInfo.InvariantCulture, s: fields[0], result: out var ticks)
                && double.TryParse(provider: CultureInfo.InvariantCulture, s: fields[2], result: out var privateGb)
                && double.TryParse(provider: CultureInfo.InvariantCulture, s: fields[3], result: out var residentGb)
                && double.IsFinite(privateGb) && double.IsFinite(residentGb) && (privateGb > 0) && (residentGb > 0)
                && !merged.Exists(match: entry => ((entry.Id == id) && (entry.StartTicks == ticks)))) {
                merged.Add(item: (id, ticks, privateGb, residentGb));
            }
        }
        foreach (var own in m_entries.Where(predicate: entry => m_dirty.Contains(item: (entry.Id, entry.StartTicks)))) {
            var at = merged.FindIndex(match: entry => ((entry.Id == own.Id) && (entry.StartTicks == own.StartTicks)));

            if (at < 0) { merged.Add(item: own); }
            else { merged[at] = (own.Id, own.StartTicks, Math.Max(own.PrivateGb, merged[at].PrivateGb), Math.Max(own.ResidentGb, merged[at].ResidentGb)); }
        }

        Truncate(entries: merged);
        m_entries.Clear();
        m_entries.AddRange(collection: merged);

        return string.Concat(values: m_entries.Select(selector: static entry => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{entry.StartTicks} {entry.Id} {entry.PrivateGb:R} {entry.ResidentGb:R}\n")));
    }
    /// <summary>Marks every observation as stored, after <see cref="Merge"/>'s text has been written.</summary>
    public void Written() => m_dirty.Clear();
}
