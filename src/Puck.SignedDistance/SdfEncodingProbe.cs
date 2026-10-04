using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Text;

namespace Puck.SignedDistance;

/// <summary>One call of the encoding probe: a small program the builder emits from a vector of inputs, every value the
/// call hands the builder.</summary>
/// <param name="Name">The call's name in the description.</param>
/// <param name="Inputs">The values the call passes: an integral value where the builder takes an integer, and positive
/// values the call stays valid for when each is raised (an integral one by one, any other by a sixteenth).</param>
/// <param name="Emit">Emits the call's program into an empty builder, given the builder's first material and the inputs
/// to pass.</param>
public sealed record SdfEncodingProbeCall(string Name, IReadOnlyList<float> Inputs, Action<SdfProgramBuilder, int, IReadOnlyList<float>> Emit);
/// <summary>
/// The SDF instruction set's encoding, exercised and described. <see cref="Calls"/> holds one probe call per operation,
/// shape type, blend and lift, one per member of each enum an instruction lane carries (<see cref="SdfNoiseFlavor"/>,
/// <see cref="SdfCellMode"/>, <see cref="SdfAxis"/>, <see cref="SdfPlane"/>, <see cref="SdfWallpaperGroup"/>), and calls
/// that pack every side table and flag the kernels read: a material with every field and layer, compiled part programs,
/// sweep, path and convex-polygon tables, instances and the instance grid.
/// <para><see cref="Describe()"/> states, for each call's packed program, every word that holds an integer (a count, an
/// offset, an enum, a flag, a packed bitfield), for each input which words move when that input alone is raised (the
/// bits an integer word's change spans, and every float word that moves), and for each float word that moves, the input
/// that moves it the most. So a change to where the builder puts
/// an operand, to a bitfield's position, to an enum's value or to where the packer lays a table out changes the
/// description. The description reads no float bit pattern, only which words move and by how much relative to each
/// other, so it is the same on every host, including for values the builder derives with a transcendental
/// function.</para>
/// </summary>
public static class SdfEncodingProbe {
    // A palette wide enough for every positional recolor a call strides the material by.
    private const int ProbeMaterials = 64;
    // An input moves a float word the most only when it beats the next input by this factor; otherwise the description
    // says the inputs tie, so hosts that round a derived value differently still describe alike.
    private const float Margin = 1.5f;

