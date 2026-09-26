using System.Text.Json;

namespace Puck.Abstractions.Sources;

/// <summary>A source's settings object: the members its producer binds, as the document spelled them. Two sources of one
/// producer are the same image exactly when their settings are equal member for member, so a document's producer source
/// and the render-graph instance that shows it compare settings through this one rule.</summary>
public static class ImageSourceSettings {
    /// <summary>Returns whether two settings objects hold the same members with deeply equal values. An absent object
    /// equals an empty one, and member order does not matter.</summary>
    /// <param name="left">The first settings object, or <see langword="null"/> for none.</param>
    /// <param name="right">The second settings object, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when the two are equal member for member.</returns>
    public static bool Equal(IReadOnlyDictionary<string, JsonElement>? left, IReadOnlyDictionary<string, JsonElement>? right) {
        var count = (left?.Count ?? 0);

        if (count != (right?.Count ?? 0)) {
            return false;
        }
        if (count == 0) {
            return true;
        }

        foreach (var (key, value) in left!) {
            if (
                !right!.TryGetValue(
                    key: key,
                    value: out var theirs
                ) ||
                !JsonElement.DeepEquals(
                    element1: value,
                    element2: theirs
                )
            ) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Returns a hash consistent with <see cref="Equal"/>: equal settings hash alike.</summary>
    /// <param name="settings">The settings object, or <see langword="null"/> for none.</param>
    /// <returns>The hash, which depends on the member count alone.</returns>
    public static int HashOf(IReadOnlyDictionary<string, JsonElement>? settings) => (settings?.Count ?? 0);
}
