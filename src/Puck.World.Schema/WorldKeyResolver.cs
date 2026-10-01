using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.World;

/// <summary>The live values a keyed value reads besides its keys: the presented tick every tick clock reads, and a
/// state clock's row. The state mirror is the one source a presentation hands the resolver; the validator hands
/// none.</summary>
public interface IWorldClockSource {
    /// <summary>Gets the engine tick the source presents at.</summary>
    PresentedTick Presented { get; }

    /// <summary>Reads a state clock's row as the source presents it.</summary>
    /// <param name="clock">The state clock.</param>
    /// <param name="value">The row's presented value, or zero when it reads none.</param>
    /// <returns><see langword="true"/> when the row reads a number.</returns>
    bool TryClockValue(WorldClock clock, out double value);
}
/// <summary>The segment of a keyed value a phase falls in: the key it leaves, the key it reaches, and how far along
/// it the phase stands. Every length is in phase, a share of the clock's span.</summary>
/// <param name="From">The earlier key's index.</param>
/// <param name="To">The later key's index; the first key for the segment that wraps from the last.</param>
/// <param name="Length">The segment's length in phase, through the wrap for the last segment; zero for a track of one
/// key.</param>
/// <param name="Offset">How far past the earlier key the phase stands, in phase.</param>
/// <param name="Fraction">The even fraction of the way along, <c>Offset / Length</c> clamped to <c>[0, 1]</c>, before
/// the earlier key's ease shapes it.</param>
public readonly record struct WorldKeySegment(int From, int To, double Length, double Offset, double Fraction);
/// <summary>
/// The one resolver of keyed values. It computes a clock's phase from a presented tick or a state row, selects the
/// two keys around a phase, eases the fraction between them by the earlier key's <see cref="WorldEase"/>, and blends
/// by the field's type: a scalar or a vector linearly, a colour in linear light (each key's sRGB-encoded channels
/// decoded, blended and encoded again, alpha linearly), an angle along the shorter arc across a whole turn, and a
/// direction along the great circle between its unit vectors. A rate keyed on a tick clock integrates in closed form,
/// so its offset never jumps where a key changes the rate. The resolver is pure: it reads live values only through
/// the <see cref="IWorldClockSource"/> it is handed, so the validator, which hands none, and every presentation,
/// which hands its state mirror, resolve a key alike.
/// </summary>
public static class WorldKeyResolver {
    /// <summary>Returns the clock a document's timeline names.</summary>
    /// <param name="timeline">The timeline section.</param>
    /// <param name="name">The clock's name.</param>
    /// <param name="clock">The clock, or <see langword="null"/> when the timeline names none.</param>
    /// <returns><see langword="true"/> when the timeline names the clock.</returns>
    public static bool TryClock(WorldTimelineSection timeline, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldClock? clock) {
        ArgumentNullException.ThrowIfNull(argument: timeline);

        var clocks = timeline.Clocks;

        if (clocks is not null) {
            for (var index = 0; (index < clocks.Count); index++) {
                if (string.Equals(
                    a: clocks[index]?.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    clock = clocks[index];

                    return true;
                }
            }
        }

        clock = null;

        return false;
    }
    /// <summary>Returns a clock's phase as a source presents it: a tick clock's at the source's tick, a state clock's
    /// the fractional part of its row's presented value, and an anchored clock's predicted from its anchor at the
    /// source's tick (<see cref="WorldClockAnchor.PhaseAt"/>).</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="source">The source, or <see langword="null"/> for none, which reads no phase.</param>
    /// <param name="phase">The phase, in <c>[0, 1)</c>, or zero when it reads none.</param>
    /// <returns><see langword="true"/> when the phase reads.</returns>
    public static bool TryPhase(WorldClock clock, IWorldClockSource? source, out double phase) {
        ArgumentNullException.ThrowIfNull(argument: clock);

        phase = 0d;

        if (source is null) {
            return false;
        }

        if (clock.IsStateClock) {
            if (!source.TryClockValue(
                clock: clock,
                value: out var value
            )) {
                return false;
            }

            phase = WorldClocks.Phase(value: value);

            return true;
        }

        if (clock.IsAnchored) {
            if (clock.Anchor is not { IsWellFormed: true } anchor) {
                return false;
            }

            phase = anchor.PhaseAt(tick: source.Presented);

            return true;
        }

        phase = WorldClocks.Phase(
            clock: clock,
            tick: source.Presented
        );

        return true;
    }
    /// <summary>Returns the two keys around a phase and the eased fraction of the way from the first to the second.
    /// Before the first key and after the last, the segment is the one from the last key wrapping into the first; a
    /// track of one key holds it.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="track">The track; at least one key, ascending.</param>
    /// <param name="span">The clock's span, in the units the keys' times are authored in.</param>
    /// <param name="phase">The clock's phase, in <c>[0, 1)</c>.</param>
    /// <returns>The earlier key's index, the later key's index, and the eased fraction in <c>[0, 1]</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="track"/> carries no keys.</exception>
    public static (int From, int To, double T) Locate<T>(WorldKeyTrack<T> track, double span, double phase) {
        var segment = Segment(
            phase: phase,
            span: span,
            track: track
        );

        return (segment.From, segment.To, Ease(
            ease: track.Keys[segment.From].Ease,
            t: segment.Fraction
        ));
    }
    /// <summary>Returns the segment a phase falls in, as <see cref="Locate{T}(WorldKeyTrack{T}, double, double)"/>
    /// selects it, with its even fraction before the earlier key's ease shapes it. Every comparison stays in phase
    /// space: a key's time is divided by the span, never a phase multiplied back into time, so an exact key tick
    /// selects its key.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="track">The track; at least one key, ascending.</param>
    /// <param name="span">The clock's span, in the units the keys' times are authored in.</param>
    /// <param name="phase">The clock's phase, in <c>[0, 1)</c>.</param>
    /// <returns>The segment; a track of one key is the segment from that key to itself, of no length.</returns>
    /// <exception cref="ArgumentException"><paramref name="track"/> carries no keys.</exception>
    public static WorldKeySegment Segment<T>(WorldKeyTrack<T> track, double span, double phase) {
        ArgumentNullException.ThrowIfNull(argument: track);

        var keys = track.Keys;
        var count = keys.Length;

        if (count == 0) {
            throw new ArgumentException(
                message: "A keyed value carries at least one key.",
                paramName: nameof(track)
            );
        }

        if (count == 1) {
            return new WorldKeySegment(
                Fraction: 0d,
                From: 0,
                Length: 0d,
                Offset: 0d,
                To: 0
            );
        }

        var from = (count - 1);

        for (var index = 0; (index < count); index++) {
            if ((keys[index].At / span) <= phase) {
                from = index;
            } else {
                break;
            }
        }

        var to = ((from + 1) % count);
        var fromAt = (keys[from].At / span);
        var length = ((to == 0)
            ? ((1d - fromAt) + (keys[0].At / span))
            : ((keys[to].At / span) - fromAt)
        );
        var offset = ((phase >= fromAt)
            ? (phase - fromAt)
            : ((1d - fromAt) + phase)
        );

        return new WorldKeySegment(
            Fraction: ((length > 0d)
                ? Math.Clamp(
                    max: 1d,
                    min: 0d,
                    value: (offset / length)
                )
                : 0d),
            From: from,
            Length: length,
            Offset: offset,
            To: to
        );
    }
    /// <summary>Returns an ease's shaping of an even fraction.</summary>
    /// <param name="ease">The ease.</param>
    /// <param name="t">The even fraction, in <c>[0, 1]</c>.</param>
    /// <returns>The eased fraction: <paramref name="t"/> for <see cref="WorldEase.Linear"/>, <c>t² (3 − 2t)</c> for
    /// <see cref="WorldEase.Smooth"/>, and zero below one for <see cref="WorldEase.Step"/>.</returns>
    public static double Ease(WorldEase ease, double t) => (ease switch {
        WorldEase.Smooth => ((t * t) * (3d - (2d * t))),
        WorldEase.Step => ((t >= 1d)
            ? 1d
            : 0d),
        _ => t,
    });
    /// <summary>Returns a keyed scalar at a phase, blended linearly.</summary>
    /// <param name="track">The track.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <returns>The value.</returns>
    public static float Scalar(WorldKeyTrack<float> track, double span, double phase) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );
        var a = track.Keys[from].Value;
        var b = track.Keys[to].Value;

