using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>What changes one shape's own pose within its creation (<see cref="CreationRig.Writers"/>).</summary>
[Flags]
public enum CreationRigWriters : byte {
    /// <summary>Nothing: the shape rides its parent, or the root.</summary>
    None = 0,
    /// <summary>A swing or a slide a driver plays (<see cref="CreationRigTerm"/>).</summary>
    Term = 1,
    /// <summary>An effector's solve: the shape is a bone of its chain.</summary>
    Solve = 2,
    /// <summary>An effector's align: the shape is the tip of an effector that aligns it, whether or not it is a
    /// bone.</summary>
    Align = 4,
    /// <summary>A timeline frame poses the shape away from its authored pose.</summary>
    Frame = 8,
    /// <summary>A look's part follower (<c>partDynamics</c>) moves the shape (<see cref="CreationRig.Follow"/>).</summary>
    Follower = 16,
}
/// <summary>Which motion a <see cref="CreationRigTerm"/> plays.</summary>
public enum CreationRigTermKind : byte {
    /// <summary>A turn about a pivot: an authored swing, or a clip's turn about the shape's joint.</summary>
    Turn,
    /// <summary>A translation: an authored slide along an axis, or a clip's keyed slide.</summary>
    Slide,
}
/// <summary>What a pose reads from outside its rig (<see cref="CreationRigRead"/>).</summary>
public enum CreationRigReadKind : byte {
    /// <summary>A state cell, by <see cref="CreationRigRead.Reference"/>, as stored truth or eased.</summary>
    State,
    /// <summary>The body's simulation facts, which a gate's fact token tests.</summary>
    Facts,
    /// <summary>The body's rendered motion: its speed, travel, turn and the <c>moving</c>/<c>still</c> tokens.</summary>
    Motion,
    /// <summary>Elapsed presentation time.</summary>
    Time,
    /// <summary>A surface probe of the presentation query field.</summary>
    Probe,
    /// <summary>Another body's root pose, by <see cref="CreationRigRead.Body"/>.</summary>
    Body,
    /// <summary>One of the definition's <c>curves</c> rows, by <see cref="CreationRigRead.Reference"/>.</summary>
    Curve,
}
/// <summary>
/// One thing a creation's pose reads from outside its rig, compiled once (<see cref="CreationRig.Reads"/>). Only a rig
/// makes one, so a reader handed a read can only hold one its rig lists: the pose pass reads through a view that
/// answers these and nothing else.
/// </summary>
public sealed class CreationRigRead {
    internal CreationRigRead(int index, CreationRigReadKind kind, string? reference, bool truth, int body) {
        Index = index;
        Kind = kind;
        Reference = reference;
        Truth = truth;
        Body = body;
    }

    /// <summary>Gets the read's position in <see cref="CreationRig.Reads"/>.</summary>
    public int Index { get; }
    /// <summary>Gets what is read.</summary>
    public CreationRigReadKind Kind { get; }
    /// <summary>Gets the state reference (<see cref="CreationRigReadKind.State"/>) or the curve row's name
    /// (<see cref="CreationRigReadKind.Curve"/>), else <see langword="null"/>.</summary>
    public string? Reference { get; }
    /// <summary>Gets whether a state read is of the stored truth rather than the eased follower.</summary>
    public bool Truth { get; }
    /// <summary>Gets the population index of a <see cref="CreationRigReadKind.Body"/> read, else -1.</summary>
    public int Body { get; }

    /// <inheritdoc/>
    public override string ToString() => Kind switch {
        CreationRigReadKind.State => $"state {Reference}{(Truth ? " (truth)" : string.Empty)}",
        CreationRigReadKind.Body => $"body {Body}",
        CreationRigReadKind.Curve => $"curve {Reference}",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}
/// <summary>What one gate token tests (<see cref="CreationRigGateToken"/>).</summary>
public enum CreationRigGateTokenKind : byte {
    /// <summary>A body fact (or <c>always</c>), by name, which the consuming side resolves.</summary>
    Fact,
    /// <summary>The body's eased speed is above the moving threshold.</summary>
    Moving,
    /// <summary>The body's eased speed is not above the moving threshold.</summary>
    Still,
    /// <summary>A state cell's stored truth is nonzero.</summary>
    State,
}
/// <summary>One compiled gate token.</summary>
/// <param name="Kind">What it tests.</param>
/// <param name="Name">The authored token.</param>
/// <param name="Read">The state read a <see cref="CreationRigGateTokenKind.State"/> token tests, else
/// <see langword="null"/>.</param>
public readonly record struct CreationRigGateToken(CreationRigGateTokenKind Kind, string Name, CreationRigRead? Read);
/// <summary>One driver as its rig plays it: the authored row, what it reads and the earlier drivers it rides.</summary>
public sealed class CreationRigDriver {
    internal CreationRigDriver(int index, CreationDriverDocument document, CreationRigRead? signal, IReadOnlyList<CreationRigGateToken> gate, int source, int cadenceBy, int weightBy, CreationRigRead? cadenceCurve, CreationRigRead? weightCurve) {
        Index = index;
        Document = document;
        Signal = signal;
        Gate = gate;
        Source = source;
        CadenceBy = cadenceBy;
        WeightBy = weightBy;
        CadenceCurve = cadenceCurve;
        WeightCurve = weightCurve;
    }

