using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The <c>sdf-mesh-visibility</c> and <c>sdf-mesh-motion</c> canaries' expectations are an analytic oracle's, not
/// recorded colors. For every capture a leg's script takes, the oracle casts each pixel's camera ray, through the pixel
/// center the SDF march casts through, two ways: the fixed-point raycast (<see cref="SdfFieldEvaluator.Raycast"/>) of the
/// program the static stamper emits for rendering, and an analytic intersection with the triangles of the mesh draws
/// it emits beside the program. The raycast is the unbounded reference primary's mesh bound is checked against: it runs
/// from the camera's near plane to the render far distance, where the engine's march ends, and never stops at a mesh.
/// Only a converged march is an SDF surface, and no region is judged over a pixel whose march ended unproven. The nearer
/// surface is the pixel's, the mesh at an equal distance, and no surface is the background. The visibility debug view
/// colors each kind, so every <c>imageRegion</c> bound in the manifest is held to the oracle's colors over its region: a
/// bound that holds must contain every pixel, and a bound that must fail must miss one by more than its tolerance. A
/// placement or camera row edit the script makes before a capture is applied to the document the oracle reads for it,
/// and every slot of the layout a <c>view.override</c> last selected renders its own camera at its rect's extent under
/// the render scale tier the script last set, quantized as the render graph quantizes a footprint. Where that extent is
/// not the rect's, a capture pixel shows its kind only when every record in the reconstruction filter's footprint is that
/// kind, so no region is judged over a pixel that blends kinds, or over one no slot covers. The engine's march accepts a
/// surface within a pixel footprint of the ray, so no region is judged within two pixels of an SDF silhouette either,
/// nor over the background of a view that draws no mesh, which shows the sky outside the tiles the beam could not prove
/// empty.
/// </summary>
public sealed class SdfMeshCanaryOracleLawTests {
    // The visibility debug view's colors (passes/sdf-render-view.hlsli's renderView, mode 11), background first.
    // A pixel whose fixed-point march ended without proving its answer: no region may be judged over one.
    private const int Inconclusive = 3;
    // A capture pixel a reduced render scale reconstructs from records of more than one kind, so its color blends them:
    // no region may be judged over one.
    private const int Mixed = 4;
    // A capture pixel no slot of the layout covers: no region may be judged over one.
    private const int Outside = 5;
    // A pixel within FootprintReach of an SDF silhouette the oracle's march misses. The engine's march accepts a surface
    // within one pixel footprint of the ray (max(SurfaceEpsilon, footprint * t), march/sdf-cone.hlsli), so its
    // silhouettes reach past the oracle's: no region may be judged over one.
    private const int Fringe = 6;
    // A background pixel of a view that draws no mesh. The hit passes then run only over the box of tiles the beam could
    // not prove empty (passes/sdf-cull-args.comp.hlsl), and a pixel outside it shows the sky rather than the background
    // kind; the box rests on the beam's conservative cone proofs, which the oracle does not replay, so no region may be
    // judged over one.
    private const int Flattened = 7;
    // The reconstruction's reach, in source texels, on each side of a destination pixel's sample point: the clamped
    // Catmull-Rom filter of the place pass reads a four-by-four footprint, and bilinear reads within it.
    private const int ReconstructionReach = 2;
    // How far, in pixels of a view's extent on each axis, the engine's footprint acceptance carries an SDF silhouette
    // past the oracle's. One footprint is a pixel across its ray, and a ray grazing a chamfered edge or an opening's
    // corner stays within it along a span; the captures of both backends show silhouettes up to two pixels past.
    private const int FootprintReach = 2;

    private static readonly Vector3[] KindColors = [
        new(x: 0.02f, y: 0.05f, z: 0.28f),
        new(x: 0.15f, y: 0.90f, z: 0.25f),
        new(x: 0.95f, y: 0.55f, z: 0.10f),
    ];

