using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A creation's wallpaper fold is refused where it enters: the canonicalizer refuses, by name at the fold's
/// <c>group</c>, a group it does not recognize and a group whose fold is discontinuous (the groups
/// <see cref="SdfProgram"/> would refuse at build), and normalizing keeps the authored group rather than substituting
/// another. Every mirror group is admitted as authored.</summary>
public sealed class CreationWallpaperGroupLawTests {
    [Fact]
    public void AnUnrecognizedGroupIsRefusedAtTheGroup() {
        var document = Wallpaper(group: ((SdfWallpaperGroup)99));

        CreationFixtures.AssertRefusesAt(document: document, path: "domain[0].group");
        CreationFixtures.AssertRefusesNaming(document: document, needle: "group '99' is not recognized");
    }
    [Fact]
    public void EveryDiscontinuousGroupIsRefusedByName() {
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: static group => !SdfWallpaperFold.IsContinuous(group: group))) {
            CreationFixtures.AssertRefusesNaming(document: Wallpaper(group: group), needle: $"group '{group}' folds discontinuously");
        }
    }
    [Fact]
    public void EveryMirrorGroupIsAdmittedAsAuthored() {
        foreach (var group in Enum.GetValues<SdfWallpaperGroup>().Where(predicate: SdfWallpaperFold.IsContinuous)) {
            var canonical = CreationCanonicalizer.Canonicalize(document: Wallpaper(group: group));

            Assert.Equal(expected: group, actual: Assert.IsType<ShapeDomainOp.Wallpaper>(@object: canonical.Document.Shapes![0].Domain![0]).Group);
        }
    }
    [Fact]
    public void NormalizingKeepsAnUnrecognizedGroup() {
        var normalized = CreationCanonicalizer.Normalize(document: Wallpaper(group: ((SdfWallpaperGroup)99)));

        Assert.Equal(expected: ((SdfWallpaperGroup)99), actual: Assert.IsType<ShapeDomainOp.Wallpaper>(@object: normalized.Shapes![0].Domain![0]).Group);
    }

    private static CreationDocument Wallpaper(SdfWallpaperGroup group) => CreationFixtures.Document(
        name: "wallpaper",
        shapes: [CreationFixtures.UnitSphereShape with { Domain = [new ShapeDomainOp.Wallpaper(Cell: new System.Numerics.Vector2(value: 4), Group: group)] }]
    );
}
