using System.Text;
using Puck.Assets;

namespace Puck.State;

/// <summary>The key an embedded text is filed under: an embedding lock's entries, the language server's lookups, and a
/// host's embedding cache all name a text by this hash rather than by the text itself.</summary>
public static class EmbeddingText {
    /// <summary>Computes the content pin of a text's UTF-8 bytes.</summary>
    /// <param name="text">The text to hash.</param>
    /// <returns>The pin of <paramref name="text"/>'s UTF-8 encoding; an embedding lock keys its entries by the pin's
    /// <see cref="ContentPin.Hex"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static ContentPin Hash(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: text));
    }
}
