using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>
/// The one implementation of a look's form effect (<see cref="WorldFormEffect"/>) as the client plays it: the band it
/// sweeps over a run of slots (<see cref="SdfFormBand"/>), the extent of a creation along the sweep, and the pieces a form
/// sheds or assembles (<see cref="WorldFormPieces"/>). The stamp pool plays it across a body's form switch and the rigid
/// copies across a body arriving and leaving; both hand their runs here, so a band reads alike whoever plays it.
/// Presentation only: every random draw is a <see cref="Pcg32XshRr"/> stream keyed by a seed the caller derives from the
/// body, so every client plays the same effect, and nothing here reaches simulation state.
/// </summary>
public static class WorldFormEffects {
    /// <summary>Returns the way an effect sweeps, in the creation's own frame: its authored direction made unit, or up.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The unit direction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    public static Vector3 Direction(WorldFormEffect effect) {
        ArgumentNullException.ThrowIfNull(argument: effect);

        var direction = (effect.Direction?.Value ?? Vector3.UnitY);

        return ((direction.LengthSquared() > 0f) ? Vector3.Normalize(value: direction) : Vector3.UnitY);
    }
    /// <summary>Returns how far a creation reaches along a direction at rest, in creation units: the least and greatest of
    /// its rest box's corners along it (<see cref="CreationGeometry.RestBox"/>), or its reach both ways where it fills no
    /// finite box.</summary>
    /// <param name="document">The creation's engine document.</param>
    /// <param name="direction">The unit direction, in the creation's frame.</param>
    /// <returns>The least and greatest distance along <paramref name="direction"/> from the creation's origin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static (float Low, float High) Extent(CreationDocument document, Vector3 direction) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var (low, high) = CreationGeometry.RestBox(document: document);

        if (!(low.X <= high.X) || !(low.Y <= high.Y) || !(low.Z <= high.Z)) {
            var reach = CreationGeometry.Reach(document: document);

            return (-reach, reach);
        }

        // The box's support along the direction: its centre's distance less or plus its half extent's projection.
        var centre = ((low + high) * 0.5f);
        var half = ((high - low) * 0.5f);
        var along = Vector3.Dot(vector1: centre, vector2: direction);
        var spread = Vector3.Dot(vector1: half, vector2: Vector3.Abs(value: direction));

        return ((along - spread), (along + spread));
    }
    /// <summary>Returns the seed a body's form effect draws from: the body, the creation it plays on and the count of
    /// effects that body has played, mixed so neighbouring bodies and successive switches draw apart.</summary>
    /// <param name="body">The body's population index, or a negative value for a run no body owns.</param>
    /// <param name="creation">The creation's identity hash.</param>
    /// <param name="ordinal">How many effects the player of this one has begun before it.</param>
    /// <returns>The seed.</returns>
    public static uint Seed(int body, string? creation, long ordinal) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: unchecked((uint)body));
        hash.Add(value: ordinal);
        hash.Add(value: creation);
        return unchecked((uint)hash.Value);
    }
    /// <summary>Returns the light an authored colour and luminance mean, linear scene colour in nits: the colour decoded
    /// (<see cref="DisplayColor.ToScene"/>), or the fallback's, or white, times the luminance.</summary>
    /// <param name="color">The hex colour, or <see langword="null"/>.</param>
    /// <param name="fallback">The hex colour used where <paramref name="color"/> is absent, or <see langword="null"/>.</param>
    /// <param name="luminance">The luminance, nits.</param>
    /// <returns>The light.</returns>
    public static Vector3 Light(string? color, string? fallback, float luminance) {
        if (!(luminance > 0f)) { return Vector3.Zero; }

        var display = HexColor.Parse(value: (color ?? fallback), fallback: new DisplayColor(rgb: Vector3.One));

        return (display.ToScene().Linear * luminance);
    }
    /// <summary>Returns the band an effect sweeps over one run of slots at one moment.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="arriving">Whether the run arrives (it is built where the band has passed) or leaves (it dissolves
    /// there).</param>
    /// <param name="seed">The seed the band's noise and motes draw from.</param>
    /// <param name="firstSlot">The run's first absolute slot.</param>
    /// <param name="slotCount">The run's slot count.</param>
    /// <param name="origin">The world point the sweep is measured from: the run's root.</param>
    /// <param name="rotation">The run's root orientation, which turns the effect's direction into the world.</param>
    /// <param name="low">Where the form starts along the direction from <paramref name="origin"/>, metres; the band starts
    /// its edge's width and roughness before it.</param>
    /// <param name="high">Where the form ends, metres; the band ends its edge's width and roughness past it.</param>
    /// <param name="elapsed">The seconds since the band started; a negative value holds it at its start.</param>
    /// <param name="parted">Whether the run's slots move apart, so a draw blending several of them is not drawn.</param>
    /// <param name="direction">The sweep's direction in the run's frame, or <see langword="null"/> for the effect's
    /// own.</param>
    /// <param name="seconds">The sweep's length, seconds, or <see langword="null"/> for the effect's own.</param>
    /// <param name="side">The side the run is drawn on, or <see langword="null"/> for the one <paramref name="arriving"/>
    /// gives; <see cref="SdfFormBandSide.Whole"/> cuts nothing (a shell flaring, a strand parting).</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    public static SdfFormBand Band(WorldFormEffect effect, bool arriving, uint seed, int firstSlot, int slotCount, Vector3 origin, Quaternion rotation, float low, float high,
        float elapsed, bool parted, Vector3? direction = null, float? seconds = null, SdfFormBandSide? side = null) {
        ArgumentNullException.ThrowIfNull(argument: effect);

        var axis = Vector3.Transform(value: (direction ?? Direction(effect: effect)), rotation: rotation);
        // The sweep starts and ends a roughened edge's width past the form, so no surface is cut or glows before the
        // band starts or after it ends.
        var pad = (MathF.Max(x: effect.Roughness, y: 0f) + MathF.Max(x: effect.EdgeWidth, y: 0f));

        low -= pad;
        high += pad;

        return new SdfFormBand {
            FirstSlot = firstSlot,
            SlotCount = slotCount,
            Side = (side ?? (arriving ? SdfFormBandSide.Swept : SdfFormBandSide.Ahead)),
            Parted = parted,
            Seed = seed,
            Origin = origin,
            Axis = ((axis.LengthSquared() > 0f) ? Vector3.Normalize(value: axis) : Vector3.UnitY),
            Low = low,
            High = MathF.Max(x: low, y: high),
            Elapsed = MathF.Max(x: elapsed, y: 0f),
            Seconds = MathF.Max(x: (seconds ?? effect.Seconds), y: 1e-3f),
            EdgeLight = Light(color: effect.EdgeColor, fallback: null, luminance: effect.EdgeLuminance),
            EdgeWidth = effect.EdgeWidth,
            Roughness = effect.Roughness,
            NoiseScale = (1f / MathF.Max(x: effect.RoughnessScale, y: 1e-3f)),
            MoteCount = effect.Motes,
            MoteLight = Light(color: effect.MoteColor, fallback: effect.EdgeColor, luminance: effect.MoteLuminance),
            MoteSize = effect.MoteSize,
            MoteSeconds = effect.MoteSeconds,
            MoteSpeed = effect.MoteSpeed,
            MoteRise = effect.MoteRise,
            MoteSwirl = effect.MoteSwirl,
        };
    }
    /// <summary>Begins an effect's play on a creation: its seed and its sweep's extent along its direction at rest.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="arriving">Whether the form arrives or leaves.</param>
    /// <param name="creation">The creation the form wears.</param>
    /// <param name="body">The body's population index, or a negative value for none.</param>
    /// <param name="ordinal">How many plays its player began before it.</param>
    /// <param name="start">When it begins, on its player's clock, seconds.</param>
    /// <returns>The play.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> or <paramref name="creation"/> is
    /// <see langword="null"/>.</exception>
    public static WorldFormPlay Begin(WorldFormEffect effect, bool arriving, WorldPrototype creation, int body, long ordinal, double start) {
        ArgumentNullException.ThrowIfNull(argument: effect);
        ArgumentNullException.ThrowIfNull(argument: creation);

        var (low, high) = Extent(direction: Direction(effect: effect), document: creation.EngineDocument);

        return new WorldFormPlay(Effect: effect, Arriving: arriving, Seed: Seed(body: body, creation: creation.Hash, ordinal: ordinal), Start: start, Low: low, High: high);
    }
    /// <summary>Returns the eased share of an assembling piece's flight: past one near the end, so the piece overshoots
    /// its place and settles back into it as it locks (a back ease, whose overshoot is about a tenth).</summary>
    /// <param name="share">The share of the flight elapsed, in [0, 1].</param>
    /// <returns>The eased share: zero at zero and one at one.</returns>
    public static float LockIn(float share) {
        const float Overshoot = 1.70158f;

        var s = (Math.Clamp(value: share, min: 0f, max: 1f) - 1f);

        return (1f + (((Overshoot + 1f) * s * s * s) + (Overshoot * s * s)));
    }
}

