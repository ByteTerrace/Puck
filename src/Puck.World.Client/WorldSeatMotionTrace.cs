using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Puck.World.Client;

/// <summary>
/// The last presented frames of one local seat, recorded by <see cref="WorldFramePresenter"/> as it frames the seat: when
/// each frame was framed, the camera's eye, the body pose the seat is presented at, the fraction its world interpolates
/// by, and which world presents it. <c>world.view.motion</c> reads it back as the evenness of the seat's motion from frame
/// to frame, which a held key should make uniform: a stall (a frame that does not move) or a jump (one that moves far
/// more than its neighbours) is judder the eye sees while the frame rate stays steady. Presentation only.
/// </summary>
/// <remarks>Written on the render thread and read by the console under one lock.</remarks>
public sealed class WorldSeatMotionTrace {
    /// <summary>The most frames the trace holds.</summary>
    public const int Capacity = 240;

    private readonly Lock m_gate = new();
    private readonly Entry[] m_entries = new Entry[Capacity];
    private int m_count;
    private int m_slot;

    private readonly record struct Entry(long Timestamp, Vector3 Eye, Vector3 Body, float Alpha, string World);

    /// <summary>Gets or sets the zero-based seat slot the trace records; changing it forgets the frames held.</summary>
    public int Slot {
        get {
            lock (m_gate) {
                return m_slot;
            }
        }
        set {
            lock (m_gate) {
                m_slot = value;
                m_count = 0;
            }
        }
    }

    /// <summary>Records one presented frame of a seat; a frame of any other seat is ignored.</summary>
    /// <param name="slot">The zero-based seat slot framed.</param>
    /// <param name="eye">The camera's eye, in the presentation's world units.</param>
    /// <param name="body">The body pose the seat is presented at.</param>
    /// <param name="alpha">The fraction the seat's world interpolates its bodies by this frame.</param>
    /// <param name="world">The world presenting the seat: <c>boot</c>, or the routed authority's identity.</param>
    public void Record(int slot, Vector3 eye, Vector3 body, float alpha, string world) {
        lock (m_gate) {
            if (slot != m_slot) {
                return;
            }

            m_entries[(m_count % Capacity)] = new Entry(Alpha: alpha, Body: body, Eye: eye, Timestamp: Stopwatch.GetTimestamp(), World: world);
            m_count++;
        }
    }
    /// <summary>Forgets every frame held.</summary>
    public void Reset() {
        lock (m_gate) {
            m_count = 0;
        }
    }
    /// <summary>Describes the evenness of the last frames held: per frame, how far the body and the eye moved, as a mean,
    /// a spread and the count of stalls and jumps, with the frame interval and the alpha sequence's range.</summary>
    /// <param name="frames">The most recent frames to describe, at least three.</param>
    /// <returns>The description, or why there is none.</returns>
    public string Describe(int frames) {
        lock (m_gate) {
            var held = Math.Min(val1: m_count, val2: Capacity);
            var count = Math.Min(val1: held, val2: Math.Max(val1: frames, val2: 3));

            if (count < 3) {
                return $"seat {(m_slot + 1)}: {held} frames held, too few to judge";
            }

            var first = (m_count - count);
            var bodySteps = new double[(count - 1)];
            var eyeSteps = new double[(count - 1)];
            var intervals = new double[(count - 1)];
            var alphaLow = float.MaxValue;
            var alphaHigh = float.MinValue;
            var worlds = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < count); index++) {
                var entry = m_entries[((first + index) % Capacity)];

                alphaLow = MathF.Min(alphaLow, entry.Alpha);
                alphaHigh = MathF.Max(alphaHigh, entry.Alpha);
                _ = worlds.Add(item: entry.World);
                if (index == 0) {
                    continue;
                }

                var previous = m_entries[((first + index - 1) % Capacity)];

                bodySteps[(index - 1)] = Vector3.Distance(value1: entry.Body, value2: previous.Body);
                eyeSteps[(index - 1)] = Vector3.Distance(value1: entry.Eye, value2: previous.Eye);
                intervals[(index - 1)] = Stopwatch.GetElapsedTime(startingTimestamp: previous.Timestamp, endingTimestamp: entry.Timestamp).TotalMilliseconds;
            }

            return string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"seat {(m_slot + 1)} over {count} frames in {string.Join(separator: ",", values: worlds)}: interval {Steps(values: intervals, unit: "ms")} | body {Steps(values: bodySteps, unit: "mm", scale: 1000.0)} | eye {Steps(values: eyeSteps, unit: "mm", scale: 1000.0)} | alpha {alphaLow:0.00}..{alphaHigh:0.00}"
            );
        }

        // A sequence's mean and spread, and how many of its steps stall (under a fifth of the mean) or jump (over twice it).
        static string Steps(double[] values, string unit, double scale = 1.0) {
            var mean = 0.0;

            foreach (var value in values) {
                mean += value;
            }
            mean /= values.Length;

            var spread = 0.0;
            var stalls = 0;
            var jumps = 0;

            foreach (var value in values) {
                spread += ((value - mean) * (value - mean));
                stalls += ((value < (0.2 * mean)) ? 1 : 0);
                jumps += ((value > (2.0 * mean)) ? 1 : 0);
            }

            return string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{(mean * scale):0.0} {unit} sd {(Math.Sqrt(spread / values.Length) * scale):0.0} stalls {stalls} jumps {jumps}"
            );
        }
    }
}
