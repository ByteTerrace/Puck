using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions;

namespace Puck.Cli.Bench;

/// <summary>One host reading a measurement records beside its numbers: the machine's load while it ran.</summary>
/// <param name="CpuPercent">The machine's CPU use over the interval since the previous reading, or NaN when unread.</param>
/// <param name="FreeRamGb">The free physical memory in gigabytes.</param>
/// <param name="GpuHolder">The process holding the GPU, or <see langword="null"/> when it is idle.</param>
/// <param name="HeavyTestHolder">The heavy test run another process is running, or <see langword="null"/>.</param>
public readonly record struct BenchHostReading(double CpuPercent, double FreeRamGb, string? GpuHolder, string? HeavyTestHolder);

/// <summary>Takes one host reading; the CLI root answers it with the host-load probe the gate and leases use.</summary>
/// <returns>The reading.</returns>
public delegate BenchHostReading BenchHostSample();

/// <summary>The build a measurement ran: what a before/after comparison reads to know which code each side measured.</summary>
/// <param name="Path">The entry assembly whose directory supplied the measured engine assemblies.</param>
/// <param name="Version">The build's version with the commit it was built at as build metadata, when it recorded one.</param>
/// <param name="Sha256">The SHA-256 over the names and bytes of the engine assemblies (<c>Puck.*.dll</c>, the CLI's own
/// excluded) in that directory, in ordinal name order.</param>
public sealed record BenchBuild(string Path, string Version, string Sha256) {
    /// <summary>Reads a build's identity from its entry assembly.</summary>
    /// <param name="entryAssembly">The entry assembly, such as <c>Puck.World.dll</c> or <c>Puck.Cli.dll</c>, whose
    /// runtime configuration carries the source revision.</param>
    /// <returns>The identity.</returns>
    public static BenchBuild Of(string entryAssembly) {
        var directory = System.IO.Path.GetDirectoryName(path: System.IO.Path.GetFullPath(path: entryAssembly))!;
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        foreach (var file in EngineAssemblies(directory: directory)) {
            hash.AppendData(data: Encoding.UTF8.GetBytes(s: (System.IO.Path.GetFileName(path: file) + "\n")));
            hash.AppendData(data: File.ReadAllBytes(path: file));
        }

        var version = (FileVersionInfo.GetVersionInfo(fileName: entryAssembly).ProductVersion ?? "0.0.0");

        return new BenchBuild(
            Path: PuckPaths.Normalize(path: System.IO.Path.GetFullPath(path: entryAssembly)),
            Sha256: Convert.ToHexStringLower(inArray: hash.GetHashAndReset()),
            Version: CliRevision.Describe(revision: Revision(entryAssembly: entryAssembly), version: version)
        );
    }
    /// <summary>Lists the engine assemblies of a build directory: <c>Puck.*.dll</c> without the CLI's own.</summary>
    /// <param name="directory">The build directory.</param>
    /// <returns>Their full paths, in ordinal name order.</returns>
    public static IEnumerable<string> EngineAssemblies(string directory) => Directory.EnumerateFiles(path: directory, searchPattern: "Puck.*.dll")
        .Where(predicate: static file => !System.IO.Path.GetFileName(path: file).StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.Cli"))
        .Order(comparer: StringComparer.Ordinal);
    // The commit a Puck executable was built at lives in its runtime configuration (Directory.Build.targets,
    // PuckStampSourceRevision), the same value WorldSchema.SourceRevision reads in process.
    private static string? Revision(string entryAssembly) {
        try {
            var configuration = System.IO.Path.ChangeExtension(path: entryAssembly, extension: ".runtimeconfig.json");

            return (File.Exists(path: configuration)
                ? JsonNode.Parse(json: File.ReadAllText(path: configuration))?["runtimeOptions"]?["configProperties"]?["Puck.SourceRevision"]?.GetValue<string>()
                : null);
        } catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) {
            return null;
        }
    }
}

