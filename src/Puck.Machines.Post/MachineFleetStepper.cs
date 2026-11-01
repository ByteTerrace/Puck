using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Puck.Machines.Post;

/// <summary>Schedules prepared benchmark machines as parallel independent spans or serial frame-by-frame rounds.</summary>
public static class MachineFleetStepper {
    /// <summary>Selects the deterministic input stream consumed by each fleet member.</summary>
    public interface IStreams {
        /// <summary>Selects one machine's input stream.</summary>
        /// <param name="index">The machine's zero-based fleet index.</param>
        /// <param name="count">The number of machines.</param>
        /// <returns>The input stream index.</returns>
        static abstract int For(int index, int count);
    }
    /// <summary>Measures individual machine windows independently of the fleet's aggregate stopwatch.</summary>
    public interface ITiming {
        /// <summary>Begins one machine's measured window.</summary>
        /// <returns>The policy's starting timestamp.</returns>
        long Start();
        /// <summary>Completes one machine's measured window.</summary>
        /// <param name="index">The machine's zero-based fleet index.</param>
        /// <param name="start">The timestamp returned by <see cref="Start"/>.</param>
        void Finish(int index, long start);
    }
    /// <summary>Gives every machine stream zero.</summary>
    public readonly struct SharedStreams : IStreams {
        /// <inheritdoc/>
        public static int For(int index, int count) => 0;
    }
    /// <summary>Gives each machine its own stream except the final member, which mirrors stream zero.</summary>
    public readonly struct PairedStreams : IStreams {
        /// <inheritdoc/>
        public static int For(int index, int count) => ((index == (count - 1)) ? 0 : index);
    }
    /// <summary>Gives every machine its fleet index as its input stream.</summary>
    public readonly struct IndependentStreams : IStreams {
        /// <inheritdoc/>
        public static int For(int index, int count) => index;
    }
    /// <summary>Leaves the hot loop free of individual-machine timestamp reads.</summary>
    public readonly struct Untimed : ITiming {
        /// <inheritdoc/>
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        public long Start() => 0;
        /// <inheritdoc/>
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        public void Finish(int index, long start) { }
    }
    /// <summary>Accumulates stopwatch ticks separately for every machine.</summary>
    /// <param name="ticks">The zero-initialized per-machine totals, owned by the caller.</param>
    public readonly struct PerMachineTiming(long[] ticks) : ITiming {
        /// <inheritdoc/>
        public long Start() => Stopwatch.GetTimestamp();
        /// <inheritdoc/>
        public void Finish(int index, long start) => ticks[index] += (Stopwatch.GetTimestamp() - start);
    }

    /// <summary>Advances a prepared fleet and measures the aggregate scheduling span. Parallel timing windows cover
    /// one machine's whole span; serial windows cover one machine frame and accumulate across rounds.</summary>
    /// <typeparam name="TMachine">The prepared owning machine adapter.</typeparam>
    /// <typeparam name="TSnapshot">The machine's immutable snapshot type.</typeparam>
    /// <typeparam name="TStreams">The input stream selection policy.</typeparam>
    /// <typeparam name="TTiming">The individual-machine timing policy.</typeparam>
    /// <param name="machines">The prepared independent machines, retained by the caller.</param>
    /// <param name="frames">The frame count to run on every machine.</param>
    /// <param name="parallel">Whether every machine runs its whole span on a parallel worker.</param>
    /// <param name="timing">The per-machine measurement policy.</param>
    /// <returns>The aggregate elapsed span, excluding construction and snapshot capture.</returns>
    public static TimeSpan Run<TMachine, TSnapshot, TStreams, TTiming>(TMachine[] machines, int frames,
        bool parallel, TTiming timing)
        where TMachine : struct, IMachineBenchmarkSubject<TSnapshot>
        where TSnapshot : class
        where TStreams : struct, IStreams
        where TTiming : struct, ITiming {
        var count = machines.Length;
        var stopwatch = Stopwatch.StartNew();

        if (parallel) {
            Parallel.For(fromInclusive: 0, toExclusive: count, body: index => {
                var machine = machines[index];
                var stream = TStreams.For(count: count, index: index);
                var start = timing.Start();

                for (var frame = 0; (frame < frames); ++frame) {
                    machine.RunFrame(frame: frame, stream: stream);
                }
                timing.Finish(index: index, start: start);
            });
        } else {
            for (var frame = 0; (frame < frames); ++frame) {
                for (var index = 0; (index < count); ++index) {
                    var start = timing.Start();

                    machines[index].RunFrame(stream: TStreams.For(count: count, index: index), frame: frame);
                    timing.Finish(index: index, start: start);
                }
            }
        }
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
}
