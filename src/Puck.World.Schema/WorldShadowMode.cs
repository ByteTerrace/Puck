using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a named light competes for the quality row's bounded shadow slots.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldShadowMode>))]
public enum WorldShadowMode {
    /// <summary>Selected before automatic candidates, in authored list order.</summary>
    [JsonStringEnumMemberName("always")]
    Always,
    /// <summary>Selected by tick-resolved luminance after always-shadowed candidates.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,
    /// <summary>Lights without consuming a shadow slot.</summary>
    [JsonStringEnumMemberName("never")]
    Never,
}
