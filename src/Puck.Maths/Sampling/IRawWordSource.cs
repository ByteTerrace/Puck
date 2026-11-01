namespace Puck.Maths;

/// <summary>
/// A source of raw 32-bit words that can refuse: the shape <see cref="BoundedRandomSampling"/>'s budgeted rejection
/// loops draw through, so a generator and a stream with a private or capped cursor share one loop. Implemented
/// by <see cref="DrawGeneratorWords{TGenerator}"/> for a bare generator, and by a consumer's own stream when a word
/// can fail (a cursor at its ceiling, an exhausted table). Never boxed: the loops constrain their parameter
/// <c>where TSource : IRawWordSource</c> and thread it by <c>ref</c>.
/// </summary>
public interface IRawWordSource {
    /// <summary>Draws the next raw word, consuming one position of the source.</summary>
    /// <param name="word">The raw word, valid only on success.</param>
    /// <param name="reason">Why the source refused, or empty on success.</param>
    /// <returns>Whether a word was drawn.</returns>
    bool TryNextWord(out uint word, out string reason);
}
