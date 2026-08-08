namespace Puck.Demo.Forge.Bake;

/// <summary>One weighted colour of a tile histogram (colours ascend within a histogram — the deterministic order).</summary>
/// <param name="Colour">The packed RGB555 colour.</param>
/// <param name="Count">How many pixels carry it.</param>
internal readonly record struct HistogramEntry(ushort Colour, int Count);

/// <summary>What the palette fit produced: the palette table (each palette's colours in display order, lightest
/// first), the per-tile palette assignment, and the pre-merge pressure gauge.</summary>
/// <param name="Palettes">The fitted palettes (≤ the budget; each ≤ the usable-colour count).</param>
/// <param name="Assignments">Per input tile, the palette index it quantizes against.</param>
/// <param name="TilePaletteCount">Distinct per-tile palettes after dedupe, BEFORE the merge.</param>
internal sealed record PaletteFitResult(IReadOnlyList<ushort[]> Palettes, int[] Assignments, int TilePaletteCount);

/// <summary>
/// The CGB palette assignment, entirely in rounded RGB555 space: per-tile median-cut palettes → dedupe by sorted
/// colour tuple → a pre-merge guard (≤ 48 clusters) → greedy pairwise merges to the budget → exactly two Lloyd
/// rounds. Every stage is deterministic: fixed iteration counts, lowest-index tie-breaks, and no dictionary
/// enumeration ever decides an order.
/// </summary>
internal static class PaletteFitter {
    // The pre-merge ceiling: above this many distinct tile palettes, the cheapest (smallest-pixel-count) clusters
    // fold into their nearest neighbour first so the O(n²) greedy merge starts from a bounded n.
    private const int PreMergeCeiling = 48;

    private sealed class Cluster {
        public required ushort[] Colours { get; set; }
        public required List<HistogramEntry> Histogram { get; set; }
        public long PixelCount { get; set; }
        public required List<int> Tiles { get; init; }
    }

    /// <summary>Fits palettes to a set of tile histograms.</summary>
    /// <param name="tiles">Per tile, its weighted colour histogram (colours ascending; empty = a fully transparent
    /// tile, which is assigned palette 0 and never influences the fit).</param>
    /// <param name="usableColours">Colours per palette the pixels may use (4 for backgrounds; 3 for sprites, whose
    /// slot 0 is transparent).</param>
    /// <param name="paletteBudget">The most palettes to emit.</param>
    /// <returns>The fit result.</returns>
    public static PaletteFitResult Fit(IReadOnlyList<List<HistogramEntry>> tiles, int usableColours, int paletteBudget) {
        ArgumentNullException.ThrowIfNull(tiles);

        var clusters = BuildClusters(tiles: tiles, usableColours: usableColours);
        var tilePaletteCount = clusters.Count;

        if (clusters.Count == 0) {
            return new PaletteFitResult(Assignments: new int[tiles.Count], Palettes: [], TilePaletteCount: 0);
        }

        while (clusters.Count > PreMergeCeiling) {
            PreMergeSmallest(clusters: clusters, usableColours: usableColours);
        }

        while (clusters.Count > Math.Max(val1: 1, val2: paletteBudget)) {
            MergeCheapestPair(clusters: clusters, tiles: tiles, usableColours: usableColours);
        }

        // EXACTLY two Lloyd rounds: reassign every tile to its argmin-error palette, then re-derive each palette
        // from its assigned pool. Two is the settled count — enough to heal the merge's seams, never an open loop.
        for (var round = 0; (round < 2); round++) {
            LloydRound(clusters: clusters, tiles: tiles, usableColours: usableColours);
        }

        return Emit(clusters: clusters, tiles: tiles, tilePaletteCount: tilePaletteCount);
    }