        return ((float)(a + ((b - a) * t)));
    }
    /// <summary>Returns a keyed angle at a phase, blended along the shorter arc between the two keys' angles: from
    /// 350° to 10° it passes 0°, never 180°.</summary>
    /// <param name="track">The track, in radians.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <returns>The angle, in radians: the earlier key's angle plus the eased share of the shorter arc, unwrapped.</returns>
    public static float Angle(WorldKeyTrack<float> track, double span, double phase) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );

        return ((float)ShortArc(
            from: track.Keys[from].Value,
            t: t,
            to: track.Keys[to].Value
        ));
    }
    /// <summary>Returns a keyed colour at a phase, blended in linear light: each key's red, green and blue decoded
    /// from sRGB, blended, and encoded again; alpha blended as it is.</summary>
    /// <param name="track">The track, whose values are colour literals.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <param name="fallback">The colour a key that holds no literal reads.</param>
    /// <returns>The colour.</returns>
    public static Vector4 Color(WorldKeyTrack<BindableColor> track, double span, double phase, Vector4 fallback) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );

        return LinearLight(
            from: (track.Keys[from].Value.Literal ?? fallback),
            t: t,
            to: (track.Keys[to].Value.Literal ?? fallback)
        );
    }
    /// <summary>Returns a keyed direction at a phase, blended along the great circle between the two keys' unit
    /// vectors. A zero key reads <see cref="Vector3.UnitY"/>; two opposite keys turn about the axis
    /// <see cref="Perpendicular"/> names.</summary>
    /// <param name="track">The track.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <returns>The direction, of unit length.</returns>
    public static Vector3 Direction(WorldKeyTrack<Vector3> track, double span, double phase) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );

        return GreatCircle(
            from: track.Keys[from].Value,
            t: t,
            to: track.Keys[to].Value
        );
    }
    /// <summary>Returns a keyed two-component vector at a phase, blended linearly.</summary>
    /// <param name="track">The track.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <returns>The vector.</returns>
    public static Vector2 Vector(WorldKeyTrack<Vector2> track, double span, double phase) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );
        var a = track.Keys[from].Value;
        var b = track.Keys[to].Value;

        return new Vector2(
            x: ((float)(a.X + ((b.X - a.X) * t))),
            y: ((float)(a.Y + ((b.Y - a.Y) * t)))
        );
    }
    /// <summary>Returns a keyed three-component vector at a phase, blended linearly and never normalized.</summary>
    /// <param name="track">The track.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The clock's phase.</param>
    /// <returns>The vector.</returns>
    public static Vector3 Vector(WorldKeyTrack<Vector3> track, double span, double phase) {
        var (from, to, t) = Locate(
            phase: phase,
            span: span,
            track: track
        );
        var a = track.Keys[from].Value;
        var b = track.Keys[to].Value;

        return new Vector3(
            x: ((float)(a.X + ((b.X - a.X) * t))),
            y: ((float)(a.Y + ((b.Y - a.Y) * t))),
            z: ((float)(a.Z + ((b.Z - a.Z) * t)))
        );
    }
    /// <summary>Returns two colours blended in linear light.</summary>
    /// <param name="from">The colour at zero, sRGB-encoded.</param>
    /// <param name="to">The colour at one, sRGB-encoded.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The blend, sRGB-encoded.</returns>
    public static Vector4 LinearLight(Vector4 from, Vector4 to, double t) => new(
        w: ((float)(from.W + ((to.W - from.W) * t))),
        x: Channel(
            from: from.X,
            t: t,
            to: to.X
        ),
        y: Channel(
            from: from.Y,
            t: t,
            to: to.Y
        ),
        z: Channel(
            from: from.Z,
            t: t,
            to: to.Z
        )
    );
    /// <summary>Returns an angle blended along the shorter arc to another.</summary>
    /// <param name="from">The angle at zero, in radians.</param>
    /// <param name="to">The angle at one, in radians.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The blend, in radians, unwrapped from <paramref name="from"/>.</returns>
    public static double ShortArc(double from, double to, double t) => (from + (Math.IEEERemainder(
        x: (to - from),
        y: Math.Tau
    ) * t));
    /// <summary>Returns two directions blended along the great circle between their unit vectors.</summary>
    /// <param name="from">The direction at zero, any nonzero length.</param>
    /// <param name="to">The direction at one, any nonzero length.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The blend, of unit length.</returns>
    public static Vector3 GreatCircle(Vector3 from, Vector3 to, double t) {
        var a = Unit(value: from);
        var b = Unit(value: to);
        var cosine = Math.Clamp(
            max: 1d,
            min: -1d,
            value: (((((double)a.X) * b.X) + (((double)a.Y) * b.Y)) + (((double)a.Z) * b.Z))
        );
        var angle = Math.Acos(d: cosine);

        if (angle < 1e-9d) {
            return a;
        }

        if ((Math.PI - angle) < 1e-6d) {
            // Opposite keys bound no one great circle; turn about a fixed perpendicular so every reader picks the same.
            var axis = Perpendicular(direction: a);
            var turn = (angle * t);

            return Unit(value: ((a * ((float)Math.Cos(d: turn))) + (Vector3.Cross(
                vector1: axis,
                vector2: a
            ) * ((float)Math.Sin(a: turn)))));
        }

        var sine = Math.Sin(a: angle);
        var weightFrom = (Math.Sin(a: ((1d - t) * angle)) / sine);
        var weightTo = (Math.Sin(a: (t * angle)) / sine);

        return Unit(value: new Vector3(
            x: ((float)((a.X * weightFrom) + (b.X * weightTo))),
            y: ((float)((a.Y * weightFrom) + (b.Y * weightTo))),
            z: ((float)((a.Z * weightFrom) + (b.Z * weightTo)))
        ));
    }
    /// <summary>Returns the axis two opposite directions turn about: the unit cross product of the direction with
    /// whichever world axis it is least aligned with.</summary>
    /// <param name="direction">The direction, of unit length.</param>
    /// <returns>A unit vector perpendicular to <paramref name="direction"/>.</returns>
    public static Vector3 Perpendicular(Vector3 direction) {
        var x = MathF.Abs(x: direction.X);
        var y = MathF.Abs(x: direction.Y);
        var z = MathF.Abs(x: direction.Z);
        var axis = (((x <= y) && (x <= z))
            ? Vector3.UnitX
            : ((y <= z)
                ? Vector3.UnitY
                : Vector3.UnitZ)
        );

        return Unit(value: Vector3.Cross(
            vector1: direction,
            vector2: axis
        ));
    }
    /// <summary>Returns a rate integrated from engine tick zero to a presented tick, reduced into
    /// <c>[−<paramref name="modulus"/> / 2, <paramref name="modulus"/> / 2]</c>: a cloud drift's offset, a spin's angle,
    /// a twinkle's cycles. A literal rate integrates as <see cref="PresentedTick.Integrate"/> does. A rate keyed on a
    /// tick clock integrates each key segment in closed form: whole periods of the clock through the integral over one
    /// period, the period in progress through the segments it has passed and the part of the one it is in, so the
    /// offset is continuous where a key changes the rate.</summary>
    /// <param name="track">The rate's keys, in units per second.</param>
    /// <param name="clock">The tick clock the keys read.</param>
    /// <param name="tick">The presented tick.</param>
    /// <param name="modulus">The period the result is reduced by, in the rate's units; positive.</param>
    /// <returns>The reduced integral.</returns>
    /// <exception cref="ArgumentException"><paramref name="clock"/> is no tick clock, or <paramref name="track"/>
    /// carries no keys.</exception>
    public static double Integrate(WorldKeyTrack<float> track, WorldClock clock, PresentedTick tick, double modulus) => Integrate(
        clock: clock,
        modulus: modulus,
        tick: tick,
        track: track,
        value: static value => value
    );
    /// <summary>Returns one component of a keyed rate integrated from engine tick zero to a presented tick, as
    /// <see cref="Integrate(WorldKeyTrack{float}, WorldClock, PresentedTick, double)"/> integrates a scalar rate: a
    /// drift's offset along one axis.</summary>
    /// <typeparam name="T">The rate's type.</typeparam>
    /// <param name="track">The rate's keys, in units per second.</param>
    /// <param name="value">Reads the integrated component of a key's value.</param>
    /// <param name="clock">The tick clock the keys read.</param>
    /// <param name="tick">The presented tick.</param>
    /// <param name="modulus">The period the result is reduced by, in the rate's units; positive.</param>
    /// <returns>The reduced integral.</returns>
    /// <exception cref="ArgumentException"><paramref name="clock"/> is no tick clock, or <paramref name="track"/>
    /// carries no keys.</exception>
    public static double Integrate<T>(WorldKeyTrack<T> track, Func<T, double> value, WorldClock clock, PresentedTick tick, double modulus) {
        ArgumentNullException.ThrowIfNull(argument: value);
        ArgumentNullException.ThrowIfNull(argument: track);
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: modulus);

        if (!clock.IsTickClock) {
            throw new ArgumentException(
                message: $"Clock '{clock.Name}' is no tick clock: a state row's or an anchor's integral would depend on its history; a rate keys only on a tick clock.",
                paramName: nameof(clock)
            );
        }

        if (track.Keys.Length == 0) {
            throw new ArgumentException(
                message: "A keyed value carries at least one key.",
                paramName: nameof(track)
            );
        }

        var period = WorldClocks.PeriodTicks(clock: clock);
        var start = WorldClocks.StartTicks(clock: clock);
        var periodSeconds = (((double)period) / EngineTicks.PerSecond);
        var perPeriod = (periodSeconds * PhaseIntegral(
            phase: 1d,
            span: clock.Span,
            track: track,
            value: value
        ));
        // The clock stands `start` ticks into its period at tick zero, so the integral from tick zero is the integral
        // over its own position from `start` to `tick + start`.
        var shifted = (((UInt128)tick.Whole) + start);
        var periods = ((ulong)(shifted / period));
        var into = (((double)((ulong)(shifted % period))) + tick.Fraction);

        if (into >= period) {
            into -= period;
            periods++;
        }

        var whole = Math.IEEERemainder(
            x: (((double)periods) * perPeriod),
            y: modulus
        );
        var partial = (periodSeconds * PhaseIntegral(
            phase: (into / period),
            span: clock.Span,
            track: track,
            value: value
        ));
        var before = (periodSeconds * PhaseIntegral(
            phase: (((double)start) / period),
            span: clock.Span,
            track: track,
            value: value
        ));

        return Math.IEEERemainder(
            x: ((whole + partial) - before),
            y: modulus
        );
    }
    /// <summary>Returns a keyed value's integral over its clock's phase from zero to a phase, in value units times
    /// phase: the area under the eased keys, the wrap segment's two pieces included.</summary>
    /// <param name="track">The track.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The phase to integrate to, in <c>[0, 1]</c>.</param>
    /// <returns>The integral.</returns>
    public static double PhaseIntegral(WorldKeyTrack<float> track, double span, double phase) => PhaseIntegral(
        phase: phase,
        span: span,
        track: track,
        value: static value => value
    );
    /// <summary>Returns one component of a keyed value's integral over its clock's phase from zero to a phase, as
    /// <see cref="PhaseIntegral(WorldKeyTrack{float}, double, double)"/> integrates a scalar.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="track">The track.</param>
    /// <param name="value">Reads the integrated component of a key's value.</param>
    /// <param name="span">The clock's span.</param>
    /// <param name="phase">The phase to integrate to, in <c>[0, 1]</c>.</param>
    /// <returns>The integral.</returns>
    public static double PhaseIntegral<T>(WorldKeyTrack<T> track, Func<T, double> value, double span, double phase) {
        ArgumentNullException.ThrowIfNull(argument: track);
        ArgumentNullException.ThrowIfNull(argument: value);

        var keys = track.Keys;
        var count = keys.Length;

        if (count == 1) {
            return (value(arg: keys[0].Value) * phase);
        }

        var total = 0d;

        for (var index = 0; (index < count); index++) {
            var next = ((index + 1) % count);
            var start = (keys[index].At / span);
            var length = ((next == 0)
                ? ((1d - start) + (keys[0].At / span))
                : ((keys[next].At / span) - start)
            );

            if (length <= 0d) {
                continue;
            }

            // A segment covers [start, start + length], which past one wraps to [start - 1, start + length - 1]; each
            // copy contributes its overlap with [0, phase].
            for (var shift = 0; (shift < 2); shift++) {
                var low = (start - shift);
                var from = Math.Max(
                    val1: low,
                    val2: 0d
                );
                var to = Math.Min(
                    val1: (low + length),
                    val2: phase
                );

                if (to <= from) {
                    continue;
                }

                total += (length * SegmentArea(
                    a: value(arg: keys[index].Value),
                    b: value(arg: keys[next].Value),
                    ease: keys[index].Ease,
                    from: ((from - low) / length),
                    to: ((to - low) / length)
                ));
            }
        }

        return total;
    }

    // The area under one eased segment between two local fractions: ∫ a + (b − a)·e(τ) dτ.
    private static double SegmentArea(double a, double b, WorldEase ease, double from, double to) {
        var eased = (ease switch {
            WorldEase.Smooth => (SmoothArea(t: to) - SmoothArea(t: from)),
            WorldEase.Step => 0d,
            _ => (((to * to) - (from * from)) / 2d),
        });

        return ((a * (to - from)) + ((b - a) * eased));
    }
    // ∫₀ᵗ τ²(3 − 2τ) dτ.
    private static double SmoothArea(double t) => (((t * t) * t) - ((((t * t) * t) * t) / 2d));
    private static float Channel(float from, float to, double t) {
        var a = ImageSourceConversion.SrgbToLinear(value: from);
        var b = ImageSourceConversion.SrgbToLinear(value: to);

        return ((float)ImageSourceConversion.LinearToSrgb(value: (a + ((b - a) * t))));
    }
    private static Vector3 Unit(Vector3 value) {
        var length = value.Length();

        return (((length > 0f) && float.IsFinite(f: length))
            ? (value / length)
            : Vector3.UnitY
        );
    }
}
