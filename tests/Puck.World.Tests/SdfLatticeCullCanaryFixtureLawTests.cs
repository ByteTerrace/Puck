using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The premises of the <c>sdf-lattice-cull</c> canary that the CPU can hold, so the GPU leg only has to confirm what
/// the pixels show: its placement stamps one cullable instance bounded by the clip box (the lattice has no edge, the box
/// does), the bound contains every point of the box, and the lattice covers the box's front face, so that face is the one
/// emissive grey the canary expects across its footprint.</summary>
public sealed class SdfLatticeCullCanaryFixtureLawTests {
    private const string Fixture = "tests/Puck.World.Canaries/sdf-lattice-cull/fixture.world.json";

    private static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(message: "No checkout holds the test assembly.");
    }
    private static WorldDefinition Load() => WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: Path.Combine(path1: RepositoryRoot(), path2: Fixture)));
    private static SdfInstanceCost Stamp() {
        var definition = Load();
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: definition.Creations,
            definition: definition,
            placements: definition.Placements
        );

        return builder.Build().InspectInstance(index: 0);
    }

    [Fact]
    public void TheLatticePlacementIsOneCullableInstanceBoundedByTheClipBox() {
        var cost = Stamp();
        var boxReach = Load().Creations[0].Document.Shapes![1].Scale.Value.Length();

        Assert.False(condition: cost.Unmaskable, userMessage: "the clipped lattice packed the unmaskable bound");
        Assert.InRange(actual: cost.BoundRadius, low: boxReach, high: (boxReach + 1f));
    }
    [Fact]
    public void TheLatticeCoversTheBoxFrontFaceSoItIsOneGreyAcrossTheFootprint() {
        var shapes = Load().Creations[0].Document.Shapes!;
        var wallpaper = Assert.IsType<ShapeDomainOp.Wallpaper>(@object: shapes[0].Domain![0]);
        var radius = shapes[0].Scale.Value.X;
        var half = shapes[1].Scale.Value;
        var cell = wallpaper.Cell.Value;
        var limit = new Vector2(value: SdfWallpaperFold.UnboundedLimit);
        var face = half.Z;
        var random = new Random(Seed: 2071);

        for (var sample = 0; (sample < 4000); sample++) {
            var x = (((((float)random.NextDouble()) * 2f) * half.X) - half.X);
            var y = (((((float)random.NextDouble()) * 2f) * half.Y) - half.Y);
            var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: SdfWallpaperGroup.P6M, limit: limit, point: new Vector2(x: x, y: y));

            Assert.True(
                condition: (new Vector3(x: folded.X, y: folded.Y, z: face).Length() <= radius),
                userMessage: $"the lattice does not reach the box's front face at ({x}, {y}): folded ({folded.X}, {folded.Y})"
            );
        }
    }
}
