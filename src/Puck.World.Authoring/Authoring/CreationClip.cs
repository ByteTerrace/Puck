using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World.Authoring;

/// <summary>
/// A pose clip: key poses on a creation's joints, played by one driver whose phase is the position in the clip and
/// whose share on each shape is the blend. A key turns a shape about its joint (<see cref="CreationRig.Joint"/>) and may
/// slide it, measured from the shape's rest pose, so a turn carries the shapes that hang from it and layers on other
/// motion. Presentation-only, like the driver that plays it.
/// </summary>
/// <param name="Name">The clip's name, unique within the creation.</param>
/// <param name="Driver">The driver that plays the clip; its phase is in the units of <see cref="CreationClipKeyDocument.At"/>.</param>
/// <param name="Keys">The keys, at strictly increasing positions; at least one.</param>
/// <param name="Wrap">Whether the clip loops: its position is the driver's phase modulo <see cref="Period"/>, counted so
/// a negative phase wraps like a positive one, and the stretch from a track's last key back to its first interpolates
/// like any other. Null or false holds: the position is the phase, and a track holds its first key before it and its
/// last key after it. A driver whose phase wraps (a travel or time driver, and a driver that follows one) may play only
/// a looping clip whose period divides the span the phase wraps over.</param>
/// <param name="Period">A looping clip's period, in phase units (null = one turn, 2π); every key lies in
/// <c>[0, period)</c>. Refused on a held clip.</param>
public sealed record CreationClipDocument(
    string Name,
    string Driver,
    IReadOnlyList<CreationClipKeyDocument> Keys,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Wrap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Period = null
) {
    /// <summary>The stretch arriving at a key follows a straight line in its fraction.</summary>
    public const string EaseLinear = "linear";
    /// <summary>The stretch arriving at a key eases in and out (a Hermite smoothstep of its fraction).</summary>
    public const string EaseSmooth = "smooth";
    /// <summary>The stretch arriving at a key holds the key before it, and the key takes over at its position.</summary>
    public const string EaseHold = "hold";

    /// <summary>Returns whether an ease name is one a key may carry.</summary>
    /// <param name="ease">The ease name, or <see langword="null"/> for <see cref="EaseLinear"/>.</param>
    /// <returns><see langword="true"/> for null, <see cref="EaseLinear"/>, <see cref="EaseSmooth"/> and
    /// <see cref="EaseHold"/>.</returns>
    public static bool IsEase(string? ease) => (ease is null or EaseLinear or EaseSmooth or EaseHold);
}
/// <summary>One key of a pose clip.</summary>
/// <param name="At">The key's position, in the units of its driver's phase.</param>
/// <param name="Poses">The shapes the key poses; a shape the key does not name has no key here, and its track
/// interpolates across this position.</param>
/// <param name="Ease">How the stretch arriving at this key moves: <see cref="CreationClipDocument.EaseLinear"/> (null),
/// <see cref="CreationClipDocument.EaseSmooth"/> or <see cref="CreationClipDocument.EaseHold"/>.</param>
public sealed record CreationClipKeyDocument(
    float At,
    IReadOnlyList<CreationClipPoseDocument> Poses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ease = null
);
/// <summary>One shape's pose in a key: a turn about the shape's joint and a slide, both literal and measured from rest.
/// A pose with neither is the rest pose.</summary>
/// <param name="Shape">The name of the shape posed. A shape carrying domain folds may not be posed.</param>
/// <param name="Turn">The turn's steps, composed in the order written, the first turning first; the composed turn is under
/// a half turn. Null is no turn.</param>
/// <param name="Slide">The translation, in creation units, added after every turn of the shape. Null is none.</param>
public sealed record CreationClipPoseDocument(
    string Shape,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CreationClipTurnDocument>? Turn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Vector3? Slide = null
);
/// <summary>One step of a key's turn, written as a swing is: an axis in the creation's frame and an angle about it. It
/// crosses the author frame as a swing's axis does (its axis flips and its angle does not).</summary>
/// <param name="Axis">The axis, non-zero; its length is ignored.</param>
/// <param name="Angle">The angle in radians, right-handed about <paramref name="Axis"/>.</param>
public sealed record CreationClipTurnDocument(Vector3 Axis, float Angle);
/// <summary>One pose clip as its rig plays it: its driver, resolved once, and how a phase becomes its position.</summary>
public sealed class CreationRigClip {
    internal CreationRigClip(int index, CreationClipDocument document, int driver, float? period) {
        Index = index;
        Document = document;
        Driver = driver;
        Period = period;
    }