    /// <summary>Gets the driver's position in <see cref="CreationDocument.Drivers"/>.</summary>
    public int Index { get; }
    /// <summary>Gets the authored row.</summary>
    public CreationDriverDocument Document { get; }
    /// <summary>Gets the state read a state-cell signal reads, else <see langword="null"/>.</summary>
    public CreationRigRead? Signal { get; }
    /// <summary>Gets the compiled gate, empty for an ungated driver.</summary>
    public IReadOnlyList<CreationRigGateToken> Gate { get; }
    /// <summary>Gets the earlier driver a <c>driver.&lt;name&gt;</c> signal rides, or -1.</summary>
    public int Source { get; }
    /// <summary>Gets the earlier driver <see cref="CreationDriverDocument.CadenceBy"/> reads, or -1.</summary>
    public int CadenceBy { get; }
    /// <summary>Gets the earlier driver <see cref="CreationDriverDocument.WeightBy"/> reads, or -1.</summary>
    public int WeightBy { get; }
    /// <summary>Gets the curve row <see cref="CreationDriverDocument.CadenceBy"/> reads, else <see langword="null"/>.</summary>
    public CreationRigRead? CadenceCurve { get; }
    /// <summary>Gets the curve row <see cref="CreationDriverDocument.WeightBy"/> reads, else <see langword="null"/>.</summary>
    public CreationRigRead? WeightCurve { get; }
    /// <summary>Gets whether the driver replaces (<see cref="CreationDriverDocument.ModeReplace"/>): it scales every driver
    /// declared before it by one minus its weight on the shapes of its <see cref="Mask"/>.</summary>
    public bool Replaces => (Document.Mode is CreationDriverDocument.ModeReplace);
    /// <summary>Gets the shapes the driver's layer covers, ascending: those its parts cover, or, with no parts, those its
    /// own terms pose. Its terms play only here.</summary>
    public IReadOnlyList<int> Mask { get; internal set; } = [];
}
/// <summary>A motion a driver authors on a shape its mask does not cover, which never plays.</summary>
/// <param name="Driver">The driver's index.</param>
/// <param name="Shape">The shape's index.</param>
/// <param name="Member">The authored member: <c>swings[i]</c>, <c>slides[i]</c> or <c>clip 'name'</c>.</param>
public readonly record struct CreationRigUnplayed(int Driver, int Shape, string Member);
/// <summary>
/// One motion a driver plays on one shape, in the order the shape composes them: its swings as authored, then its clips'
/// turns in the order the clips are listed, then its slides and its clips' keyed slides. A term names its driver by index
/// and carries the authored values, which may be state bound, so it is read live where it is played; a clip's term
/// carries its track (<see cref="Track"/>), whose keys are literal.
/// </summary>
public sealed class CreationRigTerm {
    internal CreationRigTerm(CreationRigTermKind kind, int shape, int source, string driver, int driverIndex, DocumentVector3 axis, DocumentVector3? pivot, DocumentScalar amplitude, DocumentScalar? phase, string? wave, CreationRigTrack? track = null) {
        Kind = kind;
        Shape = shape;
        Source = source;
        Driver = driver;
        DriverIndex = driverIndex;
        Axis = axis;
        Pivot = pivot;
        Amplitude = amplitude;
        Phase = phase;
        Wave = wave;
        Track = track;
    }

    /// <summary>Gets which motion the term plays.</summary>
    public CreationRigTermKind Kind { get; }
    /// <summary>Gets the shape it moves.</summary>
    public int Shape { get; }
    /// <summary>Gets its position in the shape's authored swings (a turn) or slides (a slide), or the clip's position in
    /// <see cref="CreationDocument.Clips"/> for a clip's term, for a diagnostic that names the authored member.</summary>
    public int Source { get; }
    /// <summary>Gets the name of the driver that plays it.</summary>
    public string Driver { get; }
    /// <summary>Gets the index of the driver that plays it, or -1 when no driver carries the name.</summary>
    public int DriverIndex { get; }
    /// <summary>Gets the turn's axis or the slide's direction; zero for a clip's term, whose track carries its own.</summary>
    public DocumentVector3 Axis { get; }
    /// <summary>Gets the turn's pivot (a clip's turn is about the shape's joint, <see cref="CreationRig.Joint"/>), or
    /// <see langword="null"/> for a slide.</summary>
    public DocumentVector3? Pivot { get; }
    /// <summary>Gets the peak angle (radians) or offset (creation units).</summary>
    public DocumentScalar Amplitude { get; }
    /// <summary>Gets the phase offset added to the driver's phase, or <see langword="null"/> for none.</summary>
    public DocumentScalar? Phase { get; }
    /// <summary>Gets the waveform's name (<see cref="CreationWave"/>), or <see langword="null"/> for a sine and for a
    /// clip's term.</summary>
    public string? Wave { get; }
    /// <summary>Gets the clip track a clip's term plays, or <see langword="null"/> for a swing or a slide.</summary>
    public CreationRigTrack? Track { get; }
    /// <summary>Gets whether the term's largest motion at full share follows from the document alone: literal values
    /// and a waveform bounded by one, or a clip's literal keys about a literal joint.</summary>
    public bool Bounded => ((Track is not null)
        ? (Pivot?.Reference is null)
        : ((Amplitude.Reference is null) && (Axis.Reference is null) && (Pivot?.Reference is null) && BoundedWave(wave: Wave)));
    /// <summary>Gets the largest turn (radians, at most a half turn) or slide (creation units) the term produces at full
    /// share: a clip's largest key; infinity when it is not <see cref="Bounded"/>.</summary>
    public float Limit => (!Bounded
        ? float.PositiveInfinity
        : (Track is { } track)
            ? ((Kind == CreationRigTermKind.Turn) ? track.TurnLimit : track.SlideLimit)
            : ((Kind == CreationRigTermKind.Turn) ? MathF.Min(x: MathF.Abs(x: Amplitude.Value), y: MathF.PI) : MathF.Abs(x: Amplitude.Value)));

