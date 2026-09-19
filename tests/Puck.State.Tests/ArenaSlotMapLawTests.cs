using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the slot map answers exactly what a dictionary of the same writes answers, whether
/// its keys arrive ascending, descending, or scattered far enough apart that it leaves its array, and a cleared map
/// holds nothing and takes new keys anywhere.</summary>
public sealed class ArenaSlotMapLawTests {
    private static void AssertAgrees(ArenaSlotMap map, Dictionary<int, int> expected, IEnumerable<int> probes) {
        foreach (var probe in probes) {
            var held = expected.TryGetValue(
                key: probe,
                value: out var slot
            );

            Assert.Equal(
                held,
                map.TryGetValue(
                    key: probe,
                    value: out var answered
                )
            );
            Assert.Equal(
                held,
                map.ContainsKey(key: probe)
            );
            Assert.Equal(
                (held
                    ? slot
                    : -1
                ),
                answered
            );
        }
    }

    public static TheoryData<int[]> Orders() => new(
        [.. Enumerable.Range(start: 100, count: 300)],
        [.. Enumerable.Range(start: 100, count: 300).Reverse()],
        [7, 9_000, 3, 8_999, 4, 12, 9_001, 0],
        [0, 1_000_000, 2_000_000, 5, 1_000_001],
        [int.MaxValue, 0, (int.MaxValue - 1), 1]
    );

    [MemberData(nameof(Orders))]
    [Theory]
    public void TheMapAnswersWhatADictionaryOfTheSameWritesAnswers(int[] keys) {
        var map = new ArenaSlotMap();
        var expected = new Dictionary<int, int>();
        var slot = 0;

        foreach (var key in keys) {
            map[key] = slot;
            expected[key] = slot;
            slot++;

            AssertAgrees(
                expected: expected,
                map: map,
                probes: keys.Concat(second: [(key - 1), (key + 1), -1, 50_000])
            );
        }

        // Slot zero is a slot like any other, and a key written twice keeps its last slot.
        map[keys[0]] = 0;
        expected[keys[0]] = 0;
        AssertAgrees(
            expected: expected,
            map: map,
            probes: keys
        );
    }
    [Fact]
    public void ScatteredKeysLeaveTheArrayAndCloseKeysDoNot() {
        var close = new ArenaSlotMap();

        for (var key = 5_000; (key < 5_400); key++) {
            close[key] = key;
        }

        Assert.False(condition: close.IsSparse);

        var scattered = new ArenaSlotMap();

        scattered[0] = 0;
        scattered[((ArenaSlotMap.MaxSpanPerCell * 2) + ArenaSlotMap.SpanSlack)] = 1;

        Assert.True(condition: scattered.IsSparse);
    }
    [Fact]
    public void AClearedMapRetakesItsKeysInAnyOrderWithoutAllocating() {
        var map = new ArenaSlotMap();
        var keys = Enumerable.Range(
            count: 200,
            start: 40
        ).ToArray();

        foreach (var key in keys) {
            map[key] = key;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var round = 0; (round < 64); round++) {
            map.Clear();

            for (var index = 0; (index < keys.Length); index++) {
                map[keys[(((index * 37) + round) % keys.Length)]] = index;
            }
        }

        Assert.Equal(
            0L,
            (GC.GetAllocatedBytesForCurrentThread() - before)
        );
    }
    [Fact]
    public void AClearedMapHoldsNothingAndTakesKeysAnywhere() {
        var map = new ArenaSlotMap();

        map[10] = 1;
        map[3_000_000] = 2;
        map.Clear();

        Assert.False(condition: map.IsSparse);
        Assert.False(condition: map.ContainsKey(key: 10));
        Assert.False(condition: map.ContainsKey(key: 3_000_000));

        map[77] = 9;

        Assert.True(condition: map.TryGetValue(
            key: 77,
            value: out var slot
        ));
        Assert.Equal(
            9,
            slot
        );
        Assert.False(condition: map.ContainsKey(key: 10));
    }
}
