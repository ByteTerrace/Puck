using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli.Transpiler;

public static partial class CompileCommand {
    private sealed class CompilationMeasurement : IDisposable {
        private readonly long m_start = Stopwatch.GetTimestamp();
        private readonly long m_allocated = GC.GetTotalAllocatedBytes(precise: true);

        public void Dispose() {
            using var process = Process.GetCurrentProcess();
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"compile measure: elapsed-ms={Stopwatch.GetElapsedTime(m_start).TotalMilliseconds:0.0} peak-working-set-bytes={process.PeakWorkingSet64} managed-allocated-bytes={GC.GetTotalAllocatedBytes(precise: true) - m_allocated} managed-live-bytes={GC.GetTotalMemory(forceFullCollection: false)}"));
        }
    }
}