    // Curve waveforms require the containing world's curve, absent from a creation-only proof; linear is unbounded.
    // Driver weights, including weightBy, are clamped to [0, 1] where they are formed.
    internal static bool BoundedWave(string? wave) => (wave is null or CreationWave.Sine or CreationWave.HalfSine or CreationWave.Constant);
}
/// <summary>One effector as its rig plays it: the shapes it writes, resolved once, and what it reads.</summary>
public sealed class CreationRigEffector {
    internal CreationRigEffector(int index, CreationEffectorDocument document, int[] bones, int tip, IReadOnlyList<CreationRigGateToken> gate, CreationRigRead? target, int plantDriver) {
        Index = index;
        Document = document;
        Bones = bones;
        Tip = tip;
        Gate = gate;
        Target = target;
        PlantDriver = plantDriver;
    }

    /// <summary>Gets the effector's position in <see cref="CreationDocument.Effectors"/>.</summary>
    public int Index { get; }
    /// <summary>Gets the authored row.</summary>
    public CreationEffectorDocument Document { get; }
    /// <summary>Gets its chain's bones, root to tip, as shape indices; empty when a bone or the tip names no shape or
    /// the chain is shorter than <see cref="CreationEffectorDocument.MinChainBones"/>, which leaves it inert.</summary>
    public IReadOnlyList<int> Bones { get; }
    /// <summary>Gets its tip's shape index, or -1 when it is inert.</summary>
    public int Tip { get; }
    /// <summary>Gets whether the effector turns its tip (<see cref="CreationEffectorDocument.Align"/> above zero).</summary>
    public bool Aligns => ((Tip >= 0) && ((Document.Align ?? 0f) > 0f));
    /// <summary>Gets its compiled gate, empty when ungated.</summary>
    public IReadOnlyList<CreationRigGateToken> Gate { get; }
    /// <summary>Gets what its target reads: a probe, another body, or a state cell holding a point.</summary>
    public CreationRigRead? Target { get; }
    /// <summary>Gets the driver its plant window reads, or -1 for none.</summary>
    public int PlantDriver { get; }
}
/// <summary>
/// A creation's rig, compiled once: every writer of each shape's pose (the terms its drivers play, effector solves, an
/// effector's aligned tip, timeline frames and, for a look, part followers), the joint each shape turns about, what the
/// pose reads from outside, and how far the rig can carry any shape from the root.
/// <para>
/// This is the one door motion is read through. <see cref="Document"/> is the creation with its swings and slides
/// removed, and the motion classifiers (<see cref="CreationMotion"/>, <see cref="CreationMotionPlan"/>) take a rig, so a
/// reader that asks the document for a shape's motion finds none and cannot be handed a document that was never
/// compiled: <see cref="Compile"/> refuses a document a rig lowered.
/// </para>
/// </summary>
public sealed class CreationRig {
    private static readonly ConditionalWeakTable<CreationDocument, CreationRig> Compiled = new();

    private readonly CreationRigWriters[] m_writers;
    private readonly int[] m_parents;
    private readonly CreationRigTerm[] m_terms;
    private readonly int[] m_termStarts;
    private readonly DocumentVector3[] m_joints;
    private readonly int[] m_replacers;
    private readonly int[] m_replacerStarts;
    private readonly IReadOnlyDictionary<string, string>? m_followed;
    private int[]? m_owners;
    private float? m_reach;
    private string? m_reachRefusal;
    private readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, CreationRig> m_follows = new();

    private CreationRig(CreationDocument document, CreationRigWriters[] writers, int[] parents, CreationRigTerm[] terms, int[] termStarts, DocumentVector3[] joints, IReadOnlyList<CreationRigDriver> drivers, IReadOnlyList<CreationRigEffector> effectors, IReadOnlyList<CreationRigClip> clips, IReadOnlyList<CreationRigRead> reads, CreationRigRead? sizeBy, bool animates, IReadOnlyDictionary<string, string>? followed, int[] replacers, int[] replacerStarts, IReadOnlyList<CreationRigUnplayed> unplayed) {
        Document = document;
        Clips = clips;
        m_replacers = replacers;
        m_replacerStarts = replacerStarts;
        Unplayed = unplayed;
        m_writers = writers;
        m_parents = parents;
        m_terms = terms;
        m_termStarts = termStarts;
        m_joints = joints;
        Drivers = drivers;
        Effectors = effectors;
        Reads = reads;
        SizeBy = sizeBy;
        Animates = animates;
        m_followed = followed;
    }

