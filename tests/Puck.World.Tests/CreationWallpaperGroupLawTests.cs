using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A creation's wallpaper fold is refused where it enters: the canonicalizer refuses, by name at the fold's
/// <c>group</c>, a group it does not recognize and a group whose fold is discontinuous (the groups
/// <see cref="SdfProgram"/> would refuse at build), and normalizing keeps the authored group rather than substituting
/// another. Every mirror group is admitted as authored, and so is the limit and cell its lattice can take: a fractional
/// square limit, a finite hex limit and a cell too small to invert are refused at their own member.</summary>
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
    public void AFractionalSquareLimitIsRefusedAtTheLimit() {
        var document = Wallpaper(group: SdfWallpaperGroup.P4M, limit: new Vector2(x: 2f, y: 0.5f));

        CreationFixtures.AssertRefusesAt(document: document, path: "domain[0].limit");
        CreationFixtures.AssertRefusesNaming(document: document, needle: "whole number");
    }
    [Fact]
    public void AFiniteHexLimitIsRefusedAtTheLimitAndNamesTheBound() {
        foreach (var group in new[] { SdfWallpaperGroup.P3M1, SdfWallpaperGroup.P6M }) {
            var document = Wallpaper(group: group, limit: new Vector2(value: 2f));

            CreationFixtures.AssertRefusesAt(document: document, path: "domain[0].limit");
            CreationFixtures.AssertRefusesNaming(document: document, needle: "intersecting it with a bounding shape, not by limits");
        }
    }
    [Fact]
    public void AWholeSquareLimitAndAnAbsentLimitAreAdmitted() {
        foreach (var (group, limit) in new (SdfWallpaperGroup, Vector2?)[] { (SdfWallpaperGroup.Pmm, new Vector2(x: 2f, y: 5f)), (SdfWallpaperGroup.P4M, null), (SdfWallpaperGroup.P6M, null), (SdfWallpaperGroup.P3M1, new Vector2(value: SdfWallpaperFold.UnboundedLimit)) }) {
            _ = CreationCanonicalizer.Canonicalize(document: Wallpaper(group: group, limit: limit));
        }
    }
    // The stamper adds a domain's reach to a shape's render bound: a lattice with no edge has no reach to add, and
    // answers the program's own sentinel for an influence nothing contains, where 1e6 cells of 0.001 is a radius a
    // camera 30 units away can leave behind.
    [Fact]
    public void AnUnboundedWallpaperReachesAsFarAsNothingContains() {
        ShapeDomainOp[] unbounded = [
            new ShapeDomainOp.Wallpaper(Cell: new Vector2(value: 0.001f), Group: SdfWallpaperGroup.P6M),
            new ShapeDomainOp.Wallpaper(Cell: new Vector2(value: 1f), Group: SdfWallpaperGroup.Pmm, Limit: new Vector2(x: SdfWallpaperFold.UnboundedLimit, y: 2f)),
        ];

        foreach (var op in unbounded) {
            Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: ShapeDomainOps.Reach(domain: [op]));
        }
        Assert.InRange(
            actual: ShapeDomainOps.Reach(domain: [new ShapeDomainOp.Wallpaper(Cell: new Vector2(value: 1f), Group: SdfWallpaperGroup.P4M, Limit: new Vector2(value: 2f))]),
            low: 1f,
            high: 4f
        );
    }
    [Fact]
    public void ACellTooSmallToInvertIsRefusedAtTheCell() {
        var document = Wallpaper(group: SdfWallpaperGroup.Pmm, cell: new Vector2(value: 1.0e-19f));

        CreationFixtures.AssertRefusesAt(document: document, path: "domain[0].cell");
        CreationFixtures.AssertRefusesNaming(document: document, needle: "reciprocal");
    }
    [Fact]
    public void NormalizingKeepsAnUnrecognizedGroup() {
        var normalized = CreationCanonicalizer.Normalize(document: Wallpaper(group: ((SdfWallpaperGroup)99)));

        Assert.Equal(expected: ((SdfWallpaperGroup)99), actual: Assert.IsType<ShapeDomainOp.Wallpaper>(@object: normalized.Shapes![0].Domain![0]).Group);
    }

    private static CreationDocument Wallpaper(SdfWallpaperGroup group, Vector2? limit = null, Vector2? cell = null) => CreationFixtures.Document(
        name: "wallpaper",
        shapes: [CreationFixtures.UnitSphereShape with { Domain = [new ShapeDomainOp.Wallpaper(Cell: (cell ?? new Vector2(value: 4)), Group: group, Limit: limit)] }]
    );
}