    /// <summary>Derives up to <paramref name="colourCount"/> colours from a weighted histogram by median cut: the
    /// box with the widest single-channel spread splits at its weighted median until enough boxes exist; each box
    /// contributes its weighted mean. Colours emit lightest-first (packed value breaks luminance ties).</summary>
    /// <param name="histogram">The weighted colours (any order; internally re-sorted deterministically).</param>
    /// <param name="colourCount">The most colours to derive.</param>
    /// <returns>The derived colours (possibly fewer than asked when the histogram is small).</returns>
    public static ushort[] MedianCut(List<HistogramEntry> histogram, int colourCount) {
        ArgumentNullException.ThrowIfNull(histogram);

        if (histogram.Count == 0) {
            return [];
        }

        var boxes = new List<List<HistogramEntry>>(capacity: colourCount) { new(collection: histogram) };

        while (boxes.Count < colourCount) {
            var (boxIndex, channel) = WidestBox(boxes: boxes);

            if (boxIndex < 0) {
                break;
            }

            SplitBox(boxes: boxes, boxIndex: boxIndex, channel: channel);
        }

        var colours = new List<ushort>(capacity: boxes.Count);

        foreach (var box in boxes) {
            colours.Add(item: WeightedMean(box: box));
        }

        colours.Sort(comparison: static (left, right) => {
            var byLuminance = BakeColor.Luminance(colour: right).CompareTo(value: BakeColor.Luminance(colour: left));

            return ((byLuminance != 0) ? byLuminance : left.CompareTo(value: right));
        });

        return [.. colours];
    }

    /// <summary>A tile's total quantization error against a palette: Σ count × min squared distance.</summary>
    /// <param name="histogram">The tile's weighted colours.</param>
    /// <param name="colours">The candidate palette.</param>
    /// <returns>The weighted error (0 for an empty histogram or palette).</returns>
    public static long TileError(List<HistogramEntry> histogram, ushort[] colours) {
        ArgumentNullException.ThrowIfNull(histogram);
        ArgumentNullException.ThrowIfNull(colours);

        if (colours.Length == 0) {
            return 0L;
        }

        var error = 0L;

        foreach (var entry in histogram) {
            var best = int.MaxValue;

            foreach (var colour in colours) {
                best = Math.Min(val1: best, val2: BakeColor.DistanceSquared(a: entry.Colour, b: colour));
            }

            error += ((long)best * entry.Count);
        }

        return error;
    }

    // Stage 1 + 2: per-tile median cuts, then dedupe by the sorted colour tuple (packed into one ulong key) into
    // clusters in first-seen tile order.
    private static List<Cluster> BuildClusters(IReadOnlyList<List<HistogramEntry>> tiles, int usableColours) {
        var clusters = new List<Cluster>();
        var lookup = new Dictionary<ulong, int>();

        for (var tile = 0; (tile < tiles.Count); tile++) {
            if (tiles[tile].Count == 0) {
                continue;
            }

            var colours = MedianCut(colourCount: usableColours, histogram: tiles[tile]);
            var key = PaletteKey(colours: colours);

            if (!lookup.TryGetValue(key: key, value: out var index)) {
                index = clusters.Count;
                clusters.Add(item: new Cluster {
                    Colours = colours,
                    Histogram = new List<HistogramEntry>(collection: tiles[tile]),
                    Tiles = [],
                });
                lookup[key] = index;
            } else {
                clusters[index].Histogram = MergeHistograms(left: clusters[index].Histogram, right: tiles[tile]);
            }

            clusters[index].Tiles.Add(item: tile);
            clusters[index].PixelCount += TotalCount(histogram: tiles[tile]);
        }

        return clusters;
    }

    // Stage 3: fold the smallest-pixel-count cluster into its nearest neighbour by mean-colour distance.
    private static void PreMergeSmallest(List<Cluster> clusters, int usableColours) {
        var smallest = 0;

        for (var index = 1; (index < clusters.Count); index++) {
            if (clusters[index].PixelCount < clusters[smallest].PixelCount) {
                smallest = index;
            }
        }

        var smallestMean = WeightedMean(box: clusters[smallest].Histogram);
        var nearest = -1;
        var nearestDistance = int.MaxValue;

        for (var index = 0; (index < clusters.Count); index++) {
            if (index == smallest) {
                continue;
            }

            var distance = BakeColor.DistanceSquared(a: smallestMean, b: WeightedMean(box: clusters[index].Histogram));

            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = index;
            }
        }

