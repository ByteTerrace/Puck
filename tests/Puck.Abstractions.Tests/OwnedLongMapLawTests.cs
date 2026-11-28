using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Tests;

public sealed class OwnedLongMapLawTests {
    [Fact]
    public void RemovalKeepsProbeChainsReachableAcrossGrowthAndReuse() {
        using var map = new OwnedLongMap<int>(allocator: null);
        for (var key = 0; key < 257; key++) { map.Add(key, key * 3); }
        for (var removed = 0; removed < 257; removed++) {
            var key = (removed * 73) % 257;
            Assert.True(map.Remove(key));
            Assert.False(map.Remove(key));
            for (var other = removed + 1; other < 257; other++) {
                var remaining = (other * 73) % 257;
                Assert.True(map.TryGetValue(remaining, out var value));
                Assert.Equal(remaining * 3, value);
            }
            Assert.Equal(256 - removed, map.Count);
        }
        for (var key = 0; key < 257; key++) { map.Set(key, -key); }
        map.Clear();
        Assert.Equal(0, map.Count);
        for (var key = 0; key < 257; key++) { Assert.False(map.TryGetValue(key, out _)); }
        map.Add(long.MinValue, 1);
        Assert.True(map.Remove(long.MinValue));
    }
}
