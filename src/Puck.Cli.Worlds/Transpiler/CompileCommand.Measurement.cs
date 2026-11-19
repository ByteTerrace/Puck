using Puck.Transpiler.Lowering;
using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli.Transpiler;

public static partial class CompileCommand {
    // The measurement is process-wide state of one verb invocation: the compilers deep below Run report each source's
    // evaluation work to it (Note) without every layer between carrying it.
    private sealed class CompilationMeasurement : IDisposable {
        // The sources named when a run compiled several: the ones nearest the work limit.
        private const int LargestShown = 5;
        private static readonly Lock Gate = new();
        private static CompilationMeasurement? Active;

        private readonly long m_start = Stopwatch.GetTimestamp();
        private readonly long m_allocated = GC.GetTotalAllocatedBytes(precise: true);
        private readonly List<(string Path, long Work)> m_work = [];

        public CompilationMeasurement() {
            lock (Gate) { Active = this; }
        }

        /// <summary>Records the work units one source's compilation charged its evaluation budget; a no-op without --measure.</summary>
        public static void Note(string path, long work) {
            lock (Gate) { Active?.m_work.Add(item: (path, work)); }
        }

        public void Dispose() {
            lock (Gate) {
                if (ReferenceEquals(objA: Active, objB: this)) { Active = null; }
            }
            using var process = Process.GetCurrentProcess();
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"compile measure: elapsed-ms={Stopwatch.GetElapsedTime(m_start).TotalMilliseconds:0.0} peak-working-set-bytes={process.PeakWorkingSet64} managed-allocated-bytes={GC.GetTotalAllocatedBytes(precise: true) - m_allocated} managed-live-bytes={GC.GetTotalMemory(forceFullCollection: false)}"));
            foreach (var (path, work) in m_work.OrderByDescending(keySelector: static entry => entry.Work).Take(count: LargestShown)) {
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"compile measure: evaluation-work={work} limit={DocumentEvaluationBudget.WorkLimit} share={(100d * work / DocumentEvaluationBudget.WorkLimit):0.0}% source={CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: path))}"));
            }
        }
    }
}
