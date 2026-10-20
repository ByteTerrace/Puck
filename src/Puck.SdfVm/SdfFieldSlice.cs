using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// A square cut through a residency's field on an axis-aligned plane, sampled by the mesh bake's field sampler
/// (<c>sdf-mesh-bake.comp.hlsl</c>, the one interpreter call site) as points lattices with gradients
/// (<see cref="SdfMeshPathBake.Probe"/>), and drawn on the CPU as the standard distance diagnostic: iso-distance bands,
/// the sign, the zero line, and the gradient magnitude beside them, one for a field that is the distance.
/// <para>The slice samples the program twice at every point, once with every instance in the walk's mask and once with
/// only the segments no instance owns, as the bake meshes them, and keeps the smaller: the union of the two. Every
/// transform is the frame's own, so a body slices where it stands. Outside an instance's influence sphere the walk
/// answers that sphere's bound, not the shape's distance, which the bands show as circles about it.</para>
/// <para>The cut's pixel <c>(column, row)</c> lies at <c>u = centre.u - half + (column + 1/2)·2·half/resolution</c>,
/// <c>v = centre.v + half - (row + 1/2)·2·half/resolution</c>, where <c>(u, v)</c> is <c>(Z, Y)</c> on an X cut,
/// <c>(X, Z)</c> on a Y cut and <c>(X, Y)</c> on a Z cut: the image's right is +u and its top is +v.</para>
/// </summary>
public sealed class SdfFieldSlice {
    /// <summary>The most samples a side of a slice holds.</summary>
    public const int MaxResolution = 512;
    /// <summary>The fewest samples a side of a slice holds.</summary>
    public const int MinResolution = 16;
    /// <summary>The gradient magnitude's distance from one past which a sample counts as off: two percent.</summary>
    public const float GradientTolerance = 0.02f;
    /// <summary>The gradient magnitude below which a sample outside the near band counts as flat (<see cref="Clipping"/>).</summary>
    public const float ClipGradient = 0.1f;
    /// <summary>The share of the answered samples outside the near band that must read flat before the slice counts as
    /// clipped (<see cref="Clipping"/>).</summary>
    public const float ClipShare = 0.02f;
    /// <summary>The colour, linear RGB, both panels paint a clipped sample: a dull magenta no other reading uses.</summary>
    public static readonly Vector3 ClippedColour = new(x: 0.55f, y: 0.25f, z: 0.55f);
    // The points a lattice samples: five evaluations each fits one reference frame's share of the bake.
    private const int ChunkPoints = (SdfMeshPathBake.FrameTaps / 5);
    // The cell the sampler's gradient taps read a sixty-fourth of: taps a millimetre from the point, independent of the
    // slice's pixel, so a coarse slice measures the gradient as finely as a fine one.
    private const float TapCell = 0.064f;
    // A field value at or past this answers no shape: the walk's far sentinel.
    private const float Far = 1.0e6f;

    private readonly Vector4[] m_points;
    private readonly float[] m_values;
    private readonly Vector3[] m_gradients;
    // Whether a sample's kept value came from the lattice over the unowned segments rather than the one over the instances.
    private readonly bool[] m_world;
    // Per chunk: whether each of its two lattices (every instance, then the unowned segments) answered.
    private readonly bool[] m_answered;
    private readonly bool[] m_inFlight;
    private readonly Action<SdfFieldSlice>? m_completed;