    /// <summary>Gets the creation as every renderer, collider and anchor reads it: the compiled document with every
    /// shape's swings and slides and the creation's clips removed, because the rig plays them.</summary>
    public CreationDocument Document { get; }
    /// <summary>Gets the creation's drivers, in declaration order.</summary>
    public IReadOnlyList<CreationRigDriver> Drivers { get; }
    /// <summary>Gets the creation's pose clips that compiled, in declaration order, the order a shape composes their
    /// turns in.</summary>
    public IReadOnlyList<CreationRigClip> Clips { get; }
    /// <summary>Gets the creation's effectors, in declaration order, the order they solve in.</summary>
    public IReadOnlyList<CreationRigEffector> Effectors { get; }
    /// <summary>Gets each motion a driver authors on a shape outside its mask, which never plays.</summary>
    public IReadOnlyList<CreationRigUnplayed> Unplayed { get; }
    /// <summary>Gets everything outside the rig the pose depends on, each read once.</summary>
    public IReadOnlyList<CreationRigRead> Reads { get; }
    /// <summary>Gets the read <see cref="CreationDocument.SizeBy"/> samples, or <see langword="null"/>.</summary>
    public CreationRigRead? SizeBy { get; }
    /// <summary>Gets whether the creation animates at all: a size binding, a driver, an effector, or a frame that moves
    /// a shape.</summary>
    public bool Animates { get; }
    /// <summary>Gets the number of shapes.</summary>
    public int ShapeCount => m_writers.Length;
    /// <summary>Gets each shape's parent index, -1 for one on the creation root.</summary>
    public ReadOnlySpan<int> Parents => m_parents;
    /// <summary>Gets the most terms any one shape carries.</summary>
    public int MostTerms {
        get {
            var most = 0;

            for (var shape = 0; (shape < ShapeCount); shape++) { most = Math.Max(val1: most, val2: (m_termStarts[(shape + 1)] - m_termStarts[shape])); }
            return most;
        }
    }
    /// <summary>Gets whether any shape moves within the creation's frame, so it needs a rig of its own: some shape has
    /// a writer.</summary>
    public bool HasPartMotion => m_writers.Any(predicate: static writers => (writers != CreationRigWriters.None));
    /// <summary>Gets how far from the root, in creation units, the rig can carry any point of the creation's geometry:
    /// the rest reach (<see cref="CreationGeometry.Reach"/>) or, for a moving shape, the length of the path from the
    /// root through each pivot that turns it, in order, to its farthest point, plus the limits of the slides on the
    /// way. A turn keeps every point's distance to its pivot, so this holds for swings, solves and an aligned tip at any
    /// angle. Infinity when a slide has no finite limit, a pivot is state bound, a frame pose is state bound, or a
    /// follower moves a shape.</summary>
    public float Reach => (m_reach ??= MeasureReach());
    /// <summary>Gets why <see cref="Reach"/> is infinite, naming the shape and the writer, or <see langword="null"/>
    /// when it is finite.</summary>
    public string? ReachRefusal {
        get {
            _ = Reach;

            return m_reachRefusal;
        }
    }

    /// <summary>Returns what writes a shape's own pose.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns>The writers; <see cref="CreationRigWriters.None"/> for a shape that only rides its parent.</returns>
    public CreationRigWriters Writers(int shape) => m_writers[shape];
    /// <summary>Returns the terms a shape composes, in order: its swings as authored, then its clips' turns, then its
    /// slides and its clips' keyed slides.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns>The terms.</returns>
    public ReadOnlySpan<CreationRigTerm> Terms(int shape) => m_terms.AsSpan(start: m_termStarts[shape], length: (m_termStarts[(shape + 1)] - m_termStarts[shape]));
    /// <summary>Gets every term of every shape, shape by shape in shape order.</summary>
    public ReadOnlySpan<CreationRigTerm> AllTerms => m_terms;
    /// <summary>Returns the point a shape turns about in rest creation space: its authored
    /// <see cref="ShapeDocument.Joint"/>, else the pivot of its first swing, else its own position. An effector's solve
    /// turns a bone about it.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns>The joint, read when asked, so a joint bound to state reads its cell's current value.</returns>
    public Vector3 Joint(int shape) => m_joints[shape].Value;
    /// <summary>Returns the shape whose motion a shape inherits: the nearest of the shape and its parents that has a
    /// writer, or -1 when only the creation root moves it. Two shapes of one owner keep their places relative to each
    /// other under every writer.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns>The owner's index, or -1 for the creation root.</returns>
    public int Owner(int shape) => Owners[shape];
    /// <summary>Gets each shape's owner (<see cref="Owner"/>).</summary>
    public ReadOnlySpan<int> Owners => (m_owners ??= ResolveOwners());

    /// <summary>Returns the replacing drivers whose masks cover a shape, in declaration order.</summary>
    /// <param name="shape">The shape's index.</param>
    /// <returns>The drivers' indices.</returns>
    public ReadOnlySpan<int> Replacers(int shape) => m_replacers.AsSpan(start: m_replacerStarts[shape], length: (m_replacerStarts[(shape + 1)] - m_replacerStarts[shape]));
    /// <summary>Returns a driver's share on a shape: its weight times one minus the weight of each replacing driver
    /// declared after it whose mask covers the shape. Every term the driver plays on the shape scales by it, so
    /// declaration order decides shares and nothing else, and a replacing driver at weight zero leaves every share
    /// exactly as it was. This is the one place a share is formed.</summary>
    /// <param name="driver">The driver's index.</param>
    /// <param name="shape">The shape's index.</param>
    /// <param name="weights">The per-driver eased weights, each in [0, 1].</param>
    /// <returns>The share in [0, 1]; zero for an index past <paramref name="weights"/>.</returns>
    public float Share(int driver, int shape, ReadOnlySpan<float> weights) {
        if (((uint)driver) >= ((uint)weights.Length)) { return 0f; }

        var share = weights[driver];

        if (share == 0f) { return 0f; }
        foreach (var replacer in Replacers(shape: shape)) {
            if ((replacer > driver) && (((uint)replacer) < ((uint)weights.Length))) { share *= (1f - weights[replacer]); }
        }

        return share;
    }

