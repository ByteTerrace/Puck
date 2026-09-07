using Xunit;

namespace Puck.State.Tests;

/// <summary>
/// Laws for the extended-generator draw facet: a <see cref="GeneratorExtended"/> table (authored directly, or
/// compiled from a script) replaces <c>Pcg32Extended</c>'s own self-seeding, so the site draws through the SAME
/// seed-ladder/cursor contract every other draw site does — nothing new persists, a save/reload resumes exactly, and
/// an authored seek (<see cref="Draw.Skip"/>) shifts the sequence by a constant offset.
/// </summary>
public sealed class GeneratorExtendedLawTests {
    private const string Instance = "instance-extended";
    private const ulong WorldSeed = 0x99AA_BBCC_DDEE_FF00UL;
    private const string Site = "state.roll";

    private static ulong SeedState() => GeneratorEngine.ComputeSeedState(instanceIdentity: Instance, site: Site, documentSeed: WorldSeed);
    private static ulong Stream() => GeneratorEngine.ComputeStreamId(site: Site);
    private static long Fire(StateGenerator generator, long cursor, long skip = 0L) {
        Assert.True(condition: GeneratorEngine.TryFire(
            cursor: cursor,
            generator: generator,
            masks: null,
            reason: out var reason,
            result: out var fired,
            seedState: SeedState(),
            skip: skip,
            stream: Stream(),
            targetKind: CellKind.Int
        ), userMessage: reason);

        return fired.Numeric!.Value;
    }

    [Fact]
    public void ScriptedStreamDraw_MatchesScriptThenContinuesLikeASecondBoot() {
        long[] script = [10L, 20L, 30L];
        StateGenerator Fresh() => new(Source: GeneratorSource.StreamDraw, Extended: new GeneratorExtended(K: 8, Script: script));

        Assert.True(condition: GeneratorEngine.TryCheckExtendedShape(generator: Fresh(), reason: out var shapeReason), userMessage: shapeReason);

        var firstBoot = Fresh();
        var drawn = new long[16];

        for (var cursor = 0L; (cursor < drawn.Length); cursor++) {
            drawn[cursor] = Fire(generator: firstBoot, cursor: cursor);
        }

        for (var index = 0; (index < script.Length); index++) {
            Assert.Equal(expected: script[index], actual: drawn[index]);
        }

        // A second boot is a BRAND-NEW StateGenerator instance carrying the identical authored shape — the table is
        // a pure function of the seed ladder and the authored data, so the whole sequence, script included, repeats.
        var secondBoot = Fresh();

        for (var cursor = 0L; (cursor < drawn.Length); cursor++) {
            Assert.Equal(expected: drawn[cursor], actual: Fire(generator: secondBoot, cursor: cursor));
        }
    }
    [Fact]
    public void ScriptedUniformRange_MatchesScriptUnderLemiresMapping_AndOutOfRangeRefusesByName() {
        long[] script = [12L, 17L, 10L, 19L];
        var generator = new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 10, RangeMax: 19, Extended: new GeneratorExtended(K: 8, Script: script));

        for (var cursor = 0L; (cursor < script.Length); cursor++) {
            Assert.Equal(expected: script[cursor], actual: Fire(generator: generator, cursor: cursor));
        }

        // Past the script, the stream continues under the ordinary uniformRange mapping (no crash, no repeat of the
        // script) — the compiled table is a WHOLE table, self-seeded past the script's own length.
        _ = Fire(generator: generator, cursor: script.Length);

        var outOfRange = new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 10, RangeMax: 19, Extended: new GeneratorExtended(K: 8, Script: [20L]));

        Assert.False(condition: GeneratorEngine.TryCheckExtendedShape(generator: outOfRange, reason: out var reason));
        Assert.Contains(expectedSubstring: "outside", actualString: reason);
    }
    [Fact]
    public void ReloadResumes_DrawingTheRestAfterAFreshInstanceEqualsOneUninterruptedRun() {
        var extended = new GeneratorExtended(K: 8, Script: [111L, 222L, 333L]);
        StateGenerator Fresh() => new(Source: GeneratorSource.StreamDraw, Extended: extended);

        const int total = 20;
        const int splitAt = 7;
        var uninterrupted = new long[total];
        var continuous = Fresh();

        for (var cursor = 0L; (cursor < total); cursor++) {
            uninterrupted[cursor] = Fire(generator: continuous, cursor: cursor);
        }

        var firstHalf = Fresh();
        var beforeReload = new long[splitAt];

        for (var cursor = 0L; (cursor < splitAt); cursor++) {
            beforeReload[cursor] = Fire(generator: firstHalf, cursor: cursor);
        }

        // "Reload" is a brand-new StateGenerator instance — exactly what deserializing the document produces — asked
        // to resume from the persisted cursor alone.
        var reloaded = Fresh();
        var afterReload = new long[total - splitAt];

        for (var cursor = (long)splitAt; (cursor < total); cursor++) {
            afterReload[cursor - splitAt] = Fire(generator: reloaded, cursor: cursor);
        }

        Assert.Equal(expected: uninterrupted, actual: [.. beforeReload, .. afterReload]);
    }
    [Fact]
    public void Skip_MovesTheSequence_ReproducingTheUnskippedDrawsFromSOnward() {
        const long s = 5L;

        foreach (var generator in (StateGenerator[]) [
            new StateGenerator(Source: GeneratorSource.StreamDraw),
            new StateGenerator(Source: GeneratorSource.StreamDraw, Extended: new GeneratorExtended(K: 8, Table: [.. Enumerable.Range(start: 0, count: 8).Select(selector: static i => unchecked((uint)(0x1000_0000U * (i + 1))))])),
        ]) {
            for (var cursor = 0L; (cursor < 10L); cursor++) {
                var skipped = Fire(generator: generator, cursor: cursor, skip: s);
                var shifted = Fire(generator: generator, cursor: (cursor + s));

                Assert.Equal(expected: shifted, actual: skipped);
            }
        }
    }
    [Fact]
    public void PerTickExtendedSite_AllocatesNothingAfterTheFirstRebuild() {
        var table = new uint[32];
        var generator = new StateGenerator(Source: GeneratorSource.StreamDraw, Extended: new GeneratorExtended(K: 32, Table: table));
        var seed = SeedState();
        var stream = Stream();
        var cursor = 0L;

        // The first draw at a fresh cursor is a cache miss: it builds and caches the extended generator, which
        // allocates its table once. Warming this (and the generic-over-Pcg32Extended dispatch path's JIT) BEFORE
        // measuring is what isolates the steady-tick cost the law states.
        Assert.True(condition: GeneratorEngine.TryFire(generator: generator, targetKind: CellKind.Int, seedState: seed, stream: stream, cursor: cursor, masks: null, result: out var fired, reason: out var reason), userMessage: reason);
        cursor = checked(cursor + fired.Samples);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 0; (tick < 1000); tick++) {
            Assert.True(condition: GeneratorEngine.TryFire(generator: generator, targetKind: CellKind.Int, seedState: seed, stream: stream, cursor: cursor, masks: null, result: out fired, reason: out reason), userMessage: reason);
            cursor = checked(cursor + fired.Samples);
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(expected: 0L, actual: allocated);
    }
}
