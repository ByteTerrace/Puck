using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
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
/// bound that holds must contain every pixel, and a bound that must fail must miss one by more than its tolerance. A row edit the script makes before a
/// capture is applied to the document the oracle reads for it.
/// </summary>
public sealed class SdfMeshCanaryOracleLawTests {
    // The visibility debug view's colors (sdf-world.hlsli's renderView, mode 11), background first.
    // A pixel whose fixed-point march ended without proving its answer: no region may be judged over one.
    private const int Inconclusive = 3;
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

        foreach (var leg in (ReadOnlySpan<string>)["positive", "discriminating"]) {
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
                        definition: captures[capture],
                        height: height,
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

    // The document each capture the script names is taken under: the leg's world with every placement position the script
    // set before that screenshot.
    private static Dictionary<string, WorldDefinition> Captures(WorldDefinition definition, string script) {
        var captures = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal);
        var current = definition;

        foreach (var raw in File.ReadLines(path: script)) {
            var line = raw.Trim();

            if (line.StartsWith(value: "world.screenshot ", comparisonType: StringComparison.Ordinal)) {
                captures[Path.GetFileName(path: line["world.screenshot ".Length..])] = current;
            } else if (line.StartsWith(value: "world.row.set placements ", comparisonType: StringComparison.Ordinal)) {
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
                    PlacementRowsRaw = [.. current.Placements.Select(selector: placement => (string.Equals(
                        a: placement.Id,
                        b: words[2],
                        comparisonType: StringComparison.Ordinal
                    )
                        ? (placement with { Position = position })
                        : placement))],
                });
            }
        }

        return captures;
    }
    // Each pixel's kind in a capture of the document's first camera: 0 background, 1 SDF, 2 mesh, or inconclusive where the
    // fixed-point march ended without proving its answer. The march runs to the far distance the engine's ends at, and
    // no bound the mesh would set.
    private static int[,] KindsOf(WorldDefinition definition, int width, int height) {
        var camera = Camera(
            definition: definition,
            height: ((uint)height),
            width: ((uint)width)
        );

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
        var field = new SdfFieldEvaluator(program: builder.Build());
        var farDistance = FixedQ4816.FromDouble(value: WorldRenderFarDistance.Resolve(defaults: definition.Render));
        var kinds = new int[width, height];

        for (var y = 0; (y < height); y++) {
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

                kinds[x, y] = ((mesh is { } meshDistance && ((sdf is null) || (meshDistance <= sdf)))
                    ? 2
                    : ((sdf is null) ? 0 : 1));
            }
        }

        return kinds;
    }
    // The document's first camera as the World resolves it: its rig's eye, target and field of view at the capture's
    // extent.
    private static CameraSnapshot Camera(WorldDefinition definition, uint width, uint height) {
        var (eye, target, fieldOfView) = WorldCameraRigCompiler.Compile(
            definition: definition,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition)),
            program: definition.Cameras[0].Rig
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
    // The normalized ray through a pixel's center, as sdf-world.hlsli's cameraRayDirection casts it.
    private static Vector3 Direction(CameraSnapshot camera, int width, int height, int x, int y) {
        var ndcX = ((((x + 0.5) / width) * 2.0) - 1.0);
        var ndcY = -((((y + 0.5) / height) * 2.0) - 1.0);

        return Vector3.Normalize(value: ((camera.Forward + (((float)(ndcX * camera.AspectRatio * camera.TanHalfFieldOfView)) * camera.Right)) + (((float)(ndcY * camera.TanHalfFieldOfView)) * camera.Up)));
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
            var determinant = (double)Vector3.Dot(vector1: e1, vector2: p);

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

                Assert.True(
                    condition: (kinds[x, y] != Inconclusive),
                    userMessage: $"{expectation.GetProperty(propertyName: "name").GetString()}: pixel ({x}, {y}) is inconclusive in the oracle."
                );

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
}