    /// <summary>Returns the rig a look plays: this rig with each shape a part follower moves
    /// (<c>partDynamics</c>, keyed by part id) counted as a writer.</summary>
    /// <param name="partDynamics">The look's followers, by part id, or <see langword="null"/> for none.</param>
    /// <returns>This rig when the look follows no part this creation publishes; otherwise the followed rig, the same
    /// instance for the same map.</returns>
    public CreationRig Follow(IReadOnlyDictionary<string, string>? partDynamics) {
        if ((partDynamics is not { Count: > 0 }) || ReferenceEquals(objA: partDynamics, objB: m_followed)) { return this; }
        if (m_followed is not null) { throw new InvalidOperationException(message: "A followed rig is followed from the creation's own rig."); }

        return m_follows.GetValue(key: partDynamics, createValueCallback: map => {
            var parts = CreationPartCompiler.Compile(document: Document);
            var writers = (CreationRigWriters[])m_writers.Clone();
            var any = false;

            foreach (var name in map.Keys) {
                if (parts.TryResolve(partId: name, transformSlot: out var shape) && (((uint)shape) < ((uint)writers.Length))) {
                    writers[shape] |= CreationRigWriters.Follower;
                    any = true;
                }
            }

            return (any ? new CreationRig(animates: Animates, clips: Clips, document: Document, drivers: Drivers, effectors: Effectors, followed: map, joints: m_joints, parents: m_parents, reads: Reads, replacerStarts: m_replacerStarts, replacers: m_replacers, sizeBy: SizeBy, termStarts: m_termStarts, terms: m_terms, unplayed: Unplayed, writers: writers) : this);
        });
    }
    /// <summary>Returns the rig of a document, compiled once per document instance.</summary>
    /// <param name="document">The creation, in either frame, normalized or not.</param>
    /// <returns>The rig.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="document"/> is a rig's <see cref="Document"/>, which carries no
    /// motion.</exception>
    public static CreationRig Of(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return Compiled.GetValue(key: document, createValueCallback: Compile);
    }
    /// <summary>Compiles a creation's rig.</summary>
    /// <param name="document">The creation, in either frame, normalized or not; a reference that names nothing compiles to
    /// a writer that moves nothing, as the client plays it.</param>
    /// <returns>The rig.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="document"/> is a rig's <see cref="Document"/>, which carries no
    /// motion: a classifier handed it would see a creation that moves nothing.</exception>
    public static CreationRig Compile(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        if (document.Lowered) {
            throw new ArgumentException(message: "This document is a rig's lowered document and carries no motion; read the rig it came from (CreationRig.Document's owner, such as WorldPrototype.Rig).", paramName: nameof(document));
        }

        var shapes = (document.Shapes ?? []);
        var parents = new int[shapes.Count];

        CreationMotion.ResolveParents(parents: parents, shapes: shapes);

        var reads = new ReadTable();
        var driverRows = (document.Drivers ?? []);
        var driverIndices = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < driverRows.Count); index++) { _ = driverIndices.TryAdd(key: driverRows[index].Name, value: index); }

        int DriverBefore(string? name, int before) => (((name is not null) && driverIndices.TryGetValue(key: name, value: out var found) && (found < before)) ? found : -1);

        var drivers = new CreationRigDriver[driverRows.Count];

        for (var index = 0; (index < driverRows.Count); index++) {
            var row = driverRows[index];
            CreationRigRead? signal = null;
            var source = -1;

            if (CreationDriverDocument.IsStateSignal(signal: row.Signal)) {
                signal = reads.State(reference: row.Signal, truth: false);
            } else if (CreationDriverDocument.TryDriverSignal(name: out var named, signal: row.Signal)) {
                source = DriverBefore(name: named, before: index);
            } else if (row.Signal is CreationDriverDocument.SignalTime) {
                _ = reads.Add(kind: CreationRigReadKind.Time);
            } else {
                _ = reads.Add(kind: CreationRigReadKind.Motion);
            }

            drivers[index] = new CreationRigDriver(
                cadenceBy: DriverBefore(name: row.CadenceBy?.Driver, before: index),
                cadenceCurve: ((row.CadenceBy is { } cadence) ? reads.Curve(name: cadence.Curve) : null),
                document: row,
                gate: CompileGate(gate: row.When, reads: reads),
                index: index,
                signal: signal,
                source: source,
                weightBy: DriverBefore(name: row.WeightBy?.Driver, before: index),
                weightCurve: ((row.WeightBy is { } weight) ? reads.Curve(name: weight.Curve) : null)
            );
        }

        // A driver with parts plays only on the shapes they cover; a part that does not resolve covers nothing.
        var parts = (driverRows.Any(predicate: static row => (row.Parts is not null)) ? CreationPartCompiler.Resolve(document: document, refuse: static (_, _) => { }) : null);
        var explicitMasks = new bool[driverRows.Count][];

        for (var index = 0; (index < driverRows.Count); index++) {
            if ((driverRows[index].Parts is not { } mask) || (parts is null)) { continue; }
            var covered = new bool[shapes.Count];

            foreach (var part in mask) {
                if ((part is not null) && parts.TryCover(partId: part, shapes: out var cover)) {
                    foreach (var shape in cover) { covered[shape] = true; }
                }
            }
            explicitMasks[index] = covered;
        }