        Merge(clusters: clusters, into: nearest, from: smallest, usableColours: usableColours);
    }

    // Stage 4: one greedy step — evaluate every pair's merge candidate (a fresh median cut over the pooled
    // histograms), pick the lowest total error delta (ties → the lowest (i, j)), and merge it.
    private static void MergeCheapestPair(List<Cluster> clusters, IReadOnlyList<List<HistogramEntry>> tiles, int usableColours) {
        var bestI = 0;
        var bestJ = 1;
        var bestCost = long.MaxValue;

        for (var i = 0; (i < (clusters.Count - 1)); i++) {
            for (var j = (i + 1); (j < clusters.Count); j++) {
                var candidate = MedianCut(colourCount: usableColours, histogram: MergeHistograms(left: clusters[i].Histogram, right: clusters[j].Histogram));
                var cost = (PairCost(candidate: candidate, cluster: clusters[i], tiles: tiles) + PairCost(candidate: candidate, cluster: clusters[j], tiles: tiles));

                if (cost < bestCost) {
                    bestCost = cost;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        Merge(clusters: clusters, into: bestI, from: bestJ, usableColours: usableColours);
    }
    private static long PairCost(ushort[] candidate, Cluster cluster, IReadOnlyList<List<HistogramEntry>> tiles) {
        var cost = 0L;

        foreach (var tile in cluster.Tiles) {
            cost += (TileError(colours: candidate, histogram: tiles[tile]) - TileError(colours: cluster.Colours, histogram: tiles[tile]));
        }

        return cost;
    }
    private static void Merge(List<Cluster> clusters, int into, int from, int usableColours) {
        var target = clusters[into];
        var source = clusters[from];

        target.Histogram = MergeHistograms(left: target.Histogram, right: source.Histogram);
        target.Colours = MedianCut(colourCount: usableColours, histogram: target.Histogram);
        target.Tiles.AddRange(collection: source.Tiles);
        target.PixelCount += source.PixelCount;
        clusters.RemoveAt(index: from);
    }

    // Stage 5 (one round): reassign every tile to its argmin-error palette (ties → lowest index), then re-derive
    // each palette by median cut over its assigned pool. A palette that lost every tile keeps its colours.
    private static void LloydRound(List<Cluster> clusters, IReadOnlyList<List<HistogramEntry>> tiles, int usableColours) {
        var assigned = new List<int>[clusters.Count];

        for (var index = 0; (index < clusters.Count); index++) {
            assigned[index] = [];
        }

        for (var tile = 0; (tile < tiles.Count); tile++) {
            if (tiles[tile].Count == 0) {
                continue;
            }

            var best = 0;
            var bestError = long.MaxValue;

            for (var index = 0; (index < clusters.Count); index++) {
                var error = TileError(colours: clusters[index].Colours, histogram: tiles[tile]);

                if (error < bestError) {
                    bestError = error;
                    best = index;
                }
            }

            assigned[best].Add(item: tile);
        }

        for (var index = 0; (index < clusters.Count); index++) {
            clusters[index].Tiles.Clear();
            clusters[index].Tiles.AddRange(collection: assigned[index]);
            clusters[index].PixelCount = 0L;

            if (assigned[index].Count == 0) {
                continue;
            }

            var pooled = new List<HistogramEntry>();

            foreach (var tile in assigned[index]) {
                pooled = MergeHistograms(left: pooled, right: tiles[tile]);
                clusters[index].PixelCount += TotalCount(histogram: tiles[tile]);
            }

            clusters[index].Histogram = pooled;
            clusters[index].Colours = MedianCut(colourCount: usableColours, histogram: pooled);
        }
    }

    // Stage 6: drop empty clusters (a Lloyd round may starve one), compact indices in cluster order, and write the
    // per-tile assignment (fully transparent tiles land on palette 0).
    private static PaletteFitResult Emit(List<Cluster> clusters, IReadOnlyList<List<HistogramEntry>> tiles, int tilePaletteCount) {
        var palettes = new List<ushort[]>(capacity: clusters.Count);
        var assignments = new int[tiles.Count];

        foreach (var cluster in clusters) {
            if (cluster.Tiles.Count == 0) {
                continue;
            }

            var paletteIndex = palettes.Count;

            palettes.Add(item: cluster.Colours);

            foreach (var tile in cluster.Tiles) {
                assignments[tile] = paletteIndex;
            }
        }

        return new PaletteFitResult(Assignments: assignments, Palettes: palettes, TilePaletteCount: tilePaletteCount);
    }

    // The dedupe key: the palette's colours ascending, 16 bits each (0xFFFF pads an absent slot) — one ulong.
    private static ulong PaletteKey(ushort[] colours) {
        Span<ushort> sorted = stackalloc ushort[4];

        sorted.Fill(value: 0xFFFF);

        for (var index = 0; ((index < colours.Length) && (index < 4)); index++) {
            sorted[index] = colours[index];
        }

        sorted[..Math.Min(val1: colours.Length, val2: 4)].Sort();

        return (ulong)sorted[0] | ((ulong)sorted[1] << 16) | ((ulong)sorted[2] << 32) | ((ulong)sorted[3] << 48);
    }

    // Merges two colour-ascending histograms, summing counts — a deterministic sorted-list merge.
    private static List<HistogramEntry> MergeHistograms(List<HistogramEntry> left, List<HistogramEntry> right) {
        var merged = new List<HistogramEntry>(capacity: (left.Count + right.Count));
        var leftIndex = 0;
        var rightIndex = 0;

        while ((leftIndex < left.Count) || (rightIndex < right.Count)) {
            if ((rightIndex >= right.Count) || ((leftIndex < left.Count) && (left[leftIndex].Colour < right[rightIndex].Colour))) {
                merged.Add(item: left[leftIndex++]);
            } else if ((leftIndex >= left.Count) || (right[rightIndex].Colour < left[leftIndex].Colour)) {
                merged.Add(item: right[rightIndex++]);
            } else {
                merged.Add(item: new HistogramEntry(Colour: left[leftIndex].Colour, Count: (left[leftIndex].Count + right[rightIndex].Count)));
                leftIndex++;
                rightIndex++;
            }
        }

        return merged;
    }
    private static long TotalCount(List<HistogramEntry> histogram) {
        var total = 0L;

        foreach (var entry in histogram) {
            total += entry.Count;
        }

        return total;
    }

    // The box with the widest single-channel spread (ties → lowest box index) and that channel.
    private static (int BoxIndex, int Channel) WidestBox(List<List<HistogramEntry>> boxes) {
        var bestBox = -1;
        var bestChannel = 0;
        var bestRange = 0;

        for (var index = 0; (index < boxes.Count); index++) {
            if (boxes[index].Count < 2) {
                continue;
            }

            var (range, channel) = ChannelSpread(box: boxes[index]);

            if (range > bestRange) {
                bestRange = range;
                bestChannel = channel;
                bestBox = index;
            }
        }

        return (bestBox, bestChannel);
    }
    private static (int Range, int Channel) ChannelSpread(List<HistogramEntry> box) {
        Span<int> min = stackalloc int[3];
        Span<int> max = stackalloc int[3];

        min.Fill(value: 31);
        max.Fill(value: 0);

        foreach (var entry in box) {
            for (var channel = 0; (channel < 3); channel++) {
                var value = BakeColor.Channel(channel: channel, colour: entry.Colour);

                min[channel] = Math.Min(val1: min[channel], val2: value);
                max[channel] = Math.Max(val1: max[channel], val2: value);
            }
        }

        var bestChannel = 0;
        var bestRange = (max[0] - min[0]);

        for (var channel = 1; (channel < 3); channel++) {
            if ((max[channel] - min[channel]) > bestRange) {
                bestRange = (max[channel] - min[channel]);
                bestChannel = channel;
            }
        }

        return (bestRange, bestChannel);
    }
    private static void SplitBox(List<List<HistogramEntry>> boxes, int boxIndex, int channel) {
        var box = boxes[boxIndex];

        box.Sort(comparison: (left, right) => {
            var byChannel = BakeColor.Channel(channel: channel, colour: left.Colour).CompareTo(value: BakeColor.Channel(channel: channel, colour: right.Colour));

            return ((byChannel != 0) ? byChannel : left.Colour.CompareTo(value: right.Colour));
        });

        var total = TotalCount(histogram: box);
        var running = 0L;
        var split = 1;

        for (var index = 0; (index < (box.Count - 1)); index++) {
            running += box[index].Count;

            if ((running * 2) >= total) {
                split = (index + 1);

                break;
            }
        }

        boxes[boxIndex] = box.GetRange(index: 0, count: split);
        boxes.Add(item: box.GetRange(index: split, count: (box.Count - split)));
    }

    // The histogram's weighted mean colour, each channel rounded half-up — deterministic integer math.
    private static ushort WeightedMean(List<HistogramEntry> box) {
        if (box.Count == 0) {
            return 0;
        }

        Span<long> sums = stackalloc long[3];
        var total = 0L;

        foreach (var entry in box) {
            for (var channel = 0; (channel < 3); channel++) {
                sums[channel] += ((long)BakeColor.Channel(channel: channel, colour: entry.Colour) * entry.Count);
            }

            total += entry.Count;
        }

        return BakeColor.Pack(
            b5: (int)((sums[2] + (total / 2)) / total),
            g5: (int)((sums[1] + (total / 2)) / total),
            r5: (int)((sums[0] + (total / 2)) / total)
        );
    }
}
