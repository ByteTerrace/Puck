using System.Text.Json;

namespace Puck.World;

/// <summary>Strict settings readers shared by host-selected extension providers.</summary>
public static class WorldExtensionSettings {
    /// <summary>Reads an object containing exactly one property with the expected name.</summary>
    /// <param name="settings">The provider's authored settings.</param>
    /// <param name="name">The sole permitted property name.</param>
    /// <returns>The property's value, whose type the provider validates.</returns>
    /// <exception cref="ArgumentException">The object contains missing, extra, or duplicate properties.</exception>
    public static JsonElement OnlySetting(JsonElement settings, string name) {
        if (settings.ValueKind == JsonValueKind.Object) {
            var properties = settings.EnumerateObject();

            if (properties.MoveNext() && properties.Current.NameEquals(text: name)) {
                var value = properties.Current.Value;

                if (!properties.MoveNext()) { return value; }
            }
        }
        throw new ArgumentException(message: $"Extension settings must contain exactly '{name}'.");
    }
}
