using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>A GPU shader stage.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderStage>))]
public enum ShaderStage {
    /// <summary>The vertex stage of a graphics pass.</summary>
    Vertex = 0,
    /// <summary>The fragment stage of a graphics pass.</summary>
    Fragment,
    /// <summary>A compute stage.</summary>
    Compute
}