        var unplayed = new List<CreationRigUnplayed>();
        var poses = new bool[driverRows.Count][];

        // Whether a driver's motion on a shape plays: its mask covers the shape. A motion that does not is recorded.
        bool Plays(int driver, int shape, string member) {
            if (((uint)driver) < ((uint)explicitMasks.Length) && (explicitMasks[driver] is { } mask) && !mask[shape]) {
                unplayed.Add(item: new CreationRigUnplayed(Driver: driver, Member: member, Shape: shape));

                return false;
            }
            if (((uint)driver) < ((uint)poses.Length)) { (poses[driver] ??= new bool[shapes.Count])[shape] = true; }

            return true;
        }

        var lookup = new CreationShapeLookup(shapes: shapes);
        var clips = new List<CreationRigClip>();
        // A refused clip, key or pose plays nothing; validation reports it through the same compiler.
        var tracks = CreationClipCompiler.Compile(compiled: clips, document: document, lookup: lookup, refuse: static (_, _) => { });
        var writers = new CreationRigWriters[shapes.Count];
        var terms = new List<CreationRigTerm>();
        var termStarts = new int[(shapes.Count + 1)];
        var joints = new DocumentVector3[shapes.Count];
        var lowered = new ShapeDocument[shapes.Count];
        var anyMotion = false;

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var swings = (shape.Swings ?? []);
            var slides = (shape.Slides ?? []);

            var shapeTracks = (tracks?[index] ?? []);

