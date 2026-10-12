using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm;
using Puck.SignedDistance.Baking;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>sdf-bake-impostor</c> canary's world puts its bake where the canary's claims hold. Seen from
/// the fixture's camera, the bake's bounding sphere projects well under the impostor's sixteen-pixel switch, at a coarse
/// level of its chains (more than one and a half texels a pixel), so a view records the card, and the pixels the canary
/// compares, one a side of the middle, land inside the two materials' boxes. A canary that sits at the wrong distance
/// records only the mesh and proves nothing about the card; this law refuses that on the CPU.
/// </summary>
public sealed class BakeImpostorCanaryGeometryLawTests {
    // The fixture's camera, read from its document: anchor and look-at point, filmed at 1024x576 under a field of view of 0.9.
    private const string Fixture = "tests/Puck.World.Canaries/sdf-bake-impostor/fixture.puck";
    private const float FieldOfView = 0.9f;

    private static Vector3 PointOf(string text, string operation) {
        var match = System.Text.RegularExpressions.Regex.Match(input: text, pattern: (operation + @"\(subject: worldPoint\(point: \[([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\]\)\)"));

        Assert.True(condition: match.Success, userMessage: $"the fixture has no {operation} point");

        return new Vector3(
            x: float.Parse(provider: System.Globalization.CultureInfo.InvariantCulture, s: match.Groups[1].Value),
            y: float.Parse(provider: System.Globalization.CultureInfo.InvariantCulture, s: match.Groups[2].Value),
            z: float.Parse(provider: System.Globalization.CultureInfo.InvariantCulture, s: match.Groups[3].Value)
        );
    }

    [Fact]
    public void TheCanarysBakeIsACardAtACoarseLevelOverTheRegionsItComparesInsideEachMaterial() {
        var definition = AuthoredGameFixtures.Load(relativePath: Fixture);
        var text = File.ReadAllText(path: Path.Combine(path1: AuthoredGameFixtures.Root, path2: Fixture));

        var (eye, target) = (PointOf(operation: "anchor", text: text), PointOf(operation: "lookAt", text: text));
        var creation = Assert.Single(collection: definition.Creations);

        Assert.True(condition: CreationBaker.TryBake(bake: out var bake, cancellationToken: TestContext.Current.CancellationToken, document: creation.EngineDocument, quality: WorldBakeChunk.Quality, reason: out var reason), userMessage: reason);

        var impostor = new SdfMeshImpostor(impostor: bake!.Impostor);
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: FieldOfView, position: eye, target: target, viewportHeight: 576u, viewportWidth: 1024u);
        var perUnit = SdfMeshLod.PixelsPerUnitDepth(renderHeight: 576f, tanHalfFieldOfView: camera.TanHalfFieldOfView);
        var lod = SdfMeshLod.ForImpostor(far: true, impostor: impostor);
        var pixels = lod.ProjectedPixels(cameraForward: camera.Forward, cameraPosition: camera.Position, objectToWorld: Matrix4x4.Identity, pixelsPerUnitDepth: perUnit);

        Assert.True(condition: (pixels < (0.75f * lod.SwitchPixels)), userMessage: $"the bounding sphere spans {pixels} pixels");

        var texelsAPixel = (impostor.ViewTexels / pixels);

        Assert.Equal(expected: 1, actual: ((int)MathF.Round(x: MathF.Log2(x: texelsAPixel))));

        // The pixels the canary compares: columns 510 and 513 of 1024 (centres 1.5 pixels either side of the middle) on the
        // rows 287 and 288 (centres half a pixel either side), in metres at the object's depth.
        var metresAPixel = (camera.Position.Z / perUnit);
        var left = SpanOf(bake: bake, material: 0);
        var right = SpanOf(bake: bake, material: 1);

        Assert.InRange(actual: (-1.5f * metresAPixel), high: left.MaxX, low: left.MinX);
        Assert.InRange(actual: (1.5f * metresAPixel), high: right.MaxX, low: right.MinX);

        foreach (var row in new[] { -0.5f, 0.5f }) {
            var y = (target.Y + (row * metresAPixel));

            Assert.InRange(actual: y, high: left.MaxY, low: left.MinY);
            Assert.InRange(actual: y, high: right.MaxY, low: right.MinY);
        }
    }

    // The extent of the baked mesh's triangles of one material entry.
    private static (float MinX, float MaxX, float MinY, float MaxY) SpanOf(SdfBake bake, uint material) {
        var identity = bake.Textures.Single(predicate: static texture => (texture.Usage == SdfBakeTextureUsage.Material));
        var texels = identity.Levels[0];
        var vertices = bake.Mesh.Vertices;
        var indices = bake.Mesh.Indices;

        var (minX, maxX, minY, maxY) = (float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);

        for (var triangle = 0; (triangle < (indices.Length / 3)); triangle++) {
            var centroid = (((vertices[indices[(3 * triangle)]].Uv + vertices[indices[((3 * triangle) + 1)]].Uv) + vertices[indices[((3 * triangle) + 2)]].Uv) / 3f);
            var x = Math.Clamp(max: (identity.Width - 1), min: 0, value: ((int)(centroid.X * identity.Width)));
            var y = Math.Clamp(max: (identity.Height - 1), min: 0, value: ((int)(centroid.Y * identity.Height)));

            if (texels[((y * identity.Width) + x)] != material) {
                continue;
            }

            for (var corner = 0; (corner < 3); corner++) {
                var position = vertices[indices[((3 * triangle) + corner)]].Position;

                (minX, maxX, minY, maxY) = (MathF.Min(x: minX, y: position.X), MathF.Max(x: maxX, y: position.X), MathF.Min(x: minY, y: position.Y), MathF.Max(x: maxY, y: position.Y));
            }
        }

        return (minX, maxX, minY, maxY);
    }
}