    /// <summary>Gets the clip's position in <see cref="CreationDocument.Clips"/>.</summary>
    public int Index { get; }
    /// <summary>Gets the authored row.</summary>
    public CreationClipDocument Document { get; }
    /// <summary>Gets the index of the driver that plays it.</summary>
    public int Driver { get; }
    /// <summary>Gets a looping clip's period in phase units, or <see langword="null"/> for a held clip.</summary>
    public float? Period { get; }

    /// <summary>Returns the clip position a driver phase reads: the phase itself for a held clip, and the phase modulo
    /// the period, in <c>[0, period)</c>, for a looping one.</summary>
    /// <param name="phase">The driver's phase.</param>
    /// <returns>The position.</returns>
    public float Position(float phase) {
        if (Period is not { } period) { return phase; }

        var position = (phase - (period * MathF.Floor(x: (phase / period))));

        return (((position >= period) || (position < 0f)) ? 0f : position);
    }
}
/// <summary>
/// One shape's keys in one clip, compiled: each key's turn stored as the one quaternion whose scalar part is positive,
/// and its slide. Between two keys the turn follows the arc between their stored quaternions with no change of sign
/// (<see cref="VectorFunctions.Arc"/>), so along it the turn from rest never exceeds the larger of its two ends, and two
/// spellings of one key store and play the same thing.
/// </summary>
public sealed class CreationRigTrack {
    private readonly float[] m_at;
    private readonly Quaternion[] m_turns;
    private readonly Vector3[] m_slides;
    private readonly string?[] m_eases;

    internal CreationRigTrack(CreationRigClip clip, int shape, float[] at, Quaternion[] turns, Vector3[] slides, string?[] eases) {
        Clip = clip;
        Shape = shape;
        m_at = at;
        m_turns = turns;
        m_slides = slides;
        m_eases = eases;

        foreach (var turn in turns) { TurnLimit = MathF.Max(x: TurnLimit, y: VectorFunctions.TurnAngle(turn: turn)); }
        foreach (var slide in slides) { SlideLimit = MathF.Max(x: SlideLimit, y: slide.Length()); }
    }

    /// <summary>Gets the clip the track belongs to.</summary>
    public CreationRigClip Clip { get; }
    /// <summary>Gets the shape it poses.</summary>
    public int Shape { get; }
    /// <summary>Gets the number of keys.</summary>
    public int Count => m_at.Length;
    /// <summary>Gets the largest turn of any key, radians, under a half turn: the largest turn the track produces at full
    /// share anywhere along it.</summary>
    public float TurnLimit { get; }
    /// <summary>Gets the longest slide of any key, creation units: the longest slide the track produces at full
    /// share.</summary>
    public float SlideLimit { get; }
    /// <summary>Gets whether any key turns the shape.</summary>
    public bool Turns => (TurnLimit > 0f);
    /// <summary>Gets whether any key slides the shape.</summary>
    public bool Slides => (SlideLimit > 0f);

    /// <summary>Returns the track's turn at a driver phase, at full share.</summary>
    /// <param name="phase">The driver's phase.</param>
    /// <returns>The turn about the shape's joint, with a non-negative scalar part.</returns>
    public Quaternion TurnAt(float phase) {
        Locate(from: out var from, phase: phase, to: out var to, amount: out var amount);

        return VectorFunctions.Arc(amount: amount, from: m_turns[from], to: m_turns[to]);
    }
    /// <summary>Returns the track's slide at a driver phase, at full share.</summary>
    /// <param name="phase">The driver's phase.</param>
    /// <returns>The slide, in creation units.</returns>
    public Vector3 SlideAt(float phase) {
        Locate(from: out var from, phase: phase, to: out var to, amount: out var amount);

        return Vector3.Lerp(value1: m_slides[from], value2: m_slides[to], amount: amount);
    }