    /// <summary>Initializes a new instance of the <see cref="SdfFieldSlice"/> class over the square of side
    /// <c>2·<paramref name="half"/></c> about <paramref name="centre"/> on the plane <paramref name="axis"/> =
    /// <paramref name="offset"/>.</summary>
    /// <param name="axis">The axis the plane is normal to.</param>
    /// <param name="offset">The plane's coordinate along <paramref name="axis"/>, in metres.</param>
    /// <param name="centre">The square's centre in the plane's <c>(u, v)</c> coordinates, in metres.</param>
    /// <param name="half">The square's half side, in metres; positive and finite.</param>
    /// <param name="resolution">The samples a side; between <see cref="MinResolution"/> and
    /// <see cref="MaxResolution"/>.</param>
    /// <param name="bands">The distance between iso-distance bands, in metres; positive and finite.</param>
    /// <param name="completed">The action the last answer runs, on the thread that answers it, or
    /// <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value lies outside its range, or is not finite.</exception>
    public SdfFieldSlice(SdfAxis axis, float offset, Vector2 centre, float half, int resolution, float bands, Action<SdfFieldSlice>? completed = null) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: (uint)axis, other: (uint)SdfAxis.Z, paramName: nameof(axis));
        if (!float.IsFinite(f: offset)) { throw new ArgumentOutOfRangeException(paramName: nameof(offset)); }
        if (!float.IsFinite(f: centre.X) || !float.IsFinite(f: centre.Y)) { throw new ArgumentOutOfRangeException(paramName: nameof(centre)); }
        if (!(half > 0f) || !float.IsFinite(f: half)) { throw new ArgumentOutOfRangeException(paramName: nameof(half)); }
        if (!(bands > 0f) || !float.IsFinite(f: bands)) { throw new ArgumentOutOfRangeException(paramName: nameof(bands)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(value: resolution, other: MinResolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: resolution, other: MaxResolution);

        Axis = axis;
        Offset = offset;
        Centre = centre;
        Half = half;
        Resolution = resolution;
        Bands = bands;
        m_completed = completed;
        m_points = new Vector4[(resolution * resolution)];
        m_values = new float[m_points.Length];
        m_gradients = new Vector3[m_points.Length];
        m_world = new bool[m_points.Length];
        Chunks = ((m_points.Length + (ChunkPoints - 1)) / ChunkPoints);
        m_answered = new bool[(2 * Chunks)];
        m_inFlight = new bool[(2 * Chunks)];
        Array.Fill(array: m_values, value: float.PositiveInfinity);

        var pixel = ((2f * half) / resolution);

        for (var row = 0; (row < resolution); row++) {
            for (var column = 0; (column < resolution); column++) {
                var u = ((centre.X - half) + ((column + 0.5f) * pixel));
                var v = ((centre.Y + half) - ((row + 0.5f) * pixel));

                m_points[((row * resolution) + column)] = new Vector4(value: PlanePoint(u: u, v: v), w: 0f);
            }
        }
    }

    /// <summary>Gets the axis the plane is normal to.</summary>
    public SdfAxis Axis { get; }
    /// <summary>Gets the distance between iso-distance bands, in metres.</summary>
    public float Bands { get; }
    /// <summary>Gets the square's centre in the plane's <c>(u, v)</c> coordinates, in metres.</summary>
    public Vector2 Centre { get; }
    /// <summary>Gets the lattices of each kind the slice samples in: its points split to fit one reference frame's share
    /// of the bake.</summary>
    public int Chunks { get; }
    /// <summary>Gets whether every lattice answered.</summary>
    public bool Done => Array.TrueForAll(array: m_answered, match: static answered => answered);
    /// <summary>Gets the square's half side, in metres.</summary>
    public float Half { get; }
    /// <summary>Gets the plane's coordinate along <see cref="Axis"/>, in metres.</summary>
    public float Offset { get; }
    /// <summary>Gets the samples a side.</summary>
    public int Resolution { get; }

    /// <summary>Returns the world point at plane coordinates <c>(u, v)</c>.</summary>
    /// <param name="u">The first in-plane coordinate, in metres.</param>
    /// <param name="v">The second in-plane coordinate, in metres.</param>
    /// <returns>The point.</returns>
    public Vector3 PlanePoint(float u, float v) => Axis switch {
        SdfAxis.X => new Vector3(x: Offset, y: v, z: u),
        SdfAxis.Y => new Vector3(x: u, y: Offset, z: v),
        _ => new Vector3(x: u, y: v, z: Offset),
    };
    /// <summary>Returns the next lattice the slice owes and marks it in flight, or <see langword="null"/> when every
    /// lattice is answered or in flight.</summary>
    /// <param name="instances">The program's instance count; zero skips the lattices over every instance.</param>
    /// <param name="worldSelector">The selector of the segments no instance owns.</param>
    /// <param name="ticket">The lattice's ticket, handed back to <see cref="Answer"/> or <see cref="Return"/>.</param>
    /// <returns>The lattice.</returns>
    internal SdfMeshBakeRequest? Take(int instances, int worldSelector, out int ticket) {
        for (var index = 0; (index < m_answered.Length); index++) {
            if (m_answered[index] || m_inFlight[index]) { continue; }
            // The lattice over every instance of a program that holds none answers nothing.
            if (((index & 1) == 0) && (instances == 0)) {
                m_answered[index] = true;
                continue;
            }

            var chunk = (index >> 1);
            var first = (chunk * ChunkPoints);
            var count = Math.Min(ChunkPoints, (m_points.Length - first));

            m_inFlight[index] = true;
            ticket = index;
            return new SdfMeshBakeRequest(
                Cell: new Vector3(value: TapCell),
                Dims: (count, 1, 1),
                Gradients: true,
                Origin: Vector3.Zero,
                Selector: (((index & 1) == 0) ? -1 : worldSelector)
            ) {
                Instances = (((index & 1) == 0) ? instances : 1),
                Points = m_points.AsMemory(start: first, length: count),
            };
        }

        ticket = -1;
        return null;
    }
    /// <summary>Returns a lattice unanswered, as when its dispatch never completed; the slice takes it again.</summary>
    /// <param name="ticket">The ticket <see cref="Take"/> returned.</param>
    internal void Return(int ticket) => m_inFlight[ticket] = false;
    /// <summary>Gets how many times the slice forgot its answers (<see cref="Reset"/>): a lattice taken before the last
    /// reset answers nothing.</summary>
    internal int Generation { get; private set; }
    /// <summary>Forgets every answer, as when the program the slice samples changes.</summary>
    internal void Reset() {
        Generation++;
        Array.Clear(array: m_answered);
        Array.Clear(array: m_inFlight);
        Array.Fill(array: m_values, value: float.PositiveInfinity);
        Array.Clear(array: m_gradients);
        Array.Clear(array: m_world);
    }
    /// <summary>Answers a lattice with its samples, keeping at each point the smaller of its two lattices' values and
    /// that value's gradient.</summary>
    /// <param name="ticket">The ticket <see cref="Take"/> returned.</param>
    /// <param name="samples">The lattice's samples, <see cref="SdfWorldPackage.MeshBakeSampleWords"/> words each.</param>
    /// <returns>Whether the answer completed the slice.</returns>
    internal bool Answer(int ticket, ReadOnlySpan<byte> samples) {
        var first = ((ticket >> 1) * ChunkPoints);
        var count = Math.Min(ChunkPoints, (m_points.Length - first));

        for (var index = 0; (index < count); index++) {
            var record = samples.Slice(start: ((index * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));
            var value = BinaryPrimitives.ReadSingleLittleEndian(source: record);

            if (value < m_values[(first + index)]) {
                m_values[(first + index)] = value;
                m_world[(first + index)] = ((ticket & 1) != 0);
                m_gradients[(first + index)] = new Vector3(
                    x: BinaryPrimitives.ReadSingleLittleEndian(source: record[8..]),
                    y: BinaryPrimitives.ReadSingleLittleEndian(source: record[12..]),
                    z: BinaryPrimitives.ReadSingleLittleEndian(source: record[16..])
                );
            }
        }

        m_inFlight[ticket] = false;
        m_answered[ticket] = true;
        return Done;
    }
    /// <summary>Runs the slice's completion action.</summary>
    internal void Complete() => m_completed?.Invoke(obj: this);
    /// <summary>Gets the field value at a sample, in metres of the field, or positive infinity before it is answered.</summary>
    /// <param name="column">The sample's column, from the left.</param>
    /// <param name="row">The sample's row, from the top.</param>
    /// <returns>The value.</returns>
    public float Value(int column, int row) => m_values[((row * Resolution) + column)];
    /// <summary>Gets the field's gradient at a sample, in field units a metre.</summary>
    /// <param name="column">The sample's column, from the left.</param>
    /// <param name="row">The sample's row, from the top.</param>
    /// <returns>The gradient.</returns>
    public Vector3 Gradient(int column, int row) => m_gradients[((row * Resolution) + column)];

    /// <summary>Gets the half width, in metres of field, of the band about the surface whose samples the near-surface
    /// statistics read: three of the slice's pixels, so the band holds the surface's own neighbourhood whatever the band
    /// spacing.</summary>
    public float NearBand => (3f * ((2f * Half) / Resolution));

    /// <summary>Finds the samples the sampler answered no true field at: outside the surface, a field that has stopped
    /// rising. When at least <see cref="ClipShare"/> of the answered samples outside rise slower than
    /// <see cref="ClipGradient"/>, the slice is clipped from the twentieth percentile of their values (the ceiling) up:
    /// every such sample, and every sample outside at or above the ceiling. A ridge between two shapes, which reads a low
    /// gradient on a few samples, stays under the share; a bound that rises at a tenth of the distance or slower reads as
    /// clipped too.</summary>
    /// <returns>Each sample's clipping, and the ceiling, or positive infinity when nothing is clipped.</returns>
    public (bool[] Clipped, float Ceiling) Clipping() {
        var clipped = new bool[m_values.Length];
        var flat = new List<float>();
        var outside = 0;

        for (var index = 0; (index < m_values.Length); index++) {
            if (!(m_values[index] < Far) || !(m_values[index] > 0f)) { continue; }
            outside++;
            if (m_gradients[index].Length() < ClipGradient) {
                flat.Add(item: m_values[index]);
                clipped[index] = true;
            }
        }
        if ((outside == 0) || (flat.Count < (ClipShare * outside))) {
            Array.Clear(array: clipped);
            return (clipped, float.PositiveInfinity);
        }

        flat.Sort();

        var ceiling = flat[(flat.Count / 20)];

        for (var index = 0; (index < m_values.Length); index++) {
            clipped[index] |= ((m_values[index] < Far) && (m_values[index] > 0f) && (m_values[index] >= ceiling));
        }
        return (clipped, ceiling);
    }
    /// <summary>Describes the answered slice: its plane and square, the field's range, the share of samples inside, the
    /// clipped share and its ceiling (<see cref="Clipping"/>), the share whose value the lattice over the unowned segments
    /// answered, and the gradient magnitude's least, mean and greatest values and the share of samples further than
    /// <see cref="GradientTolerance"/> from one, over the unclipped samples that answer a shape and over those within
    /// <see cref="NearBand"/> of the surface. An exact field reads one there; a bound reads below one, an overestimating
    /// warp above.</summary>
    /// <returns>One line, without brackets.</returns>
    public string Describe() {
        var (clipped, ceiling) = Clipping();
        var all = new Statistics();
        var near = new Statistics();
        var inside = 0;
        var answered = 0;
        var world = 0;
        var cut = 0;
        var low = float.PositiveInfinity;
        var high = float.NegativeInfinity;
        var band = NearBand;

        for (var index = 0; (index < m_values.Length); index++) {
            var value = m_values[index];

            if (!(value < Far)) { continue; }
            answered++;
            inside += ((value < 0f) ? 1 : 0);
            world += (m_world[index] ? 1 : 0);
            low = MathF.Min(x: low, y: value);
            high = MathF.Max(x: high, y: value);
            if (clipped[index]) {
                cut++;
                continue;
            }

            var magnitude = m_gradients[index].Length();

            all.Add(magnitude: magnitude);
            if (MathF.Abs(x: value) <= band) { near.Add(magnitude: magnitude); }
        }

        var text = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Axis.ToString().ToLowerInvariant()}={Offset:0.###} centre=({Centre.X:0.###},{Centre.Y:0.###}) half={Half:0.###} {Resolution}x{Resolution} bands={Bands:0.####}");

        if (answered == 0) {
            return $"{text} no shape answered";
        }

        var clipping = ((cut == 0)
            ? "clipped=0%"
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"clipped={((100.0 * cut) / answered):0.#}% from d={ceiling:0.####}"));

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{text} d=[{low:0.####},{high:0.####}] inside={((100.0 * inside) / m_values.Length):0.#}% {clipping} world={((100.0 * world) / answered):0.#}% |grad d| {all.Describe()} near-surface(|d|<={band:0.####}) {near.Describe()}");
    }
    /// <summary>Draws the answered slice as two panels side by side, each <see cref="Resolution"/> pixels square: on the
    /// left the distance, warm outside and cool inside, darkening toward the surface, a thin line every
    /// <see cref="Bands"/> metres of field and a thick one on the surface; on the right the gradient magnitude, white
    /// within <see cref="GradientTolerance"/> of one, blue toward one half and below (a bound), red toward one and a half and
    /// above (an overestimate), with the surface drawn black. A sample that answers no shape is grey on both, and a clipped
    /// sample (<see cref="Clipping"/>) is <see cref="ClippedColour"/> on both.</summary>
    /// <returns>The image, eight-bit RGBA rows from the top, <c>2·Resolution</c> pixels wide.</returns>
    public byte[] Render() {
        var (clipped, _) = Clipping();
        var width = (2 * Resolution);
        var rgba = new byte[((width * Resolution) * 4)];
        var pixel = ((2f * Half) / Resolution);

        for (var row = 0; (row < Resolution); row++) {
            for (var column = 0; (column < Resolution); column++) {
                var value = Value(column: column, row: row);
                var magnitude = Gradient(column: column, row: row).Length();
                // The field's own width of one pixel: a line that thick in field units draws one pixel wide wherever the
                // field's rate is, so its bands bunch where the gradient runs high and spread where it runs low.
                var line = (pixel * MathF.Max(x: magnitude, y: 1.0e-3f));
                var (distance, gradient) = ((value < Far)
                    ? (clipped[((row * Resolution) + column)]
                        ? (ClippedColour, ClippedColour)
                        : (DistanceColour(bands: Bands, line: line, value: value), GradientColour(line: line, magnitude: magnitude, value: value)))
                    : (new Vector3(value: 0.5f), new Vector3(value: 0.5f)));

                Write(colour: distance, column: column, rgba: rgba, row: row, width: width);
                Write(colour: gradient, column: (Resolution + column), rgba: rgba, row: row, width: width);
            }
        }

        return rgba;
    }

    private static Vector3 DistanceColour(float value, float bands, float line) {
        var colour = ((value < 0f) ? new Vector3(x: 0.40f, y: 0.62f, z: 0.90f) : new Vector3(x: 0.92f, y: 0.62f, z: 0.32f));
        var nearest = (MathF.Round(x: (value / bands)) * bands);

        colour *= (1f - (0.5f * MathF.Exp(x: (-4f * MathF.Abs(x: (value / bands))))));
        if (MathF.Abs(x: (value - nearest)) < (0.5f * line)) { colour *= 0.35f; }
        if (MathF.Abs(x: value) < (1.5f * line)) { colour = Vector3.One; }
        return colour;
    }
    private static Vector3 GradientColour(float magnitude, float value, float line) {
        if (MathF.Abs(x: value) < line) { return Vector3.Zero; }

        var off = (magnitude - 1f);
        var colour = ((MathF.Abs(x: off) <= GradientTolerance)
            ? Vector3.One
            : ((off < 0f)
                ? Vector3.Lerp(value1: Vector3.One, value2: new Vector3(x: 0.10f, y: 0.30f, z: 1f), amount: Math.Clamp(value: (-off / 0.5f), min: 0f, max: 1f))
                : Vector3.Lerp(value1: Vector3.One, value2: new Vector3(x: 1f, y: 0.15f, z: 0.10f), amount: Math.Clamp(value: (off / 0.5f), min: 0f, max: 1f))));

        return ((value < 0f) ? (colour * 0.8f) : colour);
    }
    private static void Write(byte[] rgba, int width, int column, int row, Vector3 colour) {
        var at = (((row * width) + column) * 4);

        rgba[at] = VectorFunctions.ToUnorm8(value: colour.X);
        rgba[(at + 1)] = VectorFunctions.ToUnorm8(value: colour.Y);
        rgba[(at + 2)] = VectorFunctions.ToUnorm8(value: colour.Z);
        rgba[(at + 3)] = 255;
    }

    // The gradient magnitude's running least, sum, greatest and count of samples off one.
    private struct Statistics {
        private float m_low;
        private float m_high;
        private double m_sum;
        private int m_count;
        private int m_off;

        public void Add(float magnitude) {
            m_low = ((m_count == 0) ? magnitude : MathF.Min(x: m_low, y: magnitude));
            m_high = ((m_count == 0) ? magnitude : MathF.Max(x: m_high, y: magnitude));
            m_sum += magnitude;
            m_count++;
            m_off += ((MathF.Abs(x: (magnitude - 1f)) > GradientTolerance) ? 1 : 0);
        }
        public readonly string Describe() => ((m_count == 0)
            ? "none"
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"min={m_low:0.###} mean={(m_sum / m_count):0.###} max={m_high:0.###} off={((100.0 * m_off) / m_count):0.#}%"));
    }
}
