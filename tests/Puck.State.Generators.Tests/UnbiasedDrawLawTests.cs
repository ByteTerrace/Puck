using System.Buffers.Binary;
using System.Security.Cryptography;
using Puck.Maths;
using Puck.Testing;
using Xunit;

namespace Puck.State.Generators.Tests;

/// <summary>Exact rejection traces, continuation and refusal; no statistical evidence is used.</summary>
public sealed class UnbiasedDrawLawTests {
    private static readonly DrawSeed Seed = new(State: 42, Stream: 54);

    private static uint PrivateWord(ClosedBitset256 key, ulong seed, ulong stream, long cursor) {
        Span<byte> keyBytes = stackalloc byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: keyBytes, value: key.Word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: keyBytes[8..], value: key.Word1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: keyBytes[16..], value: key.Word2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: keyBytes[24..], value: key.Word3);
        Span<byte> message = stackalloc byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: message, value: 0x3156455441564950UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: message[8..], value: seed);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: message[16..], value: stream);
        BinaryPrimitives.WriteInt64LittleEndian(destination: message[24..], value: cursor);
        return BinaryPrimitives.ReadUInt32LittleEndian(source: HMACSHA256.HashData(key: keyBytes, source: message));
    }
    private static StateGenerator WithWords(GeneratorSource source, long minimum, long maximum, params long[] words) {
        var raw = new StateGenerator(Source: GeneratorSource.StreamDraw, Extended: new GeneratorExtended(128, Script: words));

        Assert.True(condition: GeneratorEngine.TryBuildExtendedTable(raw, Seed.State, Seed.Stream, 0, out var table, out var reason), userMessage: reason);
        return new StateGenerator(Source: source, RangeMin: minimum, RangeMax: maximum, Extended: new GeneratorExtended(128, Table: table));
    }

    [Fact]
    public void PrivateBatchAndSingleDrawsUseTheSameHmacWordsAndRawCursor() {
        var key = new ClosedBitset256(Word0: 1, Word1: 2, Word2: 3, Word3: 4);
        var source = new StateGenerator(Source: GeneratorSource.StreamDraw);
        var values = new long[4];

        Assert.True(condition: GeneratorEngine.TryFireBatch(source, CellKind.Int, Seed.State, Seed.Stream, 7, null, values,
            out _, out var advances, out var reason, secret: key), userMessage: reason);
        Assert.Equal(values.Length, advances);
        for (var index = 0; (index < values.Length); index++) {
            Assert.Equal(PrivateWord(key, Seed.State, Seed.Stream, (7 + index)), values[index]);
            Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, Seed.State, Seed.Stream, (7 + index), null,
                out var single, out reason, key), userMessage: reason);
            Assert.Equal(single.Numeric, values[index]);
            Assert.Equal(1, single.Advances);
        }
    }
    [InlineData(1L, 0)]
    [InlineData(2L, 0)]
    [InlineData(3L, 0)]
    [InlineData(3L, 1)]
    [InlineData(3L, 3)]
    [InlineData(208L, 0)]
    [InlineData(208L, 1)]
    [InlineData(208L, 3)]
    [InlineData(2147483649L, 0)]
    [InlineData(2147483649L, 1)]
    [InlineData(2147483649L, 3)]
    [InlineData(4294967296L, 0)]
    [Theory]
    public void ExactWordsPinResultsRawCursorsBatchesAndFreshContinuation(long width, int rejected) {
        long minimum = ((width > int.MaxValue) ? int.MinValue : -100);
        var maximum = ((minimum + width) - 1);
        var words = Enumerable.Repeat(count: rejected, element: 0L).Concat(second: [uint.MaxValue, uint.MaxValue]).ToArray();
        var source = WithWords(GeneratorSource.UniformRange, minimum, maximum, words);

        Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, Seed.State, Seed.Stream, 0, null, out var first, out var reason), userMessage: reason);
        Assert.Equal(maximum, first.Numeric);
        Assert.Equal((rejected + 1L), first.Advances);
        Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, Seed.State, Seed.Stream, first.Advances, null, out var second, out reason), userMessage: reason);
        Assert.Equal(maximum, second.Numeric);
        Assert.Equal(1, second.Advances);
        var values = new long[2];

        Assert.True(condition: GeneratorEngine.TryFireBatch(source, CellKind.Int, Seed.State, Seed.Stream, 0, null, values, out _, out var advances, out reason), userMessage: reason);
        Assert.Equal(actual: values, expected: new[] { maximum, maximum });
        Assert.Equal(actual: advances, expected: (rejected + 2L));
        Assert.True(condition: GeneratorEngine.TryAdvanceBatch(source, CellKind.Int, Seed.State, Seed.Stream, 0, null, 2, out _, out var skipped, out reason), userMessage: reason);
        Assert.Equal(actual: skipped, expected: advances);
        // New declaration means a cold cache, as after checkpoint deserialization.
        var fresh = source with { Extended = new GeneratorExtended(128, Table: source.Extended!.Table!.ToArray()) };

        Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(fresh, CellKind.Int, Seed, first.Advances, null, out var stream, out reason), userMessage: reason);
        Assert.True(condition: stream.TryNext(reason: out reason, value: out var next), userMessage: reason);
        Assert.Equal(second.Numeric, next);
        Assert.Equal(advances, stream.Cursor);
    }
    [Fact]
    public void RejectionBudgetAndCursorOverflowRefuseWithoutLosingAnyWord() {
        var source = WithWords(GeneratorSource.UniformRange, 0, 2, new long[GeneratorCapacity.MaxBoundedAttempts]);

        Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(source, CellKind.Int, Seed, 0, null, out var stream, out var reason), userMessage: reason);
        for (var retry = 0; (retry < 2); retry++) {
            Assert.False(condition: stream.TryNext(reason: out reason, value: out _));
            Assert.Contains(actualString: reason, expectedSubstring: "budget");
            Assert.Equal(0, stream.Cursor);
        }
        var raw = WithWords(GeneratorSource.StreamDraw, 0, 0, 0, 0, uint.MaxValue);

        Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(raw, CellKind.Int, Seed, 0, null, out stream, out reason), userMessage: reason);
        Assert.True(condition: stream.TryNextBounded(exclusiveHigh: 3, reason: out reason, sample: out var last, value: out var index), userMessage: reason);
        Assert.Equal(actual: index, expected: 2U);
        Assert.Equal(actual: last, expected: uint.MaxValue);
        Assert.Equal(3, stream.Cursor);
        Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(raw, CellKind.Int, Seed, (long.MaxValue - 1), null, out stream, out reason), userMessage: reason);
        Assert.True(condition: stream.TryNext(reason: out reason, value: out _), userMessage: reason);
        Assert.False(condition: stream.TryNext(reason: out reason, value: out _));
        Assert.Equal(long.MaxValue, stream.Cursor);
    }
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(-1L, 1L)]
    [InlineData(-100L, 107L)]
    [InlineData(int.MinValue, 0L)]
    [InlineData(int.MinValue, int.MaxValue)]
    [Theory]
    public void ScriptedEndpointsAreAcceptedInExactlyOneAdvance(long minimum, long maximum) {
        var source = new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: minimum, RangeMax: maximum,
            Extended: new GeneratorExtended(8, Script: [minimum, maximum, minimum]));

        for (var cursor = 0L; (cursor < 3); cursor++) {
            Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, Seed.State, Seed.Stream, cursor, null, out var result, out var reason, skip: 5), userMessage: reason);
            Assert.Equal(((cursor == 1) ? maximum : minimum), result.Numeric);
            Assert.Equal(1, result.Advances);
        }
    }
    [Fact]
    public void CheckpointRestoresTheActualRawPositionAfterRejection() {
        var source = WithWords(GeneratorSource.UniformRange, -1, 1, 0, 0, uint.MaxValue, uint.MaxValue);

        var (_, arena) = TopologyArenaFixture.Build();
        Assert.True(condition: ArenaDraws.TryFire(arena, TopologyArenaFixture.Deal, source, Seed, out var first, out var reason), userMessage: reason);
        Assert.Equal(3, first.Advances);
        var snapshot = arena.CaptureSnapshot();

        Assert.True(condition: ArenaDraws.TryFire(arena, TopologyArenaFixture.Deal, source, Seed, out var next, out reason), userMessage: reason);
        var hash = arena.ComputeHash();

        var (_, restored) = TopologyArenaFixture.Build();
        restored.RestoreSnapshot(snapshot: snapshot);
        var fresh = source with { Extended = new GeneratorExtended(128, Table: source.Extended!.Table!.ToArray()) };

        Assert.True(condition: ArenaDraws.TryFire(restored, TopologyArenaFixture.Deal, fresh, Seed, out var continued, out reason), userMessage: reason);
        Assert.Equal(actual: continued, expected: next);
        Assert.Equal(4, restored.DrawCursor(rowOrdinal: TopologyArenaFixture.Deal));
        Assert.Equal(hash, restored.ComputeHash());
    }
    [Fact]
    public void SourceFailureAfterARejectedPrivateWordRestoresTheWholeSelection() {
        var key = new ClosedBitset256(Word0: 1, Word1: 2, Word2: 3, Word3: 4);
        var source = new StateGenerator(Source: GeneratorSource.StreamDraw);

        for (ulong seed = 0; (seed < 256); seed++) {
            var word = PrivateWord(cursor: (long.MaxValue - 1), key: key, seed: seed, stream: 54);

            if (BoundedRandomSampling.TryMap(exclusiveHigh: 2147483649U, value: out _, word: word)) { continue; }
            Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(source, CellKind.Int, new DrawSeed(State: seed, Stream: 54), (long.MaxValue - 1), null, out var stream, out var reason, key), userMessage: reason);
            Assert.False(condition: stream.TryNextBounded(exclusiveHigh: 2147483649U, reason: out reason, sample: out _, value: out _));
            Assert.Contains(actualString: reason, expectedSubstring: "overflow");
            Assert.Equal((long.MaxValue - 1), stream.Cursor);
            Assert.True(condition: stream.TryNext(reason: out reason, value: out var again), userMessage: reason);
            Assert.Equal(actual: again, expected: word);
            Assert.Equal(long.MaxValue, stream.Cursor);
            return;
        }
        Assert.Fail(message: "fixed seed domain supplied no rejected word");
    }
    [Fact]
    public void PrivateBoundedDrawsKeepTheHmacSourceAndResumeAtTheAcceptedWord() {
        var source = new StateGenerator(Source: GeneratorSource.StreamDraw);
        var key = new ClosedBitset256(Word0: 1, Word1: 2, Word2: 3, Word3: 4);
        // Fixed finite seed domain supplies explicit zero, one and multiple rejection traces at a half-width bound.
        foreach (var wantedRejections in new[] { 0, 1, 3 }) {
            var found = false;

            for (ulong seed = 0; ((seed < 256) && !found); seed++) {
                var words = new List<uint>();
                uint expected;

                do { words.Add(item: PrivateWord(key, seed, 54, words.Count)); }
                while (!BoundedRandomSampling.TryMap(words[^1], 2147483649U, out expected));
                if (words.Count != (wantedRejections + 1)) { continue; }
                found = true;
                Assert.True(condition: GeneratorEngine.DrawStream.TryOpen(source, CellKind.Int, new DrawSeed(State: seed, Stream: 54), 0, null, out var stream, out var reason, key), userMessage: reason);
                Assert.True(condition: stream.TryNextBounded(exclusiveHigh: 2147483649U, reason: out reason, sample: out var last, value: out var actual), userMessage: reason);
                Assert.Equal(actual: actual, expected: expected);
                Assert.Equal(words[^1], last);
                Assert.Equal(words.Count, stream.Cursor);
                Assert.True(condition: stream.TryNext(reason: out reason, value: out var next), userMessage: reason);
                Assert.Equal(PrivateWord(key, seed, 54, words.Count), next);
            }
            Assert.True(condition: found);
        }
    }
}