    // The two keys around the clip position a phase reads and the eased fraction between them. A held track holds its
    // end keys outside them; a looping track's stretch from its last key wraps to its first, one period on.
    private void Locate(float phase, out int from, out int to, out float amount) {
        var count = m_at.Length;
        var position = Clip.Position(phase: phase);

        from = 0;
        to = 0;
        amount = 0f;
        if (count == 1) { return; }

        var last = (count - 1);

        if ((position < m_at[0]) || (position >= m_at[last])) {
            if (Clip.Period is not { } period) {
                from = to = ((position < m_at[0]) ? 0 : last);

                return;
            }

            var span = ((m_at[0] + period) - m_at[last]);
            var into = ((position >= m_at[last]) ? (position - m_at[last]) : ((position + period) - m_at[last]));

            from = last;
            to = 0;
            amount = Ease(ease: m_eases[0], fraction: (into / span));

            return;
        }

        var low = 0;
        var high = last;

        while ((high - low) > 1) {
            var middle = ((low + high) >> 1);

            if (m_at[middle] <= position) { low = middle; } else { high = middle; }
        }
        from = low;
        to = high;
        amount = Ease(ease: m_eases[high], fraction: ((position - m_at[low]) / (m_at[high] - m_at[low])));
    }
    private static float Ease(string? ease, float fraction) {
        var clamped = Math.Clamp(value: fraction, min: 0f, max: 1f);

        return ease switch {
            CreationClipDocument.EaseSmooth => VectorFunctions.Smoothstep(t: clamped),
            CreationClipDocument.EaseHold => 0f,
            _ => clamped,
        };
    }
}
/// <summary>Compiles a creation's pose clips into tracks, refusing what cannot be placed or bounded. The rig compiles
/// through it and validation reports through it, so the two never disagree about what a clip means.</summary>
public static class CreationClipCompiler {
    /// <summary>The smallest scalar part a key's composed turn may keep: a turn within about a thousandth of a degree of a
    /// half turn has no sign that is unique and is refused.</summary>
    public const float MinTurnScalar = 1e-5f;

    /// <summary>Reports every refusal in a creation's clips.</summary>
    /// <param name="document">The creation, in either frame.</param>
    /// <param name="refuse">Called with each refusal's document path and message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="refuse"/> is
    /// <see langword="null"/>.</exception>
    public static void Validate(CreationDocument document, Action<string, string> refuse) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(refuse);

