using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfMeshRegionArena"/>: a mesh a list stops naming keeps its words, so a list naming it again places
/// nothing, and the arena packs only once the words of meshes no list names outnumber both the named meshes' words and
/// the retained slack.
/// </summary>
public sealed class SdfMeshRegionArenaLawTests {
    [Fact]
    public void AMeshNamedAgainKeepsItsWordsAndTheArenaPacksOnlyPastItsSlack() {
        var body = Triangle(offset: 0f);
        var room = Triangle(offset: 1f);
        var arena = new SdfMeshRegionArena(retainedSlackWords: SdfMeshRegion.WordsOf(mesh: body));

        arena.Plan(draws: [Draw(mesh: room), Draw(mesh: body)]);
        var placed = arena.Placements[body];

        // The body leaves: its words stay where they lie, and the list places nothing.
        arena.Plan(draws: [Draw(mesh: room)]);
        Assert.Equal(expected: (false, 0, placed), actual: (arena.Relocated, arena.Placed.Count, arena.Placements[body]));

        // The body returns: it takes the words it left, and the list still places nothing.
        arena.Plan(draws: [Draw(mesh: room), Draw(mesh: body)]);
        Assert.Equal(expected: (false, 0, placed), actual: (arena.Relocated, arena.Placed.Count, arena.Placements[body]));

        // Past the slack, once the words no list names outnumber the named meshes', the arena packs what the list names.
        var other = Triangle(offset: 2f);

        arena.Plan(draws: [Draw(mesh: other)]);
        Assert.True(condition: arena.Relocated);
        Assert.Equal(expected: [other], actual: arena.Placements.Keys);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfMeshRegionArena(retainedSlackWords: -1));
    }

    private static SdfMesh Triangle(float offset) => new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { new(x: offset, y: 0f, z: 0f), new(x: (offset + 1f), y: 0f, z: 0f), new(x: offset, y: 1f, z: 0f) }
    );
    private static SdfMeshDraw Draw(SdfMesh mesh) => new(Identity: mesh, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity);
}
