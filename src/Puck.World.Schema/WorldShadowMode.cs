using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a light-casting body's identity participates in the bounded shadow-slot selection.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldShadowMode>))]
public enum WorldShadowMode {
    /// <summary>Selected before automatic candidates, in authored list order.</summary>
    Always,
    /// <summary>Selected by tick-resolved luminance after always-shadowed candidates.</summary>
    Auto,
    /// <summary>Lights without consuming a shadow slot.</summary>
    Never,
}
