namespace Puck.Maths.Tests;

/// <summary>Claims over <see cref="BitmapIndex"/>: every range it answers, after loads and single-lane sets that
/// cross the window's edges, against a plain scan of an independent copy of the lane values.</summary>
internal static class BitmapIndexClaims {
    public static string? RangeVsScan() {
        var random = new Random(Seed: 20261127);
        (long Lower, int Width, int Lanes)[] shapes = [
            (-6L, 13, 64),
            (0L, 1, 1),
            (0L, 2, 64),
            (-3L, 64, 37),
            (long.MaxValue - 63L, 64, 64),
            (long.MinValue, 5, 9),
            (0L, 3, 361),
            (-2L, 5, 128),
            (7L, 2, 65),
        ];

        foreach (var (lower, width, lanes) in shapes) {
            var index = new BitmapIndex(lower: lower, width: width, lanes: lanes);
            var copy = new long[lanes];

            copy.AsSpan().Fill(value: lower);
            if (Check(index: index, copy: copy, lower: lower, width: width) is { } fresh) { return $"fresh {lower}/{width}/{lanes}: {fresh}"; }

            for (var lane = 0; (lane < lanes); lane++) {
                copy[lane] = Draw(random: random, lower: lower, width: width);
            }
            index.Load(values: copy);
            if (Check(index: index, copy: copy, lower: lower, width: width) is { } loaded) { return $"loaded {lower}/{width}/{lanes}: {loaded}"; }

            for (var step = 0; (step < 200); step++) {
                var lane = random.Next(maxValue: lanes);

                copy[lane] = Draw(random: random, lower: lower, width: width);
                index.Set(lane: lane, value: copy[lane]);
                if ((step % 20) == 0) {
                    if (Check(index: index, copy: copy, lower: lower, width: width) is { } set) { return $"step {step} {lower}/{width}/{lanes}: {set}"; }
                }
            }
            if (Check(index: index, copy: copy, lower: lower, width: width) is { } last) { return $"after sets {lower}/{width}/{lanes}: {last}"; }
        }

        if (!Throws(action: static () => _ = new BitmapIndex(lower: 0L, width: 0, lanes: 1))) { return "a zero width is not refused"; }
        if (!Throws(action: static () => _ = new BitmapIndex(lower: 0L, width: 65, lanes: 1))) { return "a width past 64 is not refused"; }
        if (!Throws(action: static () => _ = new BitmapIndex(lower: 0L, width: 1, lanes: 0))) { return "a zero lane count is not refused"; }
        if (!Throws(action: static () => _ = new BitmapIndex(lower: (long.MaxValue - 62L), width: 64, lanes: 1))) { return "a window past long.MaxValue is not refused"; }
        if (!Throws(action: static () => new BitmapIndex(lower: 0L, width: 1, lanes: 2).Set(lane: 2, value: 0L))) { return "a lane past the last is not refused"; }
        if (!Throws(action: static () => new BitmapIndex(lower: 0L, width: 1, lanes: 2).Load(values: new long[1]))) { return "a short load is not refused"; }
        if (!Throws(action: static () => new BitmapIndex(lower: 0L, width: 1, lanes: 65).Range(lower: 0L, upper: 0L, destination: new ulong[1]))) { return "a short destination is not refused"; }
        if (!Throws(action: static () => _ = new BitmapIndex(lower: 0L, width: BitmapIndex.MaxWidth, lanes: (BitmapIndex.MaxLanes + 1)))) { return "MaxLanes is not the most lanes an index takes"; }
        if (!Throws(action: static () => _ = new BitmapIndex(lower: 0L, width: (BitmapIndex.MaxWidth + 1), lanes: 1))) { return "MaxWidth is not the widest window an index takes"; }

        return null;
    }

    // Mostly window values, some just past either edge, and some far outside, so outliers come and go.
    private static long Draw(Random random, long lower, int width) {
        var pick = random.Next(maxValue: 10);

        return (pick switch {
            < 7 => (lower + random.Next(maxValue: width)),
            7 => unchecked(lower - 1L),
            8 => unchecked(lower + width),
            _ => random.NextInt64(),
        });
    }
    private static bool Bit(ulong[] words, int lane) => (((words[lane / 64] >> (lane % 64)) & 1UL) != 0UL);
    private static string? Check(BitmapIndex index, long[] copy, long lower, int width) {
        var windowUpper = (lower + (width - 1));
        var words = ((copy.Length + 63) / 64);
        var outliers = new ulong[words];

        for (var lane = 0; (lane < copy.Length); lane++) {
            if (index[lane] != copy[lane]) { return $"lane {lane} reads {index[lane]}, not {copy[lane]}"; }
            if ((copy[lane] < lower) || (copy[lane] > windowUpper)) { outliers[lane / 64] |= (1UL << (lane % 64)); }
        }
        if (!index.Outliers.SequenceEqual(other: outliers)) { return "the outliers differ from the scan's"; }
        if ((index.Lower != lower) || (index.Width != width) || (index.Lanes != copy.Length) || (index.Words != words)) { return "the shape reads back differently"; }

        // Every range whose ends lie within two of the window, plus an empty, an everything and a far range.
        var ends = new List<long> { long.MinValue, long.MaxValue };

        for (var offset = -2L; (offset <= (width + 1)); offset++) {
            ends.Add(item: unchecked(lower + offset));
        }

        var actual = new ulong[(words + 1)];

        foreach (var from in ends) {
            foreach (var to in ends) {
                actual.AsSpan().Fill(value: ulong.MaxValue);
                index.Range(destination: actual, lower: from, upper: to);
                if (actual[words] != ulong.MaxValue) { return $"range {from}..{to} wrote past its words"; }
                for (var lane = 0; (lane < (words * 64)); lane++) {
                    var expected = ((lane < copy.Length) && (copy[lane] >= from) && (copy[lane] <= to));

                    if (Bit(words: actual, lane: lane) != expected) { return $"range {from}..{to} reads lane {lane} as {!expected}"; }
                }

                var inside = 0;

                for (var plane = 0; (plane < width); plane++) {
                    var value = (lower + plane);

                    if ((value >= from) && (value <= to)) { inside++; }
                }

                var reads = BitmapIndex.RangeReads(windowLower: lower, width: width, lower: from, upper: to, lanes: copy.Length);

                if (reads != ((Math.Min(val1: inside, val2: (width - inside)) + 1) * words)) {
                    return $"range {from}..{to} prices {reads} reads over {inside} of {width} planes and {words} words";
                }
            }
        }

        return null;
    }
    private static bool Throws(Action action) {
        try {
            action();
        } catch (ArgumentException) {
            return true;
        }

        return false;
    }
}
