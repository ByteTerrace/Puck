using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>Clock keys for a presentation section. Partial records address existing authored rows by name and
/// lower once to the same typed value curves as an individual bindable.</summary>
/// <param name="Clock">The named presentation clock.</param>
/// <param name="Keys">Ordered partial records; omitted leaves carry from their last stated key around the wrap.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSectionKeys(string Clock, IReadOnlyList<WorldSectionKey> Keys);

/// <summary>One partial presentation record. The preparation pass checks every field against the section's actual
/// model shape and admits only bindable leaves; extension data is retained here for source-preserving saves.</summary>
/// <param name="At">The position in the clock's authored span.</param>
/// <param name="Ease">The easing toward the next key.</param>
public sealed record WorldSectionKey(double At, WorldKeyEase Ease = WorldKeyEase.Linear) {
    /// <summary>The named partial record, checked and lowered by <see cref="WorldPresentationValues"/>. A settable
    /// accessor lets System.Text.Json append extension fields while deserializing.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement> Values { get; set; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
