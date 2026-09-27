using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Text;

namespace Puck.SignedDistance;

/// <summary>One call of the encoding probe: a small program the builder emits, and the operand values the call passed,
/// each distinct, so where each lands in the instructions it emits is a fact of the model.</summary>
/// <param name="Name">The call's name in the description.</param>
/// <param name="Markers">The operand values the call passes, pairwise distinct and nonzero.</param>
/// <param name="Emit">Emits the call's program into an empty builder, given the builder's one material.</param>
public sealed record SdfEncodingProbeCall(string Name, IReadOnlyList<float> Markers, Action<SdfProgramBuilder, int> Emit);
/// <summary>
/// The SDF instruction set's encoding, exercised: one probe call per operation, shape type, blend and lift, and one per
/// member of each enum an instruction lane carries (<see cref="SdfNoiseFlavor"/>, <see cref="SdfAxis"/>,
/// <see cref="SdfPlane"/>, <see cref="SdfWallpaperGroup"/>, <see cref="SdfCellMode"/>), each through the builder with
/// pairwise-distinct operand values. <see cref="Describe()"/> states, for every instruction a call emits, its operation,
/// its integer lanes and which operand each float lane carries, so a change to where the builder puts an operand, or to
/// an enum's values, changes the description. The description reads no value the builder derives with a transcendental
/// function, so it is the same on every host.
/// <para>A second program, <see cref="BuildLayout"/>, exercises the packed layout with arithmetic IEEE rounds exactly on
/// every host (materials, bounds, segments, instances, a field scope, a convex polygon), so its packed words are the
/// same bytes everywhere; <see cref="Describe()"/> ends with them.</para>
/// </summary>
public static class SdfEncodingProbe {
    private const int ProbeMaterials = 64;

