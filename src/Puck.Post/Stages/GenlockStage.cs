using Microsoft.Extensions.Logging.Abstractions;
using Puck.Abstractions;
using Puck.Launcher;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A5. The genlock (latency phase-align) self-test, the POST port of the demo's
/// <c>--validate-genlock</c> gate: a pure-CPU simulation of the pacer's deadline grid against a jittered external
/// producer, proving the <see cref="GenlockPhaseAligner"/> control law machine-independently — no window, no camera,
/// no GPU. It first proves the <see cref="ExternalClockRegistry"/> election rules (auto / named / off; idempotent
/// re-registration), then simulates the design-target VRR regime — a 240 Hz deadline grid free-running against
/// ~29.9 Hz arrivals with ±0.5 ms jitter, once aligned and once free-running — and asserts the aligned
/// arrival→next-deadline latency converges to the guard offset with a collapsed spread while every per-frame nudge
/// stays inside the declared VRR-safe bound (renderPeriod/16).
/// </summary>
internal sealed class GenlockStage : IPostStage {
    private const long Frequency = 10_000_000L; // simulated Stopwatch frequency: 10 MHz (0.1 µs ticks)

    /// <inheritdoc/>
    public string Name => "genlock";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // The aligner honors the PUCK_GENLOCK=0 kill switch; under it the control law cannot engage, so the honest
        // verdict is an environment skip, not a confusing "no lock" failure.
        if (string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_GENLOCK"), "0", comparisonType: StringComparison.Ordinal)) {
            return PostStageOutcome.Skip(detail: "PUCK_GENLOCK=0 disables the phase aligner; the control law cannot be exercised");
        }

        try {
            ValidateElection();

            return PostStageOutcome.Pass(detail: ValidateConvergence());
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
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

    private static string ValidateConvergence() {
        var renderPeriod = (Frequency / 240L);           // the design-target high render rate
        var cameraPeriod = ((Frequency * 100L) / 2990L); // ~29.9 Hz — deliberately off-beat from the grid
        var guard = Math.Min(Frequency / 500L, renderPeriod / 4L); // the aligner's own desired phase

        var (lockedMean, lockedSpread, maxNudgeSeen) = Simulate(aligned: true, cameraPeriod: cameraPeriod, renderPeriod: renderPeriod);
        var (freeMean, freeSpread, _) = Simulate(aligned: false, cameraPeriod: cameraPeriod, renderPeriod: renderPeriod);

        _ = freeMean;

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

        return $"election rules hold | 240 Hz grid vs ~29.9 Hz jittered arrivals: aligned mean {Milliseconds(lockedMean):0.00} ms (guard {Milliseconds(guard):0.00} ms) spread {Milliseconds(lockedSpread):0.000} ms vs free-running {Milliseconds(freeSpread):0.000} ms | max nudge {Milliseconds(maxNudgeSeen):0.000} ms within renderPeriod/16";
    }

    // Simulates the pacer loop in the VRR (present-follows-deadline) regime: the deadline grid accumulates by
    // renderPeriod (biased by the aligner when engaged) while jittered camera arrivals publish into the clock exactly
    // as the camera pane does. For every arrival past warm-up, records the latency from the arrival to the first
    // deadline at-or-after it — the quantity latency phase-align exists to minimize and stabilize. Deterministic
    // (fixed seed).
    private static (double Mean, double Spread, long MaxNudge) Simulate(bool aligned, long cameraPeriod, long renderPeriod) {
        const int ArrivalCount = 600;
        const int WarmupArrivals = 200;

        var clock = new ExternalPresentClock();
        var aligner = new GenlockPhaseAligner(clock: clock, logger: NullLogger.Instance, logPhase: false);
        var jitter = new Random(Seed: 20260701);
        var jitterRange = (Frequency / 2000L); // ±0.5 ms of arrival jitter (decode/copy wobble)

        var arrivalIndex = 0L;
        var deadline = 0L;
        var latencies = new List<long>();
        var maxNudge = 0L;
        var nextArrival = (cameraPeriod / 3L); // an arbitrary initial phase

        while (arrivalIndex < ArrivalCount) {
            var previousDeadline = deadline;
            var accumulated = (deadline + renderPeriod);

            deadline = (aligned ? aligner.Apply(deadline: accumulated, frequency: Frequency, renderPeriod: renderPeriod) : accumulated);
            maxNudge = Math.Max(maxNudge, Math.Abs(value: (deadline - accumulated)));

            // Deliver every arrival that lands in (previousDeadline, deadline]: publish it (timestamp + version, as
            // the camera pane forwards) and record its latency to this deadline — the first grid line at-or-after it.
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