/// <summary>One measured quantity over a run's repeats.</summary>
/// <param name="Unit">The unit of every sample, such as <c>ms</c> or <c>bytes</c>.</param>
/// <param name="Median">The median sample.</param>
/// <param name="P10">The 10th percentile, interpolated between neighbouring samples.</param>
/// <param name="P90">The 90th percentile, interpolated between neighbouring samples.</param>
/// <param name="Samples">Every sample, in the order taken.</param>
public sealed record BenchMetric(string Unit, double Median, double P10, double P90, double[] Samples) {
    /// <summary>Summarizes samples of one quantity.</summary>
    /// <param name="unit">The samples' unit.</param>
    /// <param name="samples">The samples; at least one, all finite.</param>
    /// <returns>The metric.</returns>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is empty or holds a value that is not finite.</exception>
    public static BenchMetric Of(string unit, IReadOnlyList<double> samples) {
        if ((samples.Count == 0) || samples.Any(predicate: static sample => !double.IsFinite(d: sample))) {
            throw new ArgumentException(message: "A metric needs at least one finite sample.", paramName: nameof(samples));
        }

        var ordered = samples.Order().ToArray();

        return new BenchMetric(
            Median: Quantile(fraction: 0.5, ordered: ordered),
            P10: Quantile(fraction: 0.1, ordered: ordered),
            P90: Quantile(fraction: 0.9, ordered: ordered),
            Samples: [.. samples],
            Unit: unit
        );
    }
    /// <summary>Returns a quantile of ascending samples, interpolated linearly between the neighbouring samples: the one
    /// quantile every measurement verb reports.</summary>
    /// <param name="ordered">The samples in ascending order; at least one.</param>
    /// <param name="fraction">The quantile, from 0 to 1.</param>
    /// <returns>The quantile.</returns>
    public static double Quantile(IReadOnlyList<double> ordered, double fraction) {
        var position = ((ordered.Count - 1) * fraction);
        var lower = (int)Math.Floor(d: position);
        var upper = (int)Math.Ceiling(a: position);

        return (ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower)));
    }
}

/// <summary>The machine's load across a measurement, from readings taken before, during and after it.</summary>
/// <param name="Readings">How many readings were taken.</param>
/// <param name="CpuMeanPercent">The mean CPU use over the readings that measured it, or NaN when none did.</param>
/// <param name="CpuMaxPercent">The highest CPU use read, or NaN when none was.</param>
/// <param name="FreeRamMinGb">The lowest free physical memory read, in gigabytes.</param>
/// <param name="GpuHolders">Every process seen holding the GPU.</param>
/// <param name="HeavyTestHolders">Every other heavy test run seen.</param>
public sealed record BenchHostLoad(int Readings, double CpuMeanPercent, double CpuMaxPercent, double FreeRamMinGb, string[] GpuHolders, string[] HeavyTestHolders);

/// <summary>The one report every <c>puck bench</c> measurement lane writes (<c>report.json</c>) and prints with
/// <c>--json</c>: what was measured, on which build, how often, the machine's load meanwhile, and each metric's median,
/// 10th and 90th percentiles over the repeats. <c>puck measure</c> reads a metric as <c>/metrics/&lt;name&gt;/median</c>.</summary>
/// <param name="Schema">The format, <see cref="SchemaName"/>.</param>
/// <param name="Verb">The lane, such as <c>bench world</c>.</param>
/// <param name="Runtime">The .NET runtime that ran it.</param>
/// <param name="OperatingSystem">The operating system that ran it.</param>
/// <param name="Build">The measured build.</param>
/// <param name="Repeats">How many repeats each metric's samples come from.</param>
/// <param name="Settings">The lane's settings, as text.</param>
/// <param name="HostLoad">The machine's load while it ran, or <see langword="null"/> when no sampler was installed.</param>
/// <param name="Complete">Whether every repeat finished; an interrupted or failed run keeps what it measured.</param>
/// <param name="Metrics">Each metric by name.</param>
/// <param name="Rows">The lane's printed table from its last repeat, row name to text.</param>
/// <param name="Startup">The startup lane's per-process samples, or <see langword="null"/> for another lane.</param>
public sealed record BenchReport(
    string Schema,
    string Verb,
    string Runtime,
    string OperatingSystem,
    BenchBuild Build,
    int Repeats,
    IReadOnlyDictionary<string, string> Settings,
    BenchHostLoad? HostLoad,
    bool Complete,
    IReadOnlyDictionary<string, BenchMetric> Metrics,
    IReadOnlyList<KeyValuePair<string, string>>? Rows = null,
    IReadOnlyList<StartupSample>? Startup = null
) {
    /// <summary>The report format's name.</summary>
    public const string SchemaName = "puck.bench.v1";

    /// <summary>Starts a report for a lane on the current runtime.</summary>
    /// <param name="verb">The lane.</param>
    /// <param name="build">The measured build.</param>
    /// <param name="repeats">The repeat count.</param>
    /// <param name="settings">The lane's settings.</param>
    /// <returns>An incomplete report with no metrics.</returns>
    public static BenchReport Start(string verb, BenchBuild build, int repeats, IReadOnlyDictionary<string, string> settings) => new(
        Build: build,
        Complete: false,
        HostLoad: null,
        Metrics: new Dictionary<string, BenchMetric>(),
        OperatingSystem: RuntimeInformation.OSDescription,
        Repeats: repeats,
        Runtime: RuntimeInformation.FrameworkDescription,
        Schema: SchemaName,
        Settings: settings,
        Verb: verb
    );
    /// <summary>Serializes the report as indented JSON.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(value: this, jsonTypeInfo: BenchJsonContext.Default.BenchReport);
    /// <summary>Writes the report as <c>report.json</c>, replacing it whole.</summary>
    /// <param name="directory">The run directory.</param>
    /// <returns>The report's path.</returns>
    public string Write(string directory) {
        var path = System.IO.Path.Combine(path1: directory, path2: "report.json");

        Puck.Abstractions.AtomicFile.WriteAllText(path: path, contents: (ToJson() + "\n"));
        return path;
    }
    /// <summary>Formats each metric as one line: name, median, p10 and p90 with its unit.</summary>
    /// <returns>The lines.</returns>
    public IEnumerable<string> MetricLines() => Metrics.Select(selector: static entry => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"{entry.Key}: median {entry.Value.Median:G6} {entry.Value.Unit} (p10 {entry.Value.P10:G6}, p90 {entry.Value.P90:G6}, {entry.Value.Samples.Length} samples)"));
}