            // The joint comes first: a shape authors one only to say where it turns.
            joints[index] = (shape.Joint ?? ((swings.Count > 0) ? swings[0].Pivot : shape.Position));
            termStarts[index] = terms.Count;
            for (var source = 0; (source < swings.Count); source++) {
                var swing = swings[source];

                if (!Plays(driver: DriverBefore(name: swing.Driver, before: int.MaxValue), member: $"swings[{source}]", shape: index)) { continue; }
                terms.Add(item: new CreationRigTerm(amplitude: swing.Amplitude, axis: swing.Axis, driver: swing.Driver, driverIndex: DriverBefore(name: swing.Driver, before: int.MaxValue), kind: CreationRigTermKind.Turn, phase: swing.Phase, pivot: swing.Pivot, shape: index, source: source, wave: swing.Wave));
                CurveOfWave(reads: reads, wave: swing.Wave);
            }
            foreach (var track in shapeTracks) {
                if (!Plays(driver: track.Clip.Driver, member: $"clip '{track.Clip.Document.Name}'", shape: index)) { continue; }
                if (track.Turns) { terms.Add(item: ClipTerm(joint: joints[index], kind: CreationRigTermKind.Turn, shape: index, track: track)); }
            }
            for (var source = 0; (source < slides.Count); source++) {
                var slide = slides[source];

                if (!Plays(driver: DriverBefore(name: slide.Driver, before: int.MaxValue), member: $"slides[{source}]", shape: index)) { continue; }
                terms.Add(item: new CreationRigTerm(amplitude: slide.Amplitude, axis: slide.Axis, driver: slide.Driver, driverIndex: DriverBefore(name: slide.Driver, before: int.MaxValue), kind: CreationRigTermKind.Slide, phase: slide.Phase, pivot: null, shape: index, source: source, wave: slide.Wave));
                CurveOfWave(reads: reads, wave: slide.Wave);
            }
            foreach (var track in shapeTracks) {
                if (track.Slides && ((explicitMasks[track.Clip.Driver] is not { } mask) || mask[index])) { terms.Add(item: ClipTerm(joint: null, kind: CreationRigTermKind.Slide, shape: index, track: track)); }
            }
            if ((swings.Count + slides.Count) > 0) {
                anyMotion = true;
                lowered[index] = (shape with { Slides = null, Swings = null });
            } else {
                lowered[index] = shape;
            }
            if (terms.Count > termStarts[index]) { writers[index] |= CreationRigWriters.Term; }
            if (FramesMove(document: document, shape: shape)) { writers[index] |= CreationRigWriters.Frame; }
        }
        termStarts[shapes.Count] = terms.Count;

        // Each driver's mask, and each shape's replacing drivers in declaration order.
        var replacerLists = new List<int>[shapes.Count];

        for (var index = 0; (index < driverRows.Count); index++) {
            var covered = (explicitMasks[index] ?? poses[index]);
            var mask = new List<int>();

            for (var shape = 0; ((covered is not null) && (shape < shapes.Count)); shape++) {
                if (!covered[shape]) { continue; }
                mask.Add(item: shape);
                if (drivers[index].Replaces) { (replacerLists[shape] ??= []).Add(item: index); }
            }
            drivers[index].Mask = mask;
        }

        var replacerStarts = new int[(shapes.Count + 1)];
        var replacers = new List<int>();

        for (var shape = 0; (shape < shapes.Count); shape++) {
            replacerStarts[shape] = replacers.Count;
            if (replacerLists[shape] is { } list) { replacers.AddRange(collection: list); }
        }
        replacerStarts[shapes.Count] = replacers.Count;

        var effectorRows = (document.Effectors ?? []);
        var effectors = new CreationRigEffector[effectorRows.Count];

        for (var index = 0; (index < effectorRows.Count); index++) {
            var row = effectorRows[index];
            var chain = (row.Chain ?? []);
            var tip = lookup.Find(name: row.Tip);
            var bones = new int[chain.Count];
            var resolved = ((tip >= 0) && (chain.Count >= CreationEffectorDocument.MinChainBones) && (chain.Count <= CreationEffectorDocument.MaxChainBones));

            for (var bone = 0; (resolved && (bone < chain.Count)); bone++) {
                bones[bone] = lookup.Find(name: chain[bone]);
                resolved = (bones[bone] >= 0);
            }

            var target = row.Target?.Kind switch {
                CreationEffectorTargetDocument.KindBody => reads.Body(index: (row.Target.Index ?? -1)),
                CreationEffectorTargetDocument.KindState when (row.Target.Reference is { } reference) => reads.State(reference: reference, truth: false),
                null => (CreationRigRead?)null,
                _ => reads.Add(kind: CreationRigReadKind.Probe),
            };

            effectors[index] = new CreationRigEffector(
                bones: (resolved ? bones : []),
                document: row,
                gate: CompileGate(gate: row.When, reads: reads),
                index: index,
                plantDriver: DriverBefore(name: row.Plant?.Driver, before: int.MaxValue),
                target: target,
                tip: (resolved ? tip : -1)
            );
            if (!resolved) { continue; }
            foreach (var bone in bones) { writers[bone] |= CreationRigWriters.Solve; }
            if (effectors[index].Aligns) { writers[tip] |= CreationRigWriters.Align; }
        }

        var sizeBy = ((document.SizeBy is { } size) ? reads.State(reference: size, truth: true) : null);
        var animates = ((document.SizeBy is not null) || (driverRows.Count > 0) || (effectorRows.Count > 0) || writers.Any(predicate: static writer => ((writer & CreationRigWriters.Frame) != 0)));
        var lowerDocument = (anyMotion
            ? (document with { Clips = null, Shapes = lowered, Lowered = true })
            : (document with { Clips = null, Lowered = true }));

        return new CreationRig(animates: animates, clips: clips, document: lowerDocument, drivers: drivers, effectors: effectors, followed: null, joints: joints, parents: parents, reads: reads.Reads, replacerStarts: replacerStarts, replacers: [.. replacers], sizeBy: sizeBy, termStarts: termStarts, terms: [.. terms], unplayed: unplayed, writers: writers);
    }

    // A clip's turn about the shape's joint, or its keyed slide, played by the clip's driver.
    private static CreationRigTerm ClipTerm(DocumentVector3? joint, CreationRigTermKind kind, int shape, CreationRigTrack track) => new(
        amplitude: ((kind == CreationRigTermKind.Turn) ? track.TurnLimit : track.SlideLimit),
        axis: Vector3.Zero,
        driver: track.Clip.Document.Driver,
        driverIndex: track.Clip.Driver,
        kind: kind,
        phase: null,
        pivot: joint,
        shape: shape,
        source: track.Clip.Index,
        track: track,
        wave: null
    );
    // Each shape's owner: the nearest of the shape and its parents with a writer.
    private int[] ResolveOwners() {
        var owners = new int[ShapeCount];

        for (var shape = 0; (shape < ShapeCount); shape++) {
            var at = shape;

            while ((at >= 0) && (m_writers[at] == CreationRigWriters.None)) { at = m_parents[at]; }
            owners[shape] = at;
        }

        return owners;
    }
    // The reach Reach describes. A shape's own motion composes first and its parent's after it, each in rest creation
    // space: its swings in order, then its effector correction about its joint, all about fixed rest pivots. Read from the
    // outermost motion in, a point's distance to the root is at most the root's distance to the outermost pivot, plus
    // each pivot's distance to the next, plus the innermost pivot's distance to the point (each turn keeps the distance
    // to its own pivot), plus every slide's limit. An aligned tip turns about its own origin, which no other motion of
    // the tip's own moves, so its rest origin closes the path.
    private float MeasureReach() {
        var shapes = (Document.Shapes ?? []);

        float Unbounded(int shape, string why) {
            m_reachRefusal = $"shape '{CreationMotion.NameOf(document: Document, index: shape)}' {why}";

            return float.PositiveInfinity;
        }

        for (var shape = 0; (shape < shapes.Count); shape++) {
            if (shapes[shape].Position.Reference is not null) { return Unbounded(shape: shape, why: "has a state-bound position"); }
        }

        var reach = CreationGeometry.Reach(document: Document);

        if (!m_writers.Any(predicate: static writers => (writers != CreationRigWriters.None))) { return reach; }
        var pivots = new List<Vector3>();

        for (var shape = 0; (shape < shapes.Count); shape++) {
            if ((Owner(shape: shape) < 0) || ((shapes[shape].Blend is { } blend) && SdfBoundAlgebra.Carves(blend: blend))) { continue; }
            pivots.Clear();
            var slides = 0f;

            // The pivots from the innermost (the shape's own first motion) out to the root's last.
            for (var at = shape; (at >= 0); at = m_parents[at]) {
                var writers = m_writers[at];

                if ((writers & CreationRigWriters.Follower) != 0) { return Unbounded(shape: at, why: "is moved by a part follower"); }
                if ((writers & CreationRigWriters.Align) != 0) {
                    if (shapes[at].Position.Reference is not null) { return Unbounded(shape: at, why: "is aligned about a state-bound position"); }
                    pivots.Add(item: shapes[at].Position.Value);
                }
                foreach (var term in Terms(shape: at)) {
                    if (term.Kind == CreationRigTermKind.Turn) {
                        if (term.Pivot!.Reference is not null) { return Unbounded(shape: at, why: $"swings about a state-bound pivot (swings[{term.Source}])"); }
                        pivots.Add(item: term.Pivot.Value);
                    } else {
                        var limit = term.Limit;

                        if (!float.IsFinite(f: limit)) { return Unbounded(shape: at, why: $"slides with no finite limit (slides[{term.Source}])"); }
                        slides += limit;
                    }
                }
                if ((writers & CreationRigWriters.Solve) != 0) {
                    if (m_joints[at].Reference is not null) { return Unbounded(shape: at, why: "solves about a state-bound joint"); }
                    pivots.Add(item: m_joints[at].Value);
                }
            }

            var own = shapes[shape];

            var whole = CreationStampEmitter.ShapeReach(shape: own);
            var (center, radius) = ((own.Domain is { Count: > 0 }) ? (Vector3.Zero, whole) : (own.Position.Value, (whole - own.Position.Length())));
            var farthest = 0f;

            foreach (var pose in PosesOf(shape: own)) {
                if (pose is not { } at) { return Unbounded(shape: shape, why: "has a frame pose bound to state"); }
                var path = 0f;
                var last = (((own.Domain is { Count: > 0 }) ? center : at) );

                for (var index = 0; (index < pivots.Count); index++) {
                    path += Vector3.Distance(value1: last, value2: pivots[index]);
                    last = pivots[index];
                }
                farthest = MathF.Max(x: farthest, y: (path + last.Length()));
            }
            reach = MathF.Max(x: reach, y: ((farthest + radius) + slides));
        }

        return reach;
    }
    // A shape's rest position and each frame's position of it; null for a frame pose bound to state.
    private IEnumerable<Vector3?> PosesOf(ShapeDocument shape) {
        yield return shape.Position.Value;
        foreach (var frame in (Document.Frames ?? [])) {
            foreach (var transform in (frame.Transforms ?? [])) {
                if (transform.Id == shape.Id) { yield return ((transform.Position.Reference is null) ? transform.Position.Value : null); }
            }
        }
    }
    private static IReadOnlyList<CreationRigGateToken> CompileGate(IReadOnlyList<string>? gate, ReadTable reads) {
        if (gate is not { Count: > 0 } tokens) { return []; }
        var compiled = new CreationRigGateToken[tokens.Count];

        for (var index = 0; (index < tokens.Count); index++) {
            var token = tokens[index];

            compiled[index] = (CreationDriverDocument.IsStateSignal(signal: token)
                ? new CreationRigGateToken(Kind: CreationRigGateTokenKind.State, Name: token, Read: reads.State(reference: token, truth: true))
                : token switch {
                    CreationDriverDocument.TokenMoving => new CreationRigGateToken(Kind: CreationRigGateTokenKind.Moving, Name: token, Read: reads.Add(kind: CreationRigReadKind.Motion)),
                    CreationDriverDocument.TokenStill => new CreationRigGateToken(Kind: CreationRigGateTokenKind.Still, Name: token, Read: reads.Add(kind: CreationRigReadKind.Motion)),
                    CreationDriverDocument.WhenAlways => new CreationRigGateToken(Kind: CreationRigGateTokenKind.Fact, Name: token, Read: null),
                    _ => new CreationRigGateToken(Kind: CreationRigGateTokenKind.Fact, Name: token, Read: reads.Add(kind: CreationRigReadKind.Facts)),
                });
        }

        return compiled;
    }
    private static void CurveOfWave(ReadTable reads, string? wave) {
        if (CreationWave.TryCurveName(name: out var name, wave: wave)) { _ = reads.Curve(name: name); }
    }
    // Whether a timeline frame poses a shape away from its authored position or rotation.
    private static bool FramesMove(CreationDocument document, ShapeDocument shape) {
        foreach (var frame in (document.Frames ?? [])) {
            foreach (var transform in (frame.Transforms ?? [])) {
                if ((transform.Id == shape.Id) && (!Equals(objA: shape.Position, objB: transform.Position) || !Equals(objA: shape.Rotation, objB: transform.Rotation))) {
                    return true;
                }
            }
        }

        return false;
    }

    // The reads a compile lists, each once.
    private sealed class ReadTable {
        private readonly List<CreationRigRead> m_reads = [];
        private readonly Dictionary<(CreationRigReadKind, string?, bool, int), CreationRigRead> m_index = [];

        public IReadOnlyList<CreationRigRead> Reads => m_reads;

        public CreationRigRead Add(CreationRigReadKind kind, string? reference = null, bool truth = false, int body = -1) {
            var key = (kind, reference, truth, body);

            if (!m_index.TryGetValue(key: key, value: out var read)) {
                read = new CreationRigRead(body: body, index: m_reads.Count, kind: kind, reference: reference, truth: truth);
                m_reads.Add(item: read);
                m_index.Add(key: key, value: read);
            }

            return read;
        }
        public CreationRigRead State(string reference, bool truth) => Add(kind: CreationRigReadKind.State, reference: reference, truth: truth);
        public CreationRigRead Body(int index) => Add(body: index, kind: CreationRigReadKind.Body);
        public CreationRigRead Curve(string name) => Add(kind: CreationRigReadKind.Curve, reference: name);
    }
}