/// <summary>One play of a form effect on one form: the effect, which way it plays, its seed, when it began on its player's
/// clock, and how far the form reaches along its sweep at rest, in creation units. The stamp pool and the rigid copies each
/// hold one per playing form and read its band and its end from here.</summary>
/// <param name="Effect">The effect.</param>
/// <param name="Arriving">Whether the form arrives (it is built) or leaves (it dissolves).</param>
/// <param name="Seed">The seed its draws are keyed on.</param>
/// <param name="Start">When it began on its player's clock, seconds.</param>
/// <param name="Low">The least distance along the sweep's direction from the creation's origin, creation units.</param>
/// <param name="High">The greatest such distance, creation units.</param>
public sealed record WorldFormPlay(WorldFormEffect Effect, bool Arriving, uint Seed, double Start, float Low, float High) {
    /// <summary>Returns the seconds the play has run at a moment of its player's clock, its delay past; negative before it
    /// starts.</summary>
    /// <param name="clock">The player's clock, seconds.</param>
    /// <returns>The seconds.</returns>
    public float Elapsed(double clock) => (float)(clock - Start - Math.Max(val1: Effect.Delay, val2: 0f));
    /// <summary>Returns whether a whole form's play has played at a moment: its sweep and its last motes' lives done.</summary>
    /// <param name="clock">The player's clock, seconds.</param>
    /// <returns><see langword="true"/> once it has played.</returns>
    public bool Ended(double clock) => (Elapsed(clock: clock) > (Effect.TotalSeconds - Math.Max(val1: Effect.Delay, val2: 0f)));
    /// <summary>Returns the band a whole form's play sweeps over its run at a moment, in the run's root frame.</summary>
    /// <param name="clock">The player's clock, seconds.</param>
    /// <param name="firstSlot">The run's first absolute slot.</param>
    /// <param name="slotCount">The run's slot count.</param>
    /// <param name="origin">The run's root position this frame.</param>
    /// <param name="rotation">The run's root orientation this frame.</param>
    /// <param name="scale">The run's scale, metres a creation unit.</param>
    /// <returns>The band.</returns>
    public SdfFormBand Band(double clock, int firstSlot, int slotCount, Vector3 origin, Quaternion rotation, float scale) =>
        WorldFormEffects.Band(effect: Effect, arriving: Arriving, seed: Seed, firstSlot: firstSlot, slotCount: slotCount, origin: origin, rotation: rotation,
            low: (Low * scale), high: (High * scale), elapsed: Elapsed(clock: clock), parted: false);
}
