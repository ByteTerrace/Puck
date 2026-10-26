using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfMeshRegionArena"/>: a mesh a list stops naming keeps its words, so a list naming it again places
/// nothing, and the arena packs only once the words of meshes no list names outnumber both the named meshes' words and
/// the retained slack, or retaining them would append past the region's ceiling.
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
    [Fact]
    public void TheArenaPacksDroppedMeshesBeforeAppendingPastWhatARegionHolds() {
        var template = new SdfMesh(
            indices: new uint[] { 0, 0, 0 },
            positions: new Vector3[6_000_000]
        );
        var kept = Enumerable.Range(count: 25, start: 0).Select(selector: _ => Draw(mesh: (template with { }))).ToArray();
        var dropped = Enumerable.Range(count: 19, start: 0).Select(selector: _ => Draw(mesh: (template with { }))).ToArray();
        var arena = new SdfMeshRegionArena();

        // The clones share their vertex storage but occupy distinct arena placements, near the region's ceiling.
        arena.Plan(draws: [.. kept, .. dropped]);
        Assert.True(condition: ((((ulong)arena.Words) * sizeof(uint)) <= SdfWorldTables.MostMeshRegionBytes));

        arena.Plan(draws: kept);
        Assert.False(condition: arena.Relocated);

        var added = Draw(mesh: (template with { }));

        arena.Plan(draws: [.. kept, added]);
        Assert.True(condition: arena.Relocated);
        Assert.True(condition: ((((ulong)arena.Words) * sizeof(uint)) <= SdfWorldTables.MostMeshRegionBytes));
        Assert.Equal(expected: (kept.Length + 1), actual: arena.Placements.Count);
    }

    [Fact]
    public void AListPastTheReserveMovesOnlyTheMeshesTheGrownReserveTakes() {
        var small = Triangle(offset: 0f);
        // Long enough to begin inside the grown reserve and end past it.
        var big = new SdfMesh(
            indices: new uint[] { 0, 1, 2 },
            positions: Enumerable.Range(count: 4096, start: 0).Select(selector: static index => new Vector3(x: index, y: 0f, z: 0f)).ToArray()
        );
        var tail = Triangle(offset: 1f);
        var arena = new SdfMeshRegionArena();

        arena.Plan(draws: [Draw(mesh: small), Draw(mesh: big), Draw(mesh: tail)]);
        Assert.True(condition: arena.Relocated);

        var reserveEnd = ((2 * SdfMeshRegionArena.MinimumDrawCapacity) * SdfMeshRecord.DrawWords);
        var kept = arena.Placements[tail];
        var end = arena.Words;

        Assert.True(condition: ((arena.Placements[big].VertexWord < reserveEnd) && (kept.VertexWord >= reserveEnd)));

        var many = Enumerable.Range(count: (SdfMeshRegionArena.MinimumDrawCapacity + 1), start: 0).Select(selector: index => Draw(mesh: ((index % 3) switch {
            0 => small,
            1 => big,
            _ => tail,
        }))).ToArray();

        arena.Plan(draws: many);
        Assert.False(condition: arena.Relocated);
        Assert.Equal(expected: (2 * SdfMeshRegionArena.MinimumDrawCapacity), actual: arena.DrawCapacity);
        Assert.Equal(expected: [small, big], actual: arena.Placed);
        Assert.Equal(expected: 2, actual: arena.Moved.Count);
        Assert.True(condition: (arena.Moved.Contains(item: small) && arena.Moved.Contains(item: big)));
        Assert.Equal(expected: kept, actual: arena.Placements[tail]);
        Assert.Equal(expected: end, actual: arena.Placements[small].VertexWord);

        // A mesh the grown reserve takes that the list does not name is forgotten, never moved.
        arena.Plan(draws: [.. Enumerable.Range(count: ((4 * SdfMeshRegionArena.MinimumDrawCapacity) + 1), start: 0).Select(selector: _ => Draw(mesh: tail))]);
        Assert.False(condition: arena.Relocated);
        Assert.Equal(expected: [tail], actual: arena.Placed);
        Assert.False(condition: arena.Placements.ContainsKey(key: small));
    }

    private static SdfMesh Triangle(float offset) => new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { new(x: offset, y: 0f, z: 0f), new(x: (offset + 1f), y: 0f, z: 0f), new(x: offset, y: 1f, z: 0f) }
    );
    private static SdfMeshDraw Draw(SdfMesh mesh) => new(Identity: mesh, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity);
}
