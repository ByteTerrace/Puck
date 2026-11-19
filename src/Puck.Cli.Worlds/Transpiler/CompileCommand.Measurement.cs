using Puck.Abstractions.Counting;
using Puck.Assets;
using Puck.Transpiler.Lowering;
using Puck.World;
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
        // The compile session's counted work: sources compiled and held compiles served, the .puck files parsed (a root
        // and every library the session's parses did not hold), documents a composition read, merges and the ones a
        // held image answered, and whole-document validations.
        private static readonly WorkKind[] SessionWork = [
            WorldBootWork.Compiles,
            WorldBootWork.PuckCacheHits,
            WorldBootWork.PuckParses,
            WorldBootWork.DocumentsRead,
            WorldBootWork.Compositions,
            WorldBootWork.CompositionsShared,
            WorldBootWork.Validations,
        ];
        private static CompilationMeasurement? Active;

        private readonly long m_start = Stopwatch.GetTimestamp();
        private readonly long m_allocated = GC.GetTotalAllocatedBytes(precise: true);
        private readonly long m_contentReads = CompileInputs.ContentReads;
        private readonly long m_stampReads = CompileInputs.StampReads;
        private readonly long[] m_sessionWork = [.. SessionWork.Select(selector: static kind => WorldBootWork.Process.Read(kind: kind))];
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
            // The file reads the compile's door made: contents read and directories listed, the stats that vouched for
            // an answer without one, and the distinct files and directories those answers cover.
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"compile measure: file-content-reads={CompileInputs.ContentReads - m_contentReads} file-stamp-reads={CompileInputs.StampReads - m_stampReads} files-known={CompileInputs.RememberedPaths}"));
            Console.Error.WriteLine(value: ("compile measure: " + string.Join(separator: ' ', values: SessionWork.Select(selector: (kind, index) => string.Create(CultureInfo.InvariantCulture,
                $"{kind.Name["world.boot.".Length..]}={WorldBootWork.Process.Read(kind: kind) - m_sessionWork[index]}")))));
            foreach (var (path, work) in m_work.OrderByDescending(keySelector: static entry => entry.Work).Take(count: LargestShown)) {
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"compile measure: evaluation-work={work} limit={DocumentEvaluationBudget.WorkLimit} share={(100d * work / DocumentEvaluationBudget.WorkLimit):0.0}% source={CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: path))}"));
            }
        }
    }
}
