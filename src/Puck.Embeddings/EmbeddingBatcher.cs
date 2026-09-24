namespace Puck.Embeddings;

/// <summary>Splits text collections into bounded batches for provider requests.</summary>
public static class EmbeddingBatcher {
    /// <summary>The default batch size for embedding provider calls.</summary>
    public const int DefaultBatchSize = 64;
    /// <summary>The minimum allowed batch size.</summary>
    public const int MinBatchSize = 1;
    /// <summary>The maximum allowed batch size.</summary>
    public const int MaxBatchSize = 2048;

    /// <summary>Splits the source list into sequential chunks of at most <paramref name="batchSize"/> items.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items to chunk.</param>
    /// <param name="batchSize">The maximum batch size, in [1, 2048].</param>
    /// <returns>An enumeration of item batches.</returns>
    public static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, int batchSize = DefaultBatchSize) {
        ArgumentNullException.ThrowIfNull(argument: items);

        if (batchSize is < MinBatchSize or > MaxBatchSize) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(batchSize),
                message: $"Batch size must be between {MinBatchSize} and {MaxBatchSize}, received {batchSize}."
            );
        }

        for (var i = 0; (i < items.Count); i += batchSize) {
            var count = Math.Min(val1: batchSize, val2: (items.Count - i));
            var chunk = new T[count];

            for (var c = 0; (c < count); c++) {
                chunk[c] = items[(i + c)];
            }

            yield return chunk;
        }
    }
}