/// <summary>Samples the host on a background timer while a measurement runs, and folds the readings into one
/// <see cref="BenchHostLoad"/>. A reading enumerates the machine's processes, so the interval stays coarse.</summary>
public sealed class BenchHostRecorder : IDisposable {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 5);

    private readonly BenchHostSample? m_sample;
    private readonly List<BenchHostReading> m_readings = [];
    private readonly Timer? m_timer;
    private readonly Lock m_lock = new();

    /// <summary>Initializes a new instance of the <see cref="BenchHostRecorder"/> class and takes its first reading.</summary>
    /// <param name="sample">The host sampler, or <see langword="null"/> to record nothing.</param>
    public BenchHostRecorder(BenchHostSample? sample) {
        m_sample = sample;
        if (sample is null) { return; }
        Take();
        m_timer = new Timer(callback: _ => Take(), dueTime: Interval, period: Interval, state: null);
    }

    private void Take() {
        try {
            var reading = m_sample!();

            lock (m_lock) { m_readings.Add(item: reading); }
        } catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
            Console.Error.WriteLine(value: $"bench: host reading failed: {error.Message}");
        }
    }
    /// <summary>Takes a closing reading and folds every reading so far.</summary>
    /// <returns>The load, or <see langword="null"/> when no sampler was installed.</returns>
    public BenchHostLoad? Finish() {
        if (m_sample is null) { return null; }
        Take();
        BenchHostReading[] readings;

        lock (m_lock) { readings = [.. m_readings]; }
        var cpu = readings.Select(selector: static reading => reading.CpuPercent).Where(predicate: double.IsFinite).ToArray();

        return new BenchHostLoad(
            CpuMaxPercent: ((cpu.Length == 0) ? double.NaN : cpu.Max()),
            CpuMeanPercent: ((cpu.Length == 0) ? double.NaN : cpu.Average()),
            FreeRamMinGb: ((readings.Length == 0) ? double.NaN : readings.Min(selector: static reading => reading.FreeRamGb)),
            GpuHolders: [.. readings.Select(selector: static reading => reading.GpuHolder).OfType<string>().Distinct(comparer: StringComparer.Ordinal)],
            HeavyTestHolders: [.. readings.Select(selector: static reading => reading.HeavyTestHolder).OfType<string>().Distinct(comparer: StringComparer.Ordinal)],
            Readings: readings.Length
        );
    }
    /// <summary>Stops the timer.</summary>
    public void Dispose() => m_timer?.Dispose();
}

[JsonSerializable(typeof(BenchReport))]
[JsonSourceGenerationOptions(NewLine = "\n", NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class BenchJsonContext : JsonSerializerContext;