        _ = Compile(document: document, lookup: new CreationShapeLookup(shapes: (document.Shapes ?? [])), refuse: refuse);
    }
    /// <summary>Returns the span a driver's phase wraps over: one turn for an integrating driver, the span of the driver
    /// it follows times its cadence for a <c>driver.&lt;name&gt;</c> driver, and <see langword="null"/> for a phase that is
    /// a level (a state cell, a rate signal, or a driver following one).</summary>
    /// <param name="drivers">The creation's drivers.</param>
    /// <param name="index">The driver's index.</param>
    /// <returns>The span in phase units; <see cref="float.NaN"/> when the phase wraps over a span the document does not
    /// fix (a state-bound cadence or a <c>cadenceBy</c> curve on a following driver); <see langword="null"/> for a
    /// level.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="drivers"/> is <see langword="null"/>.</exception>
    public static float? WrapSpan(IReadOnlyList<CreationDriverDocument> drivers, int index) {
        ArgumentNullException.ThrowIfNull(drivers);

        var row = drivers[index];

        if (CreationDriverDocument.Integrates(signal: row.Signal)) { return MathF.Tau; }
        if (!CreationDriverDocument.TryDriverSignal(name: out var name, signal: row.Signal)) { return null; }

        var source = -1;

        for (var earlier = 0; (earlier < index); earlier++) {
            if (string.Equals(a: drivers[earlier].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                source = earlier;

                break;
            }
        }
        if ((source < 0) || (WrapSpan(drivers: drivers, index: source) is not { } span)) { return null; }
        if (float.IsNaN(f: span) || (row.Cadence.Reference is not null) || (row.CadenceBy is not null)) { return float.NaN; }

        var scaled = (MathF.Abs(x: row.Cadence.Value) * span);

        return ((scaled > 0f) ? scaled : null);
    }

    // Each shape's tracks, in clip order, or null for a creation with no clips. A refused clip, key or pose is reported and
    // left out, so a rig compiled from an invalid document plays what remains, as the client plays a reference that
    // names nothing.
    internal static List<CreationRigTrack>[]? Compile(CreationDocument document, CreationShapeLookup lookup, Action<string, string> refuse, List<CreationRigClip>? compiled = null) {
        if (document.Clips is not { Count: > 0 } clips) { return null; }

        var shapes = (document.Shapes ?? []);
        var drivers = (document.Drivers ?? []);
        var tracks = new List<CreationRigTrack>[shapes.Count];
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < clips.Count); index++) {
            var clip = clips[index];
            var path = $"clips[{index}]";
            var whole = true;

            if (string.IsNullOrWhiteSpace(value: clip.Name)) {
                refuse(arg1: $"{path}.name", arg2: "a clip name is required.");
            } else if (!names.Add(item: clip.Name)) {
                refuse(arg1: $"{path}.name", arg2: $"duplicate clip name '{clip.Name}'.");
            }

            var driver = -1;

            for (var candidate = 0; (candidate < drivers.Count); candidate++) {
                if (string.Equals(a: drivers[candidate].Name, b: clip.Driver, comparisonType: StringComparison.Ordinal)) {
                    driver = candidate;

                    break;
                }
            }
            if (driver < 0) {
                refuse(arg1: $"{path}.driver", arg2: $"names no driver '{clip.Driver}'.");

                continue;
            }

            var loops = (clip.Wrap ?? false);
            var period = (clip.Period ?? MathF.Tau);

            if (!loops && (clip.Period is not null)) {
                refuse(arg1: $"{path}.period", arg2: $"clip '{clip.Name}' is held, and only a looping clip has a period; set wrap: true or drop the period.");
                whole = false;
            }
            if (loops && (!float.IsFinite(f: period) || (period <= 0f))) {
                refuse(arg1: $"{path}.period", arg2: $"clip '{clip.Name}' has period {period}; a period is finite and positive.");

                continue;
            }

            var span = WrapSpan(drivers: drivers, index: driver);

            if (span is { } wraps) {
                if (!loops) {
                    refuse(arg1: $"{path}.wrap", arg2: $"clip '{clip.Name}' is held, and driver '{clip.Driver}' wraps its phase, so the clip would snap back to its start each time the phase wraps; loop it (wrap: true) with a period that divides the span, or play it from a level (a state cell, a rate signal, or a driver that follows one).");
                    whole = false;
                } else if (float.IsNaN(f: wraps)) {
                    refuse(arg1: $"{path}.driver", arg2: $"driver '{clip.Driver}' wraps its phase over a span its cadenceBy or a state-bound cadence changes, so clip '{clip.Name}' cannot be placed; play it from the driver it follows or from a level.");
                    whole = false;
                } else {
                    var turns = (wraps / period);

                    if ((turns < 0.999f) || (MathF.Abs(x: (turns - MathF.Round(x: turns))) > (1e-4f * MathF.Max(x: 1f, y: turns)))) {
                        refuse(arg1: $"{path}.period", arg2: $"clip '{clip.Name}' loops every {period}, which does not divide the {wraps} driver '{clip.Driver}' wraps its phase over, so the clip would jump where the phase wraps.");
                        whole = false;
                    }
                }
            }

            var keys = (clip.Keys ?? []);

            if (keys.Count == 0) {
                refuse(arg1: $"{path}.keys", arg2: $"clip '{clip.Name}' has no keys; a clip needs at least one.");

                continue;
            }

            for (var key = 0; (key < keys.Count); key++) {
                var at = keys[key].At;

                if (!float.IsFinite(f: at)) {
                    refuse(arg1: $"{path}.keys[{key}].at", arg2: "at is non-finite.");
                    whole = false;
                } else if ((key > 0) && !(at > keys[(key - 1)].At)) {
                    refuse(arg1: $"{path}.keys[{key}].at", arg2: $"at {at} does not follow the key before it ({keys[(key - 1)].At}); keys are written at increasing positions.");
                    whole = false;
                } else if (loops && ((at < 0f) || (at >= period))) {
                    refuse(arg1: $"{path}.keys[{key}].at", arg2: $"at {at} lies outside clip '{clip.Name}''s period [0, {period}); a looping clip's keys lie inside one period.");
                    whole = false;
                }
                if (!CreationClipDocument.IsEase(ease: keys[key].Ease)) {
                    refuse(arg1: $"{path}.keys[{key}].ease", arg2: $"ease '{keys[key].Ease}' is not recognized; the eases are '{CreationClipDocument.EaseLinear}', '{CreationClipDocument.EaseSmooth}' and '{CreationClipDocument.EaseHold}'.");
                    whole = false;
                }
            }

            // Each shape's keys, in key order.
            var perShape = new Dictionary<int, List<(float At, Quaternion Turn, Vector3 Slide, string? Ease)>>();

            for (var key = 0; (key < keys.Count); key++) {
                var poses = (keys[key].Poses ?? []);

                for (var pose = 0; (pose < poses.Count); pose++) {
                    var posePath = $"{path}.keys[{key}].poses[{pose}]";
                    var authored = poses[pose];
                    var shape = lookup.Find(name: authored.Shape);

                    if (shape < 0) {
                        refuse(arg1: $"{posePath}.shape", arg2: $"names no shape '{authored.Shape}'.");
                        whole = false;

                        continue;
                    }
                    if (shapes[shape].Domain is { Count: > 0 }) {
                        refuse(arg1: $"{posePath}.shape", arg2: $"shape '{authored.Shape}' carries domain operators; a fold rides its parent's frame, so a clip may not pose it.");
                        whole = false;

                        continue;
                    }
                    if (!perShape.TryGetValue(key: shape, value: out var list)) { perShape.Add(key: shape, value: list = []); }
                    if ((list.Count > 0) && (list[^1].At == keys[key].At)) {
                        refuse(arg1: $"{posePath}.shape", arg2: $"key {key} poses shape '{authored.Shape}' twice.");
                        whole = false;

                        continue;
                    }

                    var turn = Quaternion.Identity;
                    var steps = (authored.Turn ?? []);
                    var valid = true;

                    for (var step = 0; (step < steps.Count); step++) {
                        var axis = steps[step].Axis;
                        var angle = steps[step].Angle;

                        if (!VectorFunctions.IsFinite(vector: axis) || (axis == Vector3.Zero)) {
                            refuse(arg1: $"{posePath}.turn[{step}].axis", arg2: "axis is zero or non-finite.");
                            valid = false;
                        } else if (!float.IsFinite(f: angle)) {
                            refuse(arg1: $"{posePath}.turn[{step}].angle", arg2: "angle is non-finite.");
                            valid = false;
                        } else {
                            // A later step turns the result of the earlier ones, as a shape's later swing does.
                            turn = Quaternion.Normalize(value: (Quaternion.CreateFromAxisAngle(angle: angle, axis: Vector3.Normalize(value: axis)) * turn));
                        }
                    }
                    turn = VectorFunctions.WithPositiveScalar(turn: turn);
                    if (valid && !(turn.W >= MinTurnScalar)) {
                        refuse(arg1: $"{posePath}.turn", arg2: $"the composed turn on shape '{authored.Shape}' is a half turn or more, which has no one sign to interpolate from; keep a key's turn under a half turn and reach further with another key.");
                        valid = false;
                    }

                    var slide = (authored.Slide ?? Vector3.Zero);

                    if (!VectorFunctions.IsFinite(vector: slide)) {
                        refuse(arg1: $"{posePath}.slide", arg2: "slide is non-finite.");
                        valid = false;
                    }
                    if (!valid) {
                        whole = false;

                        continue;
                    }
                    list.Add(item: (keys[key].At, turn, slide, keys[key].Ease));
                }
            }
            if (!whole) { continue; }

            var rigClip = new CreationRigClip(document: clip, driver: driver, index: index, period: (loops ? period : null));

            compiled?.Add(item: rigClip);
            foreach (var (shape, list) in perShape.OrderBy(keySelector: static entry => entry.Key)) {
                var track = new CreationRigTrack(
                    at: [.. list.Select(selector: static key => key.At)],
                    clip: rigClip,
                    eases: [.. list.Select(selector: static key => key.Ease)],
                    shape: shape,
                    slides: [.. list.Select(selector: static key => key.Slide)],
                    turns: [.. list.Select(selector: static key => key.Turn)]
                );

                // A track whose every key is the rest pose moves nothing and writes nothing.
                if (!track.Turns && !track.Slides) { continue; }
                (tracks[shape] ??= []).Add(item: track);
            }
        }

        return tracks;
    }
}