    /// <summary>Returns every probe call, in the order the description lists them.</summary>
    /// <returns>The calls.</returns>
    public static IReadOnlyList<SdfEncodingProbeCall> Calls() {
        var calls = new List<SdfEncodingProbeCall>();

        void Call(string name, float[] inputs, Action<SdfProgramBuilder, int, IReadOnlyList<float>> emit) =>
            calls.Add(item: new SdfEncodingProbeCall(
                Emit: emit,
                Inputs: inputs,
                Name: name
            ));

        Call(name: "tape certificate", inputs: [0.2f, 0.3f, 0.4f, 0.5f], emit: static (b, m, v) => b.ResetPoint()
            .Translate(offset: new Vector3(x: v[0], y: v[1], z: v[2])).Sphere(radius: v[3], material: m));
        // Point operations, each ahead of a unit sphere.
        Call(name: "translate", inputs: [0.31f, 0.37f, 0.41f], emit: static (b, m, v) => Shape(b: b.ResetPoint().Translate(offset: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "rotate", inputs: [0.4f, 0.5f, 0.6f, 0.7f], emit: static (b, m, v) => Shape(b: b.ResetPoint().Rotate(rotation: new Quaternion(w: v[3], x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "scale", inputs: [1.25f, 1.5f, 1.75f], emit: static (b, m, v) => Shape(b: b.ResetPoint().Scale(scale: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "transform-dynamic", inputs: [3f], emit: static (b, m, v) => Shape(b: b.ResetPoint().TransformDynamic(slot: ((int)v[0])), material: m));
        foreach (var plane in Enum.GetValues<SdfPlane>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>()) {
                Call(name: $"rotate-plane {plane} {driver}", inputs: [0.43f, 0.47f], emit: (b, m, v) => Shape(b: b.ResetPoint().RotatePlane(driver: driver, origin: v[1], plane: plane, rate: v[0]), material: m));
            }
        }
        Call(name: "elongate", inputs: [0.21f, 0.23f, 0.29f], emit: static (b, m, v) => Shape(b: b.ResetPoint().Elongate(extents: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "repeat", inputs: [4.25f, 4.5f, 4.75f], emit: static (b, m, v) => Shape(b: b.ResetPoint().Repeat(spacing: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "repeat-limited", inputs: [4.25f, 4.5f, 4.75f, 2f, 3f, 5f], emit: static (b, m, v) => Shape(b: b.ResetPoint().RepeatLimited(limit: new Vector3(x: v[3], y: v[4], z: v[5]), spacing: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        // A group whose fold jumps is refused at program build, so only the continuous groups build here
        // (SdfWallpaperFoldLawTests holds the refusals). A hex lattice takes only the unbounded limit, so its calls carry
        // the limit as a constant and the square groups carry it as an input.
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            foreach (var plane in Enum.GetValues<SdfPlane>()) {
                if (group >= SdfWallpaperGroup.P3) {
                    Call(name: $"wallpaper-fold {group} {plane}", inputs: [4.5f, 7f], emit: (b, m, v) => Shape(b: b.ResetPoint().WallpaperFold(cell: new Vector2(value: v[0]), group: group, limit: new Vector2(value: SdfWallpaperFold.UnboundedLimit), materialStride: ((int)v[1]), plane: plane), material: m));
                } else {
                    Call(name: $"wallpaper-fold {group} {plane}", inputs: [4.5f, 2f, 3f, 7f], emit: (b, m, v) => Shape(b: b.ResetPoint().WallpaperFold(cell: new Vector2(value: v[0]), group: group, limit: new Vector2(x: v[1], y: v[2]), materialStride: ((int)v[3]), plane: plane), material: m));
                }
            }
        }
        Call(name: "log-sphere", inputs: [3f, 0.39f], emit: static (b, m, v) => Shape(b: b.ResetPoint().LogSphere(shellRatio: v[0], twist: v[1]), material: m));
        foreach (var flavor in Enum.GetValues<SdfNoiseFlavor>()) {
            Call(name: $"cell-jitter {flavor}", inputs: [6.25f, 6.5f, 6.75f, 0.5f, 0.35f, 11f, 5f], emit: (b, m, v) => Shape(b: b.ResetPoint().CellJitter(flavor: flavor, jitter: v[3], materialVariants: ((int)v[6]), seed: ((uint)v[5]), spacing: new Vector3(x: v[0], y: v[1], z: v[2]), tumble: v[4]), material: m));
        }
        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            Call(name: $"repeat-polar {axis}", inputs: [6f, 4f], emit: (b, m, v) => Shape(b: b.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: 3f)).RepeatPolar(axis: axis, count: ((int)v[0]), materialStride: ((int)v[1]), mirror: true), material: m));
        }
        Call(name: "domain-warp", inputs: [0.61f, 0.67f, 0.71f, 0.05f], emit: static (b, m, v) => Shape(b: b.ResetPoint().DomainWarp(amplitude: v[3], frequency: new Vector3(x: v[0], y: v[1], z: v[2])), material: m));
        Call(name: "symmetry-plane", inputs: [0.15f], emit: static (b, m, v) => Shape(b: b.ResetPoint().SymmetryPlane(normal: Vector3.UnitX, offset: v[0]), material: m));
        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            Call(name: $"axial-profile {axis}", inputs: [0.13f, 0.17f, 0.19f, 2.5f, 0.85f], emit: (b, m, v) => Shape(b: b.ResetPoint().AxialProfile(amount: v[0], axis: axis, bulge: v[1], span: v[3], startScale: v[4], top: v[2]), material: m));
        }
        foreach (var target in Enum.GetValues<SdfAxis>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>().Where(predicate: driver => (driver != target))) {
                Call(name: $"shear {target} {driver}", inputs: [0.07f, 0.09f, 0.11f], emit: (b, m, v) => Shape(b: b.ResetPoint().Shear(cubic: v[2], driver: driver, linear: v[0], quadratic: v[1], target: target), material: m));
            }
        }
        Call(name: "gaussian-push", inputs: [0.12f, 0.14f, 0.16f, 0.6f, 0.7f, 0.8f, 0.02f, 0.03f, 0.04f], emit: static (b, m, v) => Shape(b: b.ResetPoint().GaussianPush(center: new Vector3(x: v[0], y: v[1], z: v[2]), push: new Vector3(x: v[6], y: v[7], z: v[8]), radii: new Vector3(x: v[3], y: v[4], z: v[5])), material: m));
        Call(name: "lane-erode", inputs: [2f, 0.25f, 0.75f, 1.5f, 1.25f], emit: static (b, m, v) => Shape(b: b.ResetPoint().LaneErode(from: v[1], lane: ((int)v[0]), noiseScale: v[3], reach: v[4], to: v[2]), material: m));

        // Field operations, each after a unit sphere.
        Call(name: "onion", inputs: [0.08f], emit: static (b, m, v) => Shape(b: b.ResetPoint(), material: m).Onion(thickness: v[0]));
        Call(name: "dilate", inputs: [0.06f], emit: static (b, m, v) => Shape(b: b.ResetPoint(), material: m).Dilate(radius: v[0]));
        Call(name: "displace", inputs: [0.51f, 0.53f, 0.57f, 0.04f], emit: static (b, m, v) => Shape(b: b.ResetPoint(), material: m).Displace(amplitude: v[3], frequency: new Vector3(x: v[0], y: v[1], z: v[2])));
        Call(name: "noise-displace", inputs: [0.9f, 0.03f, 3f, 0.45f, 2.25f, 13f], emit: static (b, m, v) => Shape(b: b.ResetPoint(), material: m).NoiseDisplace(amplitude: v[1], frequency: v[0], gain: v[3], lacunarity: v[4], octaves: ((int)v[2]), seed: ((uint)v[5])));
        foreach (var mode in Enum.GetValues<SdfCellMode>()) {
            Call(name: $"cell-displace {mode}", inputs: [0.95f, 0.02f, 17f, 0.15f], emit: (b, m, v) => Shape(b: b.ResetPoint(), material: m).CellDisplace(amplitude: v[1], frequency: v[0], mode: mode, randomness: v[3], seed: ((uint)v[2])));
        }

        // Field scopes, closing with each compose.
        foreach (var compose in Enum.GetValues<SdfBlendOp>().Where(predicate: static compose => (compose is not (SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction)))) {
            Call(name: $"field-scope {compose}", inputs: [0.26f], emit: (b, m, v) => Shape(b: b.ResetPoint().PushField(compose: compose, smooth: v[0]).ResetPoint(), material: m).PopField());
        }
        Call(name: "field-scope morph", inputs: [2f, 0.2f, 0.8f], emit: static (b, m, v) => Shape(b: b.ResetPoint().PushFieldMorph(from: v[1], laneIndex: ((int)v[0]), to: v[2]).ResetPoint(), material: m).PopField());
        foreach (var subtraction in ((ReadOnlySpan<bool>)[false, true])) {
            Call(name: $"field-scope stairs {(subtraction ? "subtraction" : "union")}", inputs: [0.27f, 4f], emit: (b, m, v) => Shape(b: b.ResetPoint().PushFieldStairs(radius: v[0], steps: ((int)v[1]), subtraction: subtraction).ResetPoint(), material: m).PopField());
        }

        // Every blend a shape takes, and the shape flags.
        foreach (var blend in Enum.GetValues<SdfBlendOp>().Where(predicate: static blend => (blend is not (SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction)))) {
            Call(name: $"blend {blend}", inputs: [0.33f, 0.22f], emit: (b, m, v) => b.ResetPoint().Sphere(blend: blend, material: m, radius: v[0], smooth: v[1]));
        }
        Call(name: "detail", inputs: [0.34f], emit: static (b, m, v) => b.ResetPoint().Sphere(detail: true, material: m, radius: v[0]));
        Call(name: "no-secondary", inputs: [0.35f], emit: static (b, m, v) => b.ResetPoint().Sphere(material: m, radius: v[0]).MarkSecondary(secondary: false));

        // Every shape type.
        Call(name: "box", inputs: [0.41f, 0.43f, 0.47f, 0.05f], emit: static (b, m, v) => b.ResetPoint().Box(halfExtents: new Vector3(x: v[0], y: v[1], z: v[2]), material: m, round: v[3]));
        Call(name: "capsule", inputs: [0.52f, 0.54f, 0.56f, 0.12f], emit: static (b, m, v) => b.ResetPoint().Capsule(endpoint: new Vector3(x: v[0], y: v[1], z: v[2]), material: m, radius: v[3]));
        Call(name: "sphere", inputs: [0.58f], emit: static (b, m, v) => b.ResetPoint().Sphere(material: m, radius: v[0]));
        Call(name: "torus", inputs: [0.62f, 0.14f], emit: static (b, m, v) => b.ResetPoint().Torus(majorRadius: v[0], material: m, minorRadius: v[1]));
        Call(name: "cylinder", inputs: [0.36f, 0.64f, 0.03f], emit: static (b, m, v) => b.ResetPoint().Cylinder(halfHeight: v[1], material: m, radius: v[0], rounding: v[2]));
        Call(name: "plane", inputs: [0.18f], emit: static (b, m, v) => b.ResetPoint().Plane(material: m, normal: Vector3.UnitY, offset: v[0]));
        Call(name: "vesica", inputs: [0.66f, 0.24f], emit: static (b, m, v) => b.ResetPoint().Vesica(halfSeparation: v[1], material: m, radius: v[0]));
        Call(name: "round-cone", inputs: [0.28f, 0.16f, 0.72f], emit: static (b, m, v) => b.ResetPoint().RoundCone(height: v[2], lowerRadius: v[0], material: m, upperRadius: v[1]));
        Call(name: "superellipsoid", inputs: [0.44f, 0.46f, 0.48f, 2.5f], emit: static (b, m, v) => b.ResetPoint().Superellipsoid(exponent: v[3], material: m, radii: new Vector3(x: v[0], y: v[1], z: v[2])));
        Call(name: "sampled-region", inputs: [0.11f, 0.13f, 0.17f, 0.05f, 4f, 5f, 6f, 8f, 0.02f], emit: static (b, m, v) => b.ResetPoint().SampledRegion(boundaryFloor: v[8], boxMin: new Vector3(x: v[0], y: v[1], z: v[2]), brickWordOffset: ((int)v[7]), cellSize: v[3], dimX: ((int)v[4]), dimY: ((int)v[5]), dimZ: ((int)v[6]), material: m));
        Call(name: "screen-slab", inputs: [0.74f, 0.42f, 0.02f, 2f], emit: static (b, _, v) => b.ResetPoint().ScreenSlab(halfExtents: new Vector3(x: v[0], y: v[1], z: v[2]), round: 0f, screenIndex: ((int)v[3]), worldOrigin: Vector3.Zero, worldOrientation: Quaternion.Identity));
        Call(name: "sweep", inputs: [0.2f, 0.3f, 0.4f, 0.09f, 0.07f, 0.5f, 3f, 0.25f, 0.015f], emit: static (b, m, v) => b.ResetPoint().Sweep(a: Vector3.Zero, b: new Vector3(x: v[0], y: v[1], z: 0f), bulge: v[5], c: new Vector3(x: v[2], y: 0f, z: 0f), material: m, radiusEnd: v[4], radiusStart: v[3], strandOffset: v[8], strands: ((int)v[6]), twist: v[7]));
        Call(name: "path", inputs: [0.8f, 0.9f, 0.1f, 0.3f], emit: static (b, m, v) => b.ResetPoint().Path(halfDepth: v[2], material: m, profile: new SdfPathProfile(Contours: [new SdfPathContour(new Vector2(x: -0.5f, y: -0.5f), [new SdfPathSegment(new Vector2(x: 0.5f, y: -0.5f)), new SdfPathSegment(new Vector2(x: 0f, y: v[3]))])]), scale: new Vector2(x: v[0], y: v[1])));
        Call(name: "path stroke", inputs: [0.85f, 0.95f, 0.12f, 0.05f, 0.08f], emit: static (b, m, v) => b.ResetPoint().Path(halfDepth: v[2], material: m, profile: new SdfPathProfile(Contours: [new SdfPathContour(new Vector2(x: -0.4f, y: -0.3f), [new SdfPathSegment(new Vector2(x: 0.4f, y: -0.3f)), new SdfPathSegment(new Vector2(x: 0f, y: 0.35f))])], Stroke: new SdfPathStroke(RadiusEnd: v[4], RadiusStart: v[3])), scale: new Vector2(x: v[0], y: v[1])));
        Call(name: "glyph", inputs: [0.26f, 0.3f, 0.04f, 3.5f], emit: static (b, m, v) => b.ResetPoint().Glyph(atlas: GlyphAtlas, distanceScale: v[3], extrudeHalfDepth: v[2], halfHeight: v[1], halfWidth: v[0], material: m, uvBottomLeft: Vector2.Zero, uvTopRight: Vector2.One));
        foreach (var lift in Enum.GetValues<SdfLift>()) {
            Call(name: $"ellipse {lift}", inputs: [0.46f, 0.32f, 0.12f], emit: (b, m, v) => b.ResetPoint().Ellipse(lift: lift, liftAmount: v[2], material: m, semiX: v[0], semiY: v[1]));
            Call(name: $"regular-polygon {lift}", inputs: [5f, 0.44f, 0.13f], emit: (b, m, v) => b.ResetPoint().RegularPolygon(lift: lift, liftAmount: v[2], material: m, radius: v[1], sides: ((int)v[0])));
            Call(name: $"rounded-rectangle {lift}", inputs: [0.48f, 0.34f, 0.06f, 0.14f], emit: (b, m, v) => b.ResetPoint().RoundedRectangle(cornerRadius: v[2], halfHeight: v[1], halfWidth: v[0], lift: lift, liftAmount: v[3], material: m));
            Call(name: $"star {lift}", inputs: [6f, 0.52f, 0.4f, 0.15f], emit: (b, m, v) => b.ResetPoint().Star(lift: lift, liftAmount: v[3], material: m, points: ((int)v[0]), radius: v[1], sharpness: v[2]));
            Call(name: $"trapezoid {lift}", inputs: [0.5f, 0.3f, 0.38f, 0.16f], emit: (b, m, v) => b.ResetPoint().Trapezoid(bottomHalfWidth: v[0], halfHeight: v[2], lift: lift, liftAmount: v[3], material: m, topHalfWidth: v[1]));
            Call(name: $"chamfered-rectangle {lift}", inputs: [0.54f, 0.36f, 0.07f, 0.17f], emit: (b, m, v) => b.ResetPoint().ChamferedRectangle(chamfer: v[2], halfHeight: v[1], halfWidth: v[0], lift: lift, liftAmount: v[3], material: m));
            Call(name: $"convex-polygon {lift}", inputs: [0.05f, 0.18f, 0.75f], emit: (b, m, v) => b.ResetPoint().ConvexPolygon(cornerRadius: v[0], lift: lift, liftAmount: v[1], material: m, vertices: [new Vector2(x: -0.5f, y: -0.25f), new Vector2(x: 0f, y: v[2]), new Vector2(x: 0.5f, y: -0.25f)]));
        }

        // Side tables and flags: a material with every field and layer, static and dynamic instances over the grid, and
        // compiled part programs traced independently.
        Call(
            emit: static (b, _, v) => b.ResetPoint().Sphere(
                material: b.AddMaterial(material: new SdfMaterial(
                    Albedo: new Vector3(x: v[0], y: v[1], z: v[2]),
                    Fill: new Vector3(x: v[3], y: v[4], z: v[5]),
                    Coat: v[6],
                    Emissive: v[7],
                    Inset: new SdfInset(
                        Depth: v[8],
                        Ior: v[9],
                        Origin: new Vector3(x: v[10], y: v[11], z: v[12]),
                        Paint: new SdfRadialPaint(
                            ModulationAmplitude: v[13],
                            ModulationFrequency: v[14],
                            Seed: ((uint)v[15]),
                            Softness: v[16],
                            Stops: [new SdfRadialStop(Color: new Vector3(x: v[17], y: v[18], z: v[19]), Radius: v[20]), new SdfRadialStop(Color: new Vector3(x: v[21], y: v[22], z: v[23]), Radius: v[24])]
                        ),
                        Rotation: new Quaternion(w: v[28], x: v[25], y: v[26], z: v[27])
                    ),
                    Metal: v[29],
                    Roughness: v[30],
                    Sheen: v[31],
                    Soften: v[32],
                    Specular: v[33],
                    Weathering: new SdfWeathering(
                        Deposit: new SdfSurface(Color: new Vector3(x: v[34], y: v[35], z: v[36]), Metal: v[37], Roughness: v[38]),
                        Edge: v[39],
                        Floor: v[40],
                        Lane: ((int)v[41]),
                        Lines: v[42],
                        Reach: v[43],
                        Scale: v[44],
                        Seed: ((uint)v[45]),
                        Settle: v[46],
                        Under: [new SdfRevealStage(Surface: new SdfSurface(Color: new Vector3(x: v[47], y: v[48], z: v[49]), Metal: v[50], Roughness: v[51]), Threshold: v[52]), new SdfRevealStage(Surface: new SdfSurface(Color: new Vector3(x: v[53], y: v[54], z: v[55]), Metal: v[56], Roughness: v[57]), Threshold: v[58])]
                    ),
                    Wrap: v[59]
                )),
                radius: 0.5f
            ),
            inputs: [
                0.11f, 0.12f, 0.13f, 0.14f, 0.15f, 0.16f, 0.17f, 0.18f, 0.19f, 1.2f,
                0.21f, 0.22f, 0.23f, 0.24f, 0.25f, 26f, 0.27f, 0.28f, 0.29f, 0.3f,
                0.31f, 0.32f, 0.33f, 0.34f, 0.55f, 0.1f, 0.2f, 0.3f, 0.9f, 0.39f,
                0.4f, 0.41f, 0.42f, 0.43f, 0.44f, 0.45f, 0.46f, 0.47f, 0.48f, 0.49f,
                0.5f, 2f, 0.52f, 0.53f, 0.54f, 55f, 0.56f, 0.57f, 0.58f, 0.59f,
                0.6f, 0.61f, 0.3f, 0.63f, 0.64f, 0.65f, 0.66f, 0.67f, 0.7f, 0.69f,
            ],
            name: "material"
        );
        Call(
            emit: static (b, m, v) => {
                b.Instance(
                    boundCenter: new Vector3(x: v[0], y: v[1], z: v[2]),
                    boundRadius: v[3],
                    emit: instance => instance.ResetPoint().Translate(offset: new Vector3(x: v[0], y: v[1], z: v[2])).Box(halfExtents: new Vector3(x: v[4], y: v[5], z: v[6]), material: m, round: 0.05f)
                );
                b.DynamicInstance(
                    boundOffset: new Vector3(x: v[7], y: 0f, z: 0f),
                    boundRadius: v[8],
                    emit: instance => instance.ResetPoint().TransformDynamic(slot: ((int)v[9])).Sphere(material: m, radius: v[10]),
                    slot: ((int)v[9])
                );
            },
            inputs: [1.5f, 2.5f, 3.5f, 2.25f, 0.45f, 0.35f, 0.65f, 0.55f, 1.75f, 1f, 0.8f],
            name: "instances"
        );
        Call(
            emit: static (b, m, v) => b.Instance(
                boundCenter: Vector3.Zero,
                boundRadius: v[0],
                emit: instance => instance.PushField()
                    .ResetPoint()
                    .Sphere(material: m, radius: v[1])
                    .ResetPoint()
                    .Scale(scale: new Vector3(value: v[2]))
                    .Box(halfExtents: new Vector3(x: v[3], y: v[4], z: v[5]), material: m, round: 0.05f)
                    .PopField()
            ),
            inputs: [2.5f, 0.6f, 1.25f, 0.35f, 0.45f, 0.55f],
            name: "part-programs"
        );

        return calls;
    }
    /// <summary>Builds one call's program from its own inputs.</summary>
    /// <param name="call">The call.</param>
    /// <returns>The program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is <see langword="null"/>.</exception>
    public static SdfProgram Build(SdfEncodingProbeCall call) {
        ArgumentNullException.ThrowIfNull(argument: call);

        return Build(
            call: call,
            inputs: call.Inputs
        );
    }
    /// <summary>Describes the encoding the probe exercises (<see cref="Calls"/>).</summary>
    /// <returns>The description.</returns>
    public static string Describe() =>
        Describe(calls: Calls());
    /// <summary>Describes the encoding a set of probe calls exercises. For each call: its packed program's word count and
    /// every word that holds an integer (its exponent bits clear of any float the probe produces, so a count, an offset,
    /// an enum, a flag or a packed bitfield), by offset; then, for each input, which words move when that input alone is
    /// raised: every integer word that moves with the bits its change spans, and every float word that moves; then, for
    /// each float word that moves, the input that moves it the most, or that the largest moves tie.</summary>
    /// <param name="calls">The calls.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calls"/> is <see langword="null"/>.</exception>
    public static string Describe(IReadOnlyList<SdfEncodingProbeCall> calls) =>
        Describe(
            calls: calls,
            wordsOf: static program => program.Words.ToArray()
        );
    /// <summary>Describes the encoding a set of probe calls exercises, as <see cref="Describe(IReadOnlyList{SdfEncodingProbeCall})"/>
    /// does, over the words a function takes from each packed program: the program's own, or those of a packer that laid
    /// them out otherwise.</summary>
    /// <param name="calls">The calls.</param>
    /// <param name="wordsOf">Returns the words to describe of a packed program.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calls"/> or <paramref name="wordsOf"/> is
    /// <see langword="null"/>.</exception>
    public static string Describe(IReadOnlyList<SdfEncodingProbeCall> calls, Func<SdfProgram, uint[]> wordsOf) {
        ArgumentNullException.ThrowIfNull(argument: calls);
        ArgumentNullException.ThrowIfNull(argument: wordsOf);

        var text = new StringBuilder();

        foreach (var call in calls) {
            var baseline = wordsOf(arg: Build(call: call));

            text.Append(provider: CultureInfo.InvariantCulture, handler: $"{call.Name} words={baseline.Length}");

            for (var index = 0; (index < baseline.Length); index++) {
                if ((baseline[index] != 0u) && IsInteger(word: baseline[index])) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $" {index}:{baseline[index]:X8}");
                }
            }

            text.Append(value: '\n');

            // For each float word, every input that moves it and by how much: the input that moves a word the most names
            // the field the word carries, even where one input moves many words (a normalized quaternion, say).
            var movers = new SortedDictionary<int, List<(int Input, float Move)>>();

            for (var input = 0; (input < call.Inputs.Count); input++) {
                var raised = call.Inputs.ToArray();

                raised[input] = Raised(value: raised[input]);

                var moved = wordsOf(arg: Build(call: call, inputs: raised));

                text.Append(provider: CultureInfo.InvariantCulture, handler: $"  input {input}:");

                if (moved.Length != baseline.Length) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $" words={moved.Length}");
                }

                var floats = new List<int>();

                for (var index = 0; (index < Math.Min(val1: baseline.Length, val2: moved.Length)); index++) {
                    if (baseline[index] == moved[index]) {
                        continue;
                    }

                    if (IsInteger(word: baseline[index]) && IsInteger(word: moved[index])) {
                        var change = baseline[index] ^ moved[index];

                        text.Append(provider: CultureInfo.InvariantCulture, handler: $" {index}[{BitOperations.TrailingZeroCount(value: change)}..{(31 - BitOperations.LeadingZeroCount(value: change))}]");

                        continue;
                    }

                    var move = MathF.Abs(x: (BitConverter.UInt32BitsToSingle(value: moved[index]) - BitConverter.UInt32BitsToSingle(value: baseline[index])));

                    if (!movers.TryGetValue(key: index, value: out var list)) {
                        movers.Add(key: index, value: (list = []));
                    }

                    list.Add(item: (input, (float.IsFinite(f: move) ? move : float.MaxValue)));
                    floats.Add(item: index);
                }

                if (floats.Count != 0) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $" floats={string.Join(separator: ',', values: floats)}");
                }

                text.Append(value: '\n');
            }

            if (movers.Count != 0) {
                text.Append(value: "  largest:");

                foreach (var (index, list) in movers) {
                    var ordered = list.OrderByDescending(keySelector: static mover => mover.Move).ToArray();
                    var named = ((ordered.Length == 1) || (ordered[0].Move > (ordered[1].Move * Margin)));

                    text.Append(provider: CultureInfo.InvariantCulture, handler: $" {index}<{(named ? ordered[0].Input.ToString(provider: CultureInfo.InvariantCulture) : "tie")}");
                }

                text.Append(value: '\n');
            }
        }

        return text.ToString();
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

    private static SdfProgram Build(SdfEncodingProbeCall call, IReadOnlyList<float> inputs) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var index = 1; (index < ProbeMaterials); index++) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        }

        call.Emit(arg1: builder, arg2: material, arg3: inputs);

        return builder.Build();
    }
    // Whether a word holds an integer rather than a float: its exponent bits are clear, which a float of any magnitude
    // the probe produces never has.
    private static bool IsInteger(uint word) =>
        ((word & 0x7F800000u) == 0u);
    // An input raised: an integral value by one, any other by a sixteenth of itself.
    private static float Raised(float value) =>
        ((value == MathF.Floor(x: value))
            ? (value + 1f)
            : (value * 1.0625f));
    // A unit sphere after the call's operation, so every call's program ends in a shape.
    private static SdfProgramBuilder Shape(SdfProgramBuilder b, int material) =>
        b.Sphere(material: material, radius: 1f);
}