    /// <summary>Returns every probe call, in the order the description lists them.</summary>
    /// <returns>The calls.</returns>
    public static IReadOnlyList<SdfEncodingProbeCall> Calls() {
        var calls = new List<SdfEncodingProbeCall>();

        void Call(string name, float[] markers, Action<SdfProgramBuilder, int> emit) =>
            calls.Add(item: new SdfEncodingProbeCall(
                Emit: emit,
                Markers: markers,
                Name: name
            ));

        // Point operations, each ahead of a unit sphere.
        Call(name: "translate", markers: [0.31f, 0.37f, 0.41f], emit: static (b, m) => Shape(b: b.ResetPoint().Translate(offset: new Vector3(x: 0.31f, y: 0.37f, z: 0.41f)), material: m));
        Call(name: "rotate", markers: [], emit: static (b, m) => Shape(b: b.ResetPoint().Rotate(rotation: Quaternion.CreateFromYawPitchRoll(pitch: 0f, roll: 0f, yaw: 0.25f)), material: m));
        Call(name: "scale", markers: [1.25f, 1.5f, 1.75f], emit: static (b, m) => Shape(b: b.ResetPoint().Scale(scale: new Vector3(x: 1.25f, y: 1.5f, z: 1.75f)), material: m));
        Call(name: "transform-dynamic", markers: [3f], emit: static (b, m) => Shape(b: b.ResetPoint().TransformDynamic(slot: 3), material: m));
        foreach (var plane in Enum.GetValues<SdfPlane>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>()) {
                Call(name: $"rotate-plane {plane} {driver}", markers: [0.43f, 0.47f], emit: (b, m) => Shape(b: b.ResetPoint().RotatePlane(driver: driver, origin: 0.47f, plane: plane, rate: 0.43f), material: m));
            }
        }
        Call(name: "elongate", markers: [0.21f, 0.23f, 0.29f], emit: static (b, m) => Shape(b: b.ResetPoint().Elongate(extents: new Vector3(x: 0.21f, y: 0.23f, z: 0.29f)), material: m));
        Call(name: "repeat", markers: [4.25f, 4.5f, 4.75f], emit: static (b, m) => Shape(b: b.ResetPoint().Repeat(spacing: new Vector3(x: 4.25f, y: 4.5f, z: 4.75f)), material: m));
        Call(name: "repeat-limited", markers: [4.25f, 4.5f, 4.75f, 2f, 3f, 5f], emit: static (b, m) => Shape(b: b.ResetPoint().RepeatLimited(limit: new Vector3(x: 2f, y: 3f, z: 5f), spacing: new Vector3(x: 4.25f, y: 4.5f, z: 4.75f)), material: m));
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>()) {
            foreach (var plane in Enum.GetValues<SdfPlane>()) {
                Call(name: $"wallpaper-fold {group} {plane}", markers: [4.5f, 2f, 3f, 9.5f], emit: (b, m) => Shape(b: b.ResetPoint().WallpaperFold(cell: new Vector2(value: 4.5f), group: group, limit: new Vector2(x: 2f, y: 3f), lodDistance: 9.5f, materialStride: 7, plane: plane), material: m));
            }
        }
        Call(name: "log-sphere", markers: [0.39f], emit: static (b, m) => Shape(b: b.ResetPoint().LogSphere(shellRatio: 3f, twist: 0.39f), material: m));
        foreach (var flavor in Enum.GetValues<SdfNoiseFlavor>()) {
            Call(name: $"cell-jitter {flavor}", markers: [6.25f, 6.5f, 6.75f, 0.5f, 0.35f, 11f, 5f], emit: (b, m) => Shape(b: b.ResetPoint().CellJitter(flavor: flavor, jitter: 0.5f, materialVariants: 5, seed: 11u, spacing: new Vector3(x: 6.25f, y: 6.5f, z: 6.75f), tumble: 0.35f), material: m));
        }
        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            Call(name: $"repeat-polar {axis}", markers: [6f, 4f], emit: (b, m) => Shape(b: b.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: 3f)).RepeatPolar(axis: axis, count: 6, materialStride: 4, mirror: true), material: m));
        }
        Call(name: "domain-warp", markers: [0.61f, 0.67f, 0.71f, 0.05f], emit: static (b, m) => Shape(b: b.ResetPoint().DomainWarp(amplitude: 0.05f, frequency: new Vector3(x: 0.61f, y: 0.67f, z: 0.71f)), material: m));
        Call(name: "symmetry-plane", markers: [0.15f], emit: static (b, m) => Shape(b: b.ResetPoint().SymmetryPlane(normal: Vector3.UnitX, offset: 0.15f), material: m));
        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            Call(name: $"axial-profile {axis}", markers: [0.13f, 0.17f, 0.19f, 2.5f, 0.85f], emit: (b, m) => Shape(b: b.ResetPoint().AxialProfile(amount: 0.13f, axis: axis, bulge: 0.17f, span: 2.5f, startScale: 0.85f, top: 0.19f), material: m));
        }
        foreach (var target in Enum.GetValues<SdfAxis>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>().Where(predicate: driver => (driver != target))) {
                Call(name: $"shear {target} {driver}", markers: [0.07f, 0.09f, 0.11f], emit: (b, m) => Shape(b: b.ResetPoint().Shear(cubic: 0.11f, driver: driver, linear: 0.07f, quadratic: 0.09f, target: target), material: m));
            }
        }
        Call(name: "gaussian-push", markers: [0.12f, 0.14f, 0.16f, 0.6f, 0.7f, 0.8f, 0.02f, 0.03f, 0.04f], emit: static (b, m) => Shape(b: b.ResetPoint().GaussianPush(center: new Vector3(x: 0.12f, y: 0.14f, z: 0.16f), push: new Vector3(x: 0.02f, y: 0.03f, z: 0.04f), radii: new Vector3(x: 0.6f, y: 0.7f, z: 0.8f)), material: m));
        Call(name: "lane-erode", markers: [2f, 0.25f, 0.75f, 1.5f, 1.25f], emit: static (b, m) => Shape(b: b.ResetPoint().LaneErode(from: 0.25f, lane: 2, noiseScale: 1.5f, reach: 1.25f, to: 0.75f), material: m));

        // Field operations, each after a unit sphere.
        Call(name: "onion", markers: [0.08f], emit: static (b, m) => Shape(b: b.ResetPoint(), material: m).Onion(thickness: 0.08f));
        Call(name: "dilate", markers: [0.06f], emit: static (b, m) => Shape(b: b.ResetPoint(), material: m).Dilate(radius: 0.06f));
        Call(name: "displace", markers: [0.51f, 0.53f, 0.57f, 0.04f], emit: static (b, m) => Shape(b: b.ResetPoint(), material: m).Displace(amplitude: 0.04f, frequency: new Vector3(x: 0.51f, y: 0.53f, z: 0.57f)));
        Call(name: "noise-displace", markers: [0.9f, 0.03f, 3f, 0.45f, 2.25f, 13f], emit: static (b, m) => Shape(b: b.ResetPoint(), material: m).NoiseDisplace(amplitude: 0.03f, frequency: 0.9f, gain: 0.45f, lacunarity: 2.25f, octaves: 3, seed: 13u));
        foreach (var mode in Enum.GetValues<SdfCellMode>()) {
            Call(name: $"cell-displace {mode}", markers: [0.95f, 0.02f, 17f, 0.15f], emit: (b, m) => Shape(b: b.ResetPoint(), material: m).CellDisplace(amplitude: 0.02f, frequency: 0.95f, mode: mode, randomness: 0.15f, seed: 17u));
        }

        // Field scopes, closing with each compose.
        foreach (var compose in Enum.GetValues<SdfBlendOp>().Where(predicate: static compose => (compose is not (SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction)))) {
            Call(name: $"field-scope {compose}", markers: [0.26f], emit: (b, m) => Shape(b: b.ResetPoint().PushField(compose: compose, smooth: 0.26f).ResetPoint(), material: m).PopField());
        }
        Call(name: "field-scope morph", markers: [3f, 0.2f, 0.8f], emit: static (b, m) => Shape(b: b.ResetPoint().PushFieldMorph(from: 0.2f, laneIndex: 3, to: 0.8f).ResetPoint(), material: m).PopField());
        foreach (var subtraction in ((ReadOnlySpan<bool>)[false, true])) {
            Call(name: $"field-scope stairs {(subtraction ? "subtraction" : "union")}", markers: [0.27f, 4f], emit: (b, m) => Shape(b: b.ResetPoint().PushFieldStairs(radius: 0.27f, steps: 4, subtraction: subtraction).ResetPoint(), material: m).PopField());
        }

        // Every blend a shape takes, and the shape flags.
        foreach (var blend in Enum.GetValues<SdfBlendOp>().Where(predicate: static blend => (blend is not (SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction)))) {
            Call(name: $"blend {blend}", markers: [0.33f, 0.22f], emit: (b, m) => b.ResetPoint().Sphere(blend: blend, material: m, radius: 0.33f, smooth: 0.22f));
        }
        Call(name: "detail", markers: [0.34f], emit: static (b, m) => b.ResetPoint().Sphere(detail: true, material: m, radius: 0.34f));
        Call(name: "no-secondary", markers: [0.35f], emit: static (b, m) => b.ResetPoint().Sphere(material: m, radius: 0.35f).MarkSecondary(secondary: false));

        // Every shape type.
        Call(name: "box", markers: [0.41f, 0.43f, 0.47f, 0.05f], emit: static (b, m) => b.ResetPoint().Box(halfExtents: new Vector3(x: 0.41f, y: 0.43f, z: 0.47f), material: m, round: 0.05f));
        Call(name: "capsule", markers: [0.52f, 0.54f, 0.56f, 0.12f], emit: static (b, m) => b.ResetPoint().Capsule(endpoint: new Vector3(x: 0.52f, y: 0.54f, z: 0.56f), material: m, radius: 0.12f));
        Call(name: "sphere", markers: [0.58f], emit: static (b, m) => b.ResetPoint().Sphere(material: m, radius: 0.58f));
        Call(name: "torus", markers: [0.62f, 0.14f], emit: static (b, m) => b.ResetPoint().Torus(majorRadius: 0.62f, material: m, minorRadius: 0.14f));
        Call(name: "cylinder", markers: [0.36f, 0.64f, 0.03f], emit: static (b, m) => b.ResetPoint().Cylinder(halfHeight: 0.64f, material: m, radius: 0.36f, rounding: 0.03f));
        Call(name: "plane", markers: [0.18f], emit: static (b, m) => b.ResetPoint().Plane(material: m, normal: Vector3.UnitY, offset: 0.18f));
        Call(name: "vesica", markers: [0.66f, 0.24f], emit: static (b, m) => b.ResetPoint().Vesica(halfSeparation: 0.24f, material: m, radius: 0.66f));
        Call(name: "round-cone", markers: [0.28f, 0.16f, 0.72f], emit: static (b, m) => b.ResetPoint().RoundCone(height: 0.72f, lowerRadius: 0.28f, material: m, upperRadius: 0.16f));
        Call(name: "superellipsoid", markers: [0.44f, 0.46f, 0.48f, 2.5f], emit: static (b, m) => b.ResetPoint().Superellipsoid(exponent: 2.5f, material: m, radii: new Vector3(x: 0.44f, y: 0.46f, z: 0.48f)));
        Call(name: "sampled-region", markers: [0.11f, 0.13f, 0.17f, 0.05f, 4f, 5f, 6f, 8f, 0.02f], emit: static (b, m) => b.ResetPoint().SampledRegion(boundaryFloor: 0.02f, boxMin: new Vector3(x: 0.11f, y: 0.13f, z: 0.17f), brickWordOffset: 8, cellSize: 0.05f, dimX: 4, dimY: 5, dimZ: 6, material: m));
        Call(name: "screen-slab", markers: [0.74f, 0.42f, 0.02f, 2f], emit: static (b, _) => b.ResetPoint().ScreenSlab(halfExtents: new Vector3(x: 0.74f, y: 0.42f, z: 0.02f), round: 0f, screenIndex: 2, worldOrigin: Vector3.Zero, worldOrientation: Quaternion.Identity));
        Call(name: "sweep", markers: [0.2f, 0.3f, 0.4f, 0.09f, 0.07f, 0.5f, 3f, 0.25f, 0.015f], emit: static (b, m) => b.ResetPoint().Sweep(a: Vector3.Zero, b: new Vector3(x: 0.2f, y: 0.3f, z: 0f), bulge: 0.5f, c: new Vector3(x: 0.4f, y: 0f, z: 0f), material: m, radiusEnd: 0.07f, radiusStart: 0.09f, strandOffset: 0.015f, strands: 3, twist: 0.25f));
        Call(name: "path", markers: [0.8f, 0.9f, 0.1f], emit: static (b, m) => b.ResetPoint().Path(halfDepth: 0.1f, material: m, profile: new SdfPathProfile(Contours: [new SdfPathContour(new Vector2(x: -0.5f, y: -0.5f), [new SdfPathSegment(new Vector2(x: 0.5f, y: -0.5f)), new SdfPathSegment(new Vector2(x: 0f, y: 0.5f))])]), scale: new Vector2(x: 0.8f, y: 0.9f)));
        Call(name: "glyph", markers: [0.26f, 0.3f, 0.04f, 3.5f], emit: static (b, m) => b.ResetPoint().Glyph(atlas: GlyphAtlas, distanceScale: 3.5f, extrudeHalfDepth: 0.04f, halfHeight: 0.3f, halfWidth: 0.26f, material: m, uvBottomLeft: Vector2.Zero, uvTopRight: Vector2.One));
        foreach (var lift in Enum.GetValues<SdfLift>()) {
            Call(name: $"ellipse {lift}", markers: [0.46f, 0.32f, 0.12f], emit: (b, m) => b.ResetPoint().Ellipse(lift: lift, liftAmount: 0.12f, material: m, semiX: 0.46f, semiY: 0.32f));
            Call(name: $"regular-polygon {lift}", markers: [5f, 0.44f, 0.13f], emit: (b, m) => b.ResetPoint().RegularPolygon(lift: lift, liftAmount: 0.13f, material: m, radius: 0.44f, sides: 5));
            Call(name: $"rounded-rectangle {lift}", markers: [0.48f, 0.34f, 0.06f, 0.14f], emit: (b, m) => b.ResetPoint().RoundedRectangle(cornerRadius: 0.06f, halfHeight: 0.34f, halfWidth: 0.48f, lift: lift, liftAmount: 0.14f, material: m));
            Call(name: $"star {lift}", markers: [6f, 0.52f, 0.4f, 0.15f], emit: (b, m) => b.ResetPoint().Star(lift: lift, liftAmount: 0.15f, material: m, points: 6, radius: 0.52f, sharpness: 0.4f));
            Call(name: $"trapezoid {lift}", markers: [0.5f, 0.3f, 0.38f, 0.16f], emit: (b, m) => b.ResetPoint().Trapezoid(bottomHalfWidth: 0.5f, halfHeight: 0.38f, lift: lift, liftAmount: 0.16f, material: m, topHalfWidth: 0.3f));
            Call(name: $"chamfered-rectangle {lift}", markers: [0.54f, 0.36f, 0.07f, 0.17f], emit: (b, m) => b.ResetPoint().ChamferedRectangle(chamfer: 0.07f, halfHeight: 0.36f, halfWidth: 0.54f, lift: lift, liftAmount: 0.17f, material: m));
            Call(name: $"convex-polygon {lift}", markers: [0.05f, 0.18f], emit: (b, m) => b.ResetPoint().ConvexPolygon(cornerRadius: 0.05f, lift: lift, liftAmount: 0.18f, material: m, vertices: [new Vector2(x: -0.5f, y: -0.25f), new Vector2(x: 0f, y: 0.75f), new Vector2(x: 0.5f, y: -0.25f)]));
        }

        return calls;
    }
    /// <summary>Builds one call's program.</summary>
    /// <param name="call">The call.</param>
    /// <returns>The program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is <see langword="null"/>.</exception>
    public static SdfProgram Build(SdfEncodingProbeCall call) {
        ArgumentNullException.ThrowIfNull(argument: call);

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // A palette wide enough for every positional recolor a call strides the material by.
        for (var index = 1; (index < ProbeMaterials); index++) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        }

        call.Emit(arg1: builder, arg2: material);

        return builder.Build();
    }
    /// <summary>Builds the layout program: materials whose every field is distinct, a static and a dynamic instance, a
    /// field scope and a convex polygon, over arithmetic IEEE rounds exactly on every host.</summary>
    /// <returns>The program.</returns>
    public static SdfProgram BuildLayout() {
        var builder = new SdfProgramBuilder();
        var first = builder.AddMaterial(material: new SdfMaterial(
            Albedo: new Vector3(x: 0.125f, y: 0.25f, z: 0.375f),
            Bounce: new Vector3(x: 0.0625f, y: 0.1875f, z: 0.3125f),
            Coat: 0.4375f,
            Emissive: 0.5f,
            Metal: 0.5625f,
            Roughness: 0.625f,
            Sheen: 0.6875f,
            Soften: 0.75f,
            Specular: 0.8125f,
            Wrap: 0.875f
        ));
        var second = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9375f, y: 0.03125f, z: 0.09375f)));

        builder.Instance(
            boundCenter: new Vector3(x: 1f, y: 2f, z: 3f),
            boundRadius: 2f,
            emit: instance => instance.ResetPoint().Translate(offset: new Vector3(x: 1f, y: 2f, z: 3f)).Box(halfExtents: new Vector3(x: 0.5f, y: 0.25f, z: 0.75f), material: first, round: 0.125f)
        );
        builder.DynamicInstance(
            boundOffset: new Vector3(x: 0.5f, y: 0f, z: 0f),
            boundRadius: 1.5f,
            emit: instance => instance.ResetPoint().TransformDynamic(slot: 1).Sphere(material: second, radius: 0.75f),
            slot: 1
        );
        builder.ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .ResetPoint()
            .Sphere(material: first, radius: 0.5f)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0.25f, y: 0f, z: 0f))
            .Sphere(blend: SdfBlendOp.Subtraction, material: second, radius: 0.375f)
            .PopField();
        builder.ResetPoint().ConvexPolygon(cornerRadius: 0.0625f, lift: SdfLift.Extrude, liftAmount: 0.25f, material: first, vertices: [new Vector2(x: -0.5f, y: -0.5f), new Vector2(x: -0.5f, y: 0.5f), new Vector2(x: 0.5f, y: 0.5f), new Vector2(x: 0.5f, y: -0.5f)]);

        return builder.Build();
    }
    /// <summary>Describes the encoding the probe exercises (<see cref="Calls"/> and <see cref="BuildLayout"/>).</summary>
    /// <returns>The description: one line per instruction each call emits, then the layout program's words.</returns>
    public static string Describe() =>
        Describe(calls: Calls());
    /// <summary>Describes the encoding a set of probe calls exercises: for each instruction a call emits, its operation,
    /// its flags and its header lanes, whose integers (enums, counts, seeds, indices and float bits the builder copies)
    /// are exact on every host, and for each data lane whether it holds zero, one of the call's operands (by position),
    /// or a value the builder derived; then the layout program's words.</summary>
    /// <param name="calls">The calls.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calls"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A call's operands repeat or hold zero, so a lane could not say which it
    /// carries.</exception>
    public static string Describe(IReadOnlyList<SdfEncodingProbeCall> calls) {
        ArgumentNullException.ThrowIfNull(argument: calls);

        var text = new StringBuilder();

        foreach (var call in calls) {
            if ((call.Markers.Distinct().Count() != call.Markers.Count) || call.Markers.Contains(value: 0f)) {
                throw new ArgumentException(message: $"Probe call '{call.Name}' passes a repeated or zero operand, so a lane could not say which operand it carries.", paramName: nameof(calls));
            }

            var program = Build(call: call);

            text.Append(value: call.Name).Append(value: '\n');

            foreach (var instruction in program.Instructions) {
                text.Append(provider: CultureInfo.InvariantCulture, handler: $"  op={((uint)instruction.Op)} detail={(instruction.Detail ? 1 : 0)} secondary={(instruction.Secondary ? 1 : 0)} shape={instruction.Shape} blend={instruction.Blend} material={instruction.Material}");
                text.Append(value: " data0=").Append(value: Lanes(markers: call.Markers, vector: instruction.Data0));
                text.Append(value: " data1=").Append(value: Lanes(markers: call.Markers, vector: instruction.Data1));
                text.Append(value: '\n');
            }
        }

        text.Append(value: "layout");

        foreach (var word in BuildLayout().Words) {
            text.Append(provider: CultureInfo.InvariantCulture, handler: $" {word:X8}");
        }

        return text.Append(value: '\n').ToString();
    }

    // An atlas of one 2x2 page, enough for the builder to bake a glyph's sampling correction.
    private static FontAtlas GlyphAtlas { get; } = new(
        FontAtlasKind.Mtsdf,
        "probe://glyph",
        32,
        8,
        2,
        2,
        default,
        [],
        [],
        new FontAtlasImageData(
            height: 2,
            rgbaPixels: [0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 0],
            width: 2
        )
    );

    // A data vector's lanes: zero, the operand a lane carries, or a value the builder derived.
    private static string Lanes(IReadOnlyList<float> markers, Vector4 vector) =>
        string.Join(separator: ',', values: new[] { vector.X, vector.Y, vector.Z, vector.W }.Select(selector: value => {
            if (value == 0f) {
                return "0";
            }

            for (var index = 0; (index < markers.Count); index++) {
                if (
                    (value == markers[index]) ||
                    ((markers[index] == MathF.Floor(x: markers[index])) && (BitConverter.SingleToUInt32Bits(value: value) == ((uint)markers[index])))
                ) {
                    return $"m{index}";
                }
            }

            return "d";
        }));
    // A unit sphere after the call's operation, so every call's program ends in a shape.
    private static SdfProgramBuilder Shape(SdfProgramBuilder b, int material) =>
        b.Sphere(material: material, radius: 1f);
}