    [InlineData("sdf-mesh-visibility")]
    [InlineData("sdf-mesh-motion")]
    [Theory]
    public void EveryRegionBoundHoldsExactlyWhenTheOracleSaysItDoes(string canary) {
        var directory = $"tests/Puck.World.Canaries/{canary}";
        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: directory,
            path3: "canary.json"
        )));
        var bounds = 0;

        foreach (var leg in ((ReadOnlySpan<string>)["positive", "discriminating"])) {
            var section = manifest.RootElement.GetProperty(propertyName: leg);
            var captures = Captures(
                definition: AuthoredGameFixtures.Load(relativePath: section.GetProperty(propertyName: "world").GetString()!),
                script: Path.Combine(
                    path1: AuthoredGameFixtures.Root,
                    path2: directory,
                    path3: section.GetProperty(propertyName: "script").GetString()!
                )
            );
            var kinds = new Dictionary<string, int[,]>(comparer: StringComparer.Ordinal);

            foreach (var expectation in section.GetProperty(propertyName: "expect").EnumerateArray()) {
                if (expectation.GetProperty(propertyName: "type").GetString() != "imageRegion") {
                    continue;
                }

                var capture = expectation.GetProperty(propertyName: "capture").GetString()!;
                var extent = expectation.GetProperty(propertyName: "extent");
                var width = extent[0].GetInt32();
                var height = extent[1].GetInt32();

                if (!kinds.TryGetValue(
                    key: capture,
                    value: out var grid
                )) {
                    grid = KindsOf(
                        height: height,
                        state: captures[capture],
                        width: width
                    );
                    kinds[capture] = grid;
                }

                var name = expectation.GetProperty(propertyName: "name").GetString();

                Assert.True(
                    condition: (Holds(
                        expectation: expectation,
                        kinds: grid
                    ) == expectation.GetProperty(propertyName: "holds").GetBoolean()),
                    userMessage: $"{canary} {leg} {name}: the oracle disagrees with the bound's expected outcome."
                );
                bounds++;
            }
        }

        Assert.True(condition: (bounds > 0));
    }

    // What a capture is taken under: the leg's document with every row edit the script made before it, the layout a
    // view.override selected (null for the one the composer picks with no seat joined) and the render scale.
    private sealed record CaptureState(WorldDefinition Definition, string? Layout, float RenderScale);

    // The state each capture the script names is taken under: the leg's world with every placement position and camera
    // row the script set before that screenshot, the layout it last selected, and the render scale it last set.
    private static Dictionary<string, CaptureState> Captures(WorldDefinition definition, string script) {
        var captures = new Dictionary<string, CaptureState>(comparer: StringComparer.Ordinal);
        var current = new CaptureState(
            Definition: definition,
            Layout: null,
            RenderScale: 1f
        );

        foreach (var raw in File.ReadLines(path: script)) {
            var line = raw.Trim();

            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.screenshot ")) {
                captures[Path.GetFileName(path: line["world.screenshot ".Length..])] = current;
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set placements ")) {
                var words = line.Split(separator: ' ', count: 5);

                Assert.Equal(expected: "position", actual: words[3]);

                var coordinates = words[4].Trim('[', ']').Split(separator: ',').Select(selector: static value => float.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: value
                )).ToArray();
                var position = new DocumentVector3(value: new Vector3(
                    x: coordinates[0],
                    y: coordinates[1],
                    z: coordinates[2]
                ));

                current = (current with {
                    Definition = (current.Definition with {
                        PlacementRowsRaw = [.. current.Definition.Placements.Select(selector: placement => (string.Equals(
                            a: placement.Id,
                            b: words[2],
                            comparisonType: StringComparison.Ordinal
                        )
                            ? (placement with { Position = position })
                            : placement))],
                    }),
                });
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set cameras ")) {
                var camera = JsonSerializer.Deserialize(
                    json: line["world.row.set cameras ".Length..],
                    jsonTypeInfo: WorldJsonContext.Default.WorldCamera
                )!;

                current = (current with {
                    Definition = (current.Definition with {
                        CamerasRaw = [.. current.Definition.Cameras.Select(selector: row => (string.Equals(
                            a: row.Name,
                            b: camera.Name,
                            comparisonType: StringComparison.Ordinal
                        )
                            ? camera
                            : row))],
                    }),
                });
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "view.override layout ")) {
                var layout = line["view.override layout ".Length..].Trim();

                current = (current with {
                    Layout = ((layout == "auto")
                        ? null
                        : layout),
                });
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.render-scale ")) {
                Assert.True(condition: WorldRenderScaleTiers.TryParse(
                    name: line["world.render-scale ".Length..],
                    tier: out var tier
                ), userMessage: $"the oracle reads a named render-scale tier: {line}");

                current = (current with { RenderScale = WorldRenderScaleTiers.Scale(tier: tier) });
            }
        }

        return captures;
    }
    // Each capture pixel's kind: 0 background, 1 SDF, 2 mesh, or why no region may be judged over it (inconclusive, mixed
    // or outside). Every slot of the layout renders its camera at its rect's extent under the render scale, quantized as
    // the render graph quantizes a footprint, and the place pass copies it into the rect at native scale or reconstructs
    // it from a footprint of records otherwise.
    private static int[,] KindsOf(CaptureState state, int width, int height) {
        var definition = state.Definition;
        var layouts = definition.Views.Layouts;
        var layout = ((state.Layout is { } name)
            ? layouts.Single(predicate: row => string.Equals(
                a: row.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            ))
            : layouts.First(predicate: static row => (row.SeatCount == 0)));
        var kinds = new int[width, height];

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                kinds[x, y] = Outside;
            }
        }

        foreach (var slot in layout.Slots) {
            var left = ((int)Math.Round(a: (slot.X * width)));
            var top = ((int)Math.Round(a: (slot.Y * height)));
            var rectWidth = (((int)Math.Round(a: ((slot.X + slot.Width) * width))) - left);
            var rectHeight = (((int)Math.Round(a: ((slot.Y + slot.Height) * height))) - top);
            var renderWidth = RenderPixels(
                display: width,
                fraction: slot.Width,
                scale: state.RenderScale
            );
            var renderHeight = RenderPixels(
                display: height,
                fraction: slot.Height,
                scale: state.RenderScale
            );
            var rendered = ViewKinds(
                camera: Camera(
                    definition: definition,
                    height: ((uint)renderHeight),
                    name: slot.Camera!,
                    width: ((uint)renderWidth)
                ),
                definition: definition,
                height: renderHeight,
                width: renderWidth
            );
            var exact = ((renderWidth == rectWidth) && (renderHeight == rectHeight));

            for (var y = 0; (y < rectHeight); y++) {
                for (var x = 0; (x < rectWidth); x++) {
                    kinds[(left + x), (top + y)] = (exact
                        ? rendered[x, y]
                        : Reconstructed(
                            rendered: rendered,
                            x: ((((x + 0.5) / rectWidth) * renderWidth) - 0.5),
                            y: ((((y + 0.5) / rectHeight) * renderHeight) - 0.5)
                        ));
                }
            }
        }

        return kinds;
    }
    // A view's extent along one axis: its rect's fraction of the display under the render scale, quantized as the render
    // graph quantizes a footprint.
    private static int RenderPixels(double fraction, int display, float scale) => RenderGraphExtent.Pixels(
        display: display,
        fraction: RenderGraphExtent.Quantize(fraction: (fraction * (((scale > 0f) && (scale < 1f)) ? scale : 1f)))
    );
    // The kind a reconstructed pixel shows: the one kind of every record in its filter's footprint around the sample
    // point (in source texels, clamped to the image), mixed when the footprint holds more than one, or the first record
    // no region may be judged over.
    private static int Reconstructed(int[,] rendered, double x, double y) {
        var width = rendered.GetLength(dimension: 0);
        var height = rendered.GetLength(dimension: 1);
        var kind = -1;
        var baseColumn = ((int)Math.Floor(d: x));
        var baseRow = ((int)Math.Floor(d: y));

        for (var row = (baseRow - (ReconstructionReach - 1)); (row <= (baseRow + ReconstructionReach)); row++) {
            for (var column = (baseColumn - (ReconstructionReach - 1)); (column <= (baseColumn + ReconstructionReach)); column++) {
                var sample = rendered[Math.Clamp(max: (width - 1), min: 0, value: column), Math.Clamp(max: (height - 1), min: 0, value: row)];

                if (sample >= Inconclusive) {
                    return sample;
                }

                if ((kind >= 0) && (sample != kind)) {
                    return Mixed;
                }

                kind = sample;
            }
        }

        return kind;
    }
    // Each pixel's kind in one view rendered at its extent: 0 background, 1 SDF, 2 mesh, or inconclusive where the
    // fixed-point march ended without proving its answer. The march runs to the far distance the engine's ends at, and
    // no bound the mesh would set.
    private static int[,] ViewKinds(WorldDefinition definition, CameraSnapshot camera, int width, int height) {
        var draws = new List<SdfMeshDraw>();
        var builder = new SdfProgramBuilder();

        // The program the static stamper emits for rendering and the mesh draws beside it, as the presenter hands both
        // to the engine.
        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: definition.Creations,
            definition: definition,
            meshDraws: draws,
            placements: definition.Placements
        );

        var triangles = Triangles(draws: draws);
        var program = builder.Build();
        var farDistance = FixedQ4816.FromDouble(value: WorldRenderFarDistance.Resolve(defaults: definition.Render));
        var kinds = new int[width, height];
        var sdfDistances = new double?[width, height];
        var meshDistances = new double?[width, height];

        // Rows cast in parallel, each worker over an evaluator of its own; every pixel writes only its own cells, so the
        // result is the serial one.
        _ = Parallel.For(
            fromInclusive: 0,
            toExclusive: height,
            localInit: () => new SdfFieldEvaluator(program: program),
            localFinally: static _ => { },
            body: (y, _, field) => {
                for (var x = 0; (x < width); x++) {
                    var direction = Direction(
                        camera: camera,
                        height: height,
                        width: width,
                        x: x,
                        y: y
                    );
                    var near = (((double)SdfWorldEngine.ConeNear) / Vector3.Dot(
                        vector1: direction,
                        vector2: camera.Forward
                    ));
                    var marched = field.Raycast(
                        dir: FixedVector3.FromVector3(value: direction),
                        hit: out var hit,
                        maxDist: (farDistance - FixedQ4816.FromDouble(value: near)),
                        origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: (camera.Position + (((float)near) * direction))))
                    );

                    // A march that could not prove its answer proves neither a surface nor its absence.
                    if (marched && (hit.Confidence != WorldQueryConfidence.Exact)) {
                        kinds[x, y] = Inconclusive;

                        continue;
                    }

                    double? sdf = (marched ? (((double)hit.Distance) + near) : null);
                    var mesh = Nearest(
                        direction: direction,
                        near: near,
                        origin: camera.Position,
                        triangles: triangles
                    );

                    sdfDistances[x, y] = sdf;
                    meshDistances[x, y] = mesh;
                    kinds[x, y] = (((mesh is { } meshDistance) && ((sdf is null) || (meshDistance <= sdf)))
                        ? 2
                        : ((sdf is null) ? 0 : 1));
                }

                return field;
            });

        return Unjudged(
            kinds: kinds,
            meshDistances: meshDistances,
            meshless: (draws.Count == 0),
            sdfDistances: sdfDistances
        );
    }
    // A view's kinds with every pixel no region may be judged over marked: each one beside a pixel whose ray meets an SDF
    // surface nearer than this pixel's own mesh, which the engine's footprint acceptance may take for that surface (a
    // neighbour's surface counts where a mesh in front of it hides it there), and each background pixel of a view that
    // draws no mesh.
    private static int[,] Unjudged(int[,] kinds, double?[,] sdfDistances, double?[,] meshDistances, bool meshless) {
        var width = kinds.GetLength(dimension: 0);
        var height = kinds.GetLength(dimension: 1);
        var marked = ((int[,])kinds.Clone());

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                if ((kinds[x, y] == 1) || (kinds[x, y] == Inconclusive)) {
                    continue;
                }

                if (meshless && (kinds[x, y] == 0)) {
                    marked[x, y] = Flattened;

                    continue;
                }

                for (var row = Math.Max(val1: 0, val2: (y - FootprintReach)); (row <= Math.Min(val1: (height - 1), val2: (y + FootprintReach))); row++) {
                    for (var column = Math.Max(val1: 0, val2: (x - FootprintReach)); (column <= Math.Min(val1: (width - 1), val2: (x + FootprintReach))); column++) {
                        if ((sdfDistances[column, row] is { } sdf) && ((meshDistances[x, y] is not { } mesh) || (sdf < mesh))) {
                            marked[x, y] = Fringe;
                        }
                    }
                }
            }
        }

        return marked;
    }
    // A camera row as the World resolves it: its rig's eye, target and field of view at the extent its view renders at.
    private static CameraSnapshot Camera(WorldDefinition definition, string name, uint width, uint height) {
        var (eye, target, fieldOfView) = WorldCameraRigCompiler.Compile(
            definition: definition,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition)),
            program: definition.Cameras.Single(predicate: row => string.Equals(
                a: row.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )).Rig
        ).Resolve(
            anchor: new SdfAnchor(
                Orientation: Quaternion.Identity,
                Position: Vector3.Zero
            ),
            clock: new SdfCameraClock(
                AuthoritativeTick: 0UL,
                PresentationSeconds: 0f
            )
        );

        return CameraSnapshot.LookAt(
            fieldOfViewRadians: fieldOfView,
            position: eye,
            target: target,
            viewportHeight: height,
            viewportWidth: width
        );
    }
    // The normalized ray through a pixel's center, as march/sdf-cone.hlsli's cameraRayDirection casts it.
    private static Vector3 Direction(CameraSnapshot camera, int width, int height, int x, int y) {
        var ndcX = ((((x + 0.5) / width) * 2.0) - 1.0);
        var ndcY = -((((y + 0.5) / height) * 2.0) - 1.0);

        return Vector3.Normalize(value: ((camera.Forward + (((float)((ndcX * camera.AspectRatio) * camera.TanHalfFieldOfView)) * camera.Right)) + (((float)(ndcY * camera.TanHalfFieldOfView)) * camera.Up)));
    }
    // Every draw's world-space triangles.
    private static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(List<SdfMeshDraw> draws) {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();

        foreach (var draw in draws) {
            var positions = draw.Mesh.Positions.Span;
            var indices = draw.Mesh.Indices.Span;

            for (var index = 0; (index < indices.Length); index += 3) {
                triangles.Add(item: (
                    Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[((int)indices[index])]),
                    Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[((int)indices[(index + 1)])]),
                    Vector3.Transform(matrix: draw.ObjectToWorld, position: positions[((int)indices[(index + 2)])])
                ));
            }
        }

        return triangles;
    }
    // The distance along a unit ray to the nearest triangle beyond the near plane (Möller–Trumbore), or none.
    private static double? Nearest(Vector3 origin, Vector3 direction, double near, List<(Vector3 A, Vector3 B, Vector3 C)> triangles) {
        double? nearest = null;

        foreach (var (a, b, c) in triangles) {
            var e1 = (b - a);
            var e2 = (c - a);
            var p = Vector3.Cross(vector1: direction, vector2: e2);
            var determinant = ((double)Vector3.Dot(vector1: e1, vector2: p));

            if (Math.Abs(value: determinant) < 1e-12) {
                continue;
            }

            var s = (origin - a);
            var u = (Vector3.Dot(vector1: s, vector2: p) / determinant);
            var q = Vector3.Cross(vector1: s, vector2: e1);
            var v = (Vector3.Dot(vector1: direction, vector2: q) / determinant);
            var t = (Vector3.Dot(vector1: e2, vector2: q) / determinant);

            if ((u >= 0.0) && (v >= 0.0) && ((u + v) <= 1.0) && (t >= near) && ((nearest is null) || (t < nearest))) {
                nearest = t;
            }
        }

        return nearest;
    }
    // Whether an imageRegion bound holds over the oracle's colors, judged as the canary runner judges a capture: every
    // pixel whose center lies in the region, each channel within the bound widened by the tolerance.
    private static bool Holds(JsonElement expectation, int[,] kinds) {
        var region = expectation.GetProperty(propertyName: "region");
        var tolerance = expectation.GetProperty(propertyName: "toleranceCodes").GetDouble();
        var minimum = (expectation.TryGetProperty(propertyName: "minimum", value: out var low) ? low : (JsonElement?)null);
        var maximum = (expectation.TryGetProperty(propertyName: "maximum", value: out var high) ? high : (JsonElement?)null);
        var width = kinds.GetLength(dimension: 0);
        var height = kinds.GetLength(dimension: 1);

        for (var y = 0; (y < height); y++) {
            var centerY = ((y + 0.5) / height);

            if ((centerY < region[1].GetDouble()) || (centerY > region[3].GetDouble())) {
                continue;
            }

            for (var x = 0; (x < width); x++) {
                var centerX = ((x + 0.5) / width);

                if ((centerX < region[0].GetDouble()) || (centerX > region[2].GetDouble())) {
                    continue;
                }

                if (kinds[x, y] >= Inconclusive) {
                    Assert.Fail(message: $"{expectation.GetProperty(propertyName: "name").GetString()}: pixel ({x}, {y}) is {UnjudgedName(kind: kinds[x, y])} in the oracle.");
                }

                var color = KindColors[kinds[x, y]];
                ReadOnlySpan<double> codes = [(color.X * 255.0), (color.Y * 255.0), (color.Z * 255.0), 255.0];

                for (var channel = 0; (channel < 4); channel++) {
                    if (
                        ((minimum is { } lower) && (codes[channel] < ((lower[channel].GetDouble() * 255.0) - tolerance))) ||
                        ((maximum is { } upper) && (codes[channel] > ((upper[channel].GetDouble() * 255.0) + tolerance)))
                    ) {
                        return false;
                    }
                }
            }
        }

        return true;
    }
    private static string UnjudgedName(int kind) => kind switch {
        Inconclusive => "inconclusive",
        Mixed => "mixed",
        Outside => "outside every slot",
        Fringe => "on an SDF silhouette's fringe",
        Flattened => "background in a view that draws no mesh",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind)),
    };
}
