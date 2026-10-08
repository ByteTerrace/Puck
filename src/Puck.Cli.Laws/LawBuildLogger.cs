using System.Globalization;
using Microsoft.Build.Framework;

namespace Puck.Cli.Laws;

/// <summary>MSBuild's event logger for proof work counts. Compiler tasks count distinct projects; an up-to-date
/// CoreCompile counts only when that project never compiles in another target framework during the same build.</summary>
public sealed class LawBuildLogger : ILogger {
    private readonly HashSet<string> m_compiled = new(comparer: Puck.Abstractions.PuckPaths.Comparer);
    private readonly HashSet<string> m_upToDate = new(comparer: Puck.Abstractions.PuckPaths.Comparer);
    private readonly HashSet<BuildEventContext> m_targets = [];
    private readonly Lock m_sync = new();

    /// <inheritdoc/>
    public string? Parameters { get; set; }

    /// <inheritdoc/>
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

    /// <inheritdoc/>
    public void Initialize(IEventSource eventSource) {
        eventSource.AnyEventRaised += (_, args) => Observe(args: args);
        eventSource.BuildFinished += (_, _) => {
            var counts = Counts;

            File.WriteAllText(path: (Parameters ?? throw new LoggerException(message: "laws prove build logger requires its report path.")), contents: FormattableString.Invariant(formattable: $"{counts.Compiled}\n{counts.UpToDate}\n{counts.Targets}\n"));
        };
    }
    /// <inheritdoc/>
    public void Shutdown() { }

    public LawBuildCounts Counts {
        get {
            lock (m_sync) {
                return new LawBuildCounts(Compiled: m_compiled.Count, UpToDate: m_upToDate.Except(second: m_compiled, comparer: Puck.Abstractions.PuckPaths.Comparer).Count(), Targets: m_targets.Count);
            }
        }
    }

    public void Observe(BuildEventArgs args) {
        lock (m_sync) {
            switch (args) {
                case TargetStartedEventArgs started when (started.BuildEventContext is not null):
                    _ = m_targets.Add(item: started.BuildEventContext);
                    break;
                case TargetSkippedEventArgs skipped:
                    if (skipped.BuildEventContext is not null) { _ = m_targets.Remove(item: skipped.BuildEventContext); }
                    if ((skipped.TargetName == "CoreCompile") && (skipped.SkipReason == TargetSkipReason.OutputsUpToDate) && (skipped.ProjectFile is not null)) {
                        _ = m_upToDate.Add(item: skipped.ProjectFile);
                    }
                    break;
                case TaskStartedEventArgs task when (task.TaskName is "Csc" or "Fsc" or "Vbc"):
                    _ = m_compiled.Add(item: task.ProjectFile);
                    break;
            }
        }
    }

    internal static LawBuildCounts? Read(string path) {
        if (!File.Exists(path: path)) { return null; }
        var lines = File.ReadAllLines(path: path);

        if (lines.Length != 3) { return null; }
        var counts = new int[3];

        for (var index = 0; (index < counts.Length); ++index) {
            if (!int.TryParse(s: lines[index], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out counts[index])) { return null; }
        }
        return new LawBuildCounts(Compiled: counts[0], UpToDate: counts[1], Targets: counts[2]);
    }
}
