using Microsoft.Extensions.Logging.Abstractions;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Launcher;

namespace Puck.Demo;

/// <summary>
/// The genlock (latency phase-align) determinism gate (<c>--validate-genlock</c>): a pure-CPU simulation of the pacer's
/// deadline grid against a jittered external producer, proving the <see cref="GenlockPhaseAligner"/> control law
/// machine-independently — no window, no camera, no GPU. It simulates the design-target regime (a high render rate whose
/// presents follow the deadline, i.e. a VRR display) that a fixed-scanout desktop cannot exercise live: a 240 Hz
/// deadline grid free-runs against ~30 Hz arrivals with ±0.5 ms jitter, once with the aligner engaged and once without,
/// and the gate asserts that (a) engaged, the arrival→next-deadline latency converges to the small guard offset and
/// holds steady (its spread collapses versus the free-running beat sweep), and (b) every per-frame nudge stays inside
/// the aligner's declared VRR-safe bound (renderPeriod/16). 0 = pass, 2 = fail. It never presents.
/// </summary>
internal sealed class GenlockGateNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "genlock-gate",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;

    /// <summary>Initializes a new instance of the <see cref="GenlockGateNode"/> class.</summary>
    /// <param name="result">The shared result the exit code is written to.</param>
    public GenlockGateNode(ParityResult result) {
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        try {
            Validate();
            m_result.ExitCode = 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"GENLOCK fail | {exception.Message}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private static void Validate() {
        ValidateElection();
        ValidateConvergence();
    }

    // The registry's election rules — the host decides which rhythm the pacer follows; producers never do.
    private static void ValidateElection() {
        // AUTO: a sole source is elected; the moment a second registers, forwarding stops (no arbitrary winner).
        var auto = new ExternalClockRegistry();
        var first = auto.RegisterSource(sourceId: "camera:0");

        first.Publish(arrivalTimestamp: 100L, frameVersion: 1L);

        if (!auto.PacerClock.TryRead(arrivalTimestamp: out var autoArrival, frameVersion: out _) || (100L != autoArrival)) {
            throw new InvalidOperationException(message: "auto election did not forward the sole source's arrival to the pacer");
        }

        var second = auto.RegisterSource(sourceId: "camera:1");

        second.Publish(arrivalTimestamp: 200L, frameVersion: 1L);
        first.Publish(arrivalTimestamp: 300L, frameVersion: 2L);

        _ = auto.PacerClock.TryRead(arrivalTimestamp: out autoArrival, frameVersion: out _);

        if (100L != autoArrival) {
            throw new InvalidOperationException(message: $"plural sources with no election must stop forwarding (pacer read {autoArrival}, expected the pre-plurality 100)");
        }

        // NAMED: exactly the elected source forwards; the other's publishes stay in its own channel.
        var named = new ExternalClockRegistry(electionPolicy: "camera:1");
        var bystander = named.RegisterSource(sourceId: "camera:0");
        var elected = named.RegisterSource(sourceId: "camera:1");

        bystander.Publish(arrivalTimestamp: 50L, frameVersion: 1L);

        if (named.PacerClock.TryRead(arrivalTimestamp: out _, frameVersion: out _)) {
            throw new InvalidOperationException(message: "an unelected source's publish reached the pacer under a named election");
        }

        elected.Publish(arrivalTimestamp: 60L, frameVersion: 1L);

        if (!named.PacerClock.TryRead(arrivalTimestamp: out var namedArrival, frameVersion: out _) || (60L != namedArrival)) {
            throw new InvalidOperationException(message: "the named source's publish did not reach the pacer");
        }

        // OFF: nothing ever forwards. And registration is idempotent (a replugged device keeps its channel).
        var off = new ExternalClockRegistry(electionPolicy: ExternalClockRegistry.PolicyOff);
        var muted = off.RegisterSource(sourceId: "camera:0");

        muted.Publish(arrivalTimestamp: 70L, frameVersion: 1L);

        if (off.PacerClock.TryRead(arrivalTimestamp: out _, frameVersion: out _)) {
            throw new InvalidOperationException(message: "a publish reached the pacer under genlock \"off\"");
        }

        if (!ReferenceEquals(muted, off.RegisterSource(sourceId: "camera:0"))) {
            throw new InvalidOperationException(message: "re-registering a source id must return the same channel");
        }
    }

    private static void ValidateConvergence() {
        const long Frequency = 10_000_000L; // simulated Stopwatch frequency: 10 MHz (0.1 µs ticks)

        var renderPeriod = (Frequency / 240L);          // the design-target high render rate
        var cameraPeriod = ((Frequency * 100L) / 2990L); // ~29.9 Hz — deliberately off-beat from the grid
        var guard = Math.Min(Frequency / 500L, renderPeriod / 4L); // the aligner's own desired phase

        var (lockedMean, lockedSpread, maxNudgeSeen) = Simulate(aligned: true, cameraPeriod: cameraPeriod, frequency: Frequency, renderPeriod: renderPeriod);
        var (freeMean, freeSpread, _) = Simulate(aligned: false, cameraPeriod: cameraPeriod, frequency: Frequency, renderPeriod: renderPeriod);

        static double Milliseconds(double ticks) => ((ticks * 1000.0) / Frequency);

        // (a) Engaged, the arrival→next-deadline latency settles at the guard offset and holds. The locked spread's
        // floor is the arrival jitter itself (σ of uniform ±JitterRange = range/√3) — the controller tracks the beat
        // away, it cannot (and must not chase) per-frame sensor jitter — so assert against twice that floor, and that
        // the free-running beat sweep is well above it.
        var jitterFloor = ((Frequency / 2000.0) / Math.Sqrt(d: 3.0));

        if (lockedSpread >= (jitterFloor * 2.0)) {
            throw new InvalidOperationException(message: $"no lock: aligned arrival→deadline spread {Milliseconds(lockedSpread):0.000} ms exceeds twice the jitter floor {Milliseconds(jitterFloor):0.000} ms (free-running {Milliseconds(freeSpread):0.000} ms)");
        }

        if (freeSpread < (jitterFloor * 3.0)) {
            throw new InvalidOperationException(message: $"weak control case: the free-running spread {Milliseconds(freeSpread):0.000} ms is not meaningfully above the jitter floor {Milliseconds(jitterFloor):0.000} ms — the beat did not sweep");
        }

        if (Math.Abs(value: (lockedMean - guard)) > (renderPeriod / 8.0)) {
            throw new InvalidOperationException(message: $"wrong phase: aligned arrival→deadline mean {Milliseconds(lockedMean):0.000} ms is not at the guard offset {Milliseconds(guard):0.000} ms");
        }

        // (b) Every per-frame nudge stayed inside the declared VRR-safe bound.
        if (maxNudgeSeen > (renderPeriod / 16L)) {
            throw new InvalidOperationException(message: $"nudge bound exceeded: {Milliseconds(maxNudgeSeen):0.000} ms > renderPeriod/16 {Milliseconds(renderPeriod / 16.0):0.000} ms");
        }

        Console.Out.WriteLine(value: $"GENLOCK pass | 240 Hz grid vs ~29.9 Hz jittered arrivals | aligned mean {Milliseconds(lockedMean):0.00} ms (guard {Milliseconds(guard):0.00} ms) spread {Milliseconds(lockedSpread):0.000} ms vs free-running spread {Milliseconds(freeSpread):0.000} ms | max nudge {Milliseconds(maxNudgeSeen):0.000} ms within renderPeriod/16");
    }

    // Simulates the pacer loop in the VRR (present-follows-deadline) regime: the deadline grid accumulates by
    // renderPeriod (biased by the aligner when engaged) while jittered camera arrivals publish into the clock exactly as
    // CameraChildNode does. For every arrival past warm-up, records the latency from the arrival to the first deadline
    // at-or-after it — the quantity latency phase-align exists to minimize and stabilize. Deterministic (fixed seed).
    private static (double Mean, double Spread, long MaxNudge) Simulate(bool aligned, long cameraPeriod, long frequency, long renderPeriod) {
        const int ArrivalCount = 600;
        const int WarmupArrivals = 200;

        var clock = new ExternalPresentClock();
        var aligner = new GenlockPhaseAligner(clock: clock, logger: NullLogger.Instance, logPhase: false);
        var jitter = new Random(Seed: 20260701);
        var jitterRange = (frequency / 2000L); // ±0.5 ms of arrival jitter (decode/copy wobble)

        var arrivalIndex = 0L;
        var deadline = 0L;
        var latencies = new List<long>();
        var maxNudge = 0L;
        var nextArrival = (cameraPeriod / 3L); // an arbitrary initial phase

        while (arrivalIndex < ArrivalCount) {
            var previousDeadline = deadline;
            var accumulated = (deadline + renderPeriod);

            deadline = (aligned ? aligner.Apply(deadline: accumulated, frequency: frequency, renderPeriod: renderPeriod) : accumulated);
            maxNudge = Math.Max(maxNudge, Math.Abs(value: (deadline - accumulated)));

            // Deliver every arrival that lands in (previousDeadline, deadline]: publish it (timestamp + version, as the
            // camera pane forwards) and record its latency to this deadline — the first grid line at-or-after it.
            while (nextArrival <= deadline) {
                if (nextArrival > previousDeadline) {
                    clock.Publish(arrivalTimestamp: nextArrival, frameVersion: ++arrivalIndex);

                    if (arrivalIndex > WarmupArrivals) {
                        latencies.Add(item: (deadline - nextArrival));
                    }
                }

                nextArrival += (cameraPeriod + jitter.NextInt64(minValue: -jitterRange, maxValue: jitterRange));
            }
        }

        var mean = latencies.Average();
        var spread = Math.Sqrt(d: latencies.Average(selector: latency => Math.Pow(x: (latency - mean), y: 2.0)));

        return (mean, spread, maxNudge);
    }
}
