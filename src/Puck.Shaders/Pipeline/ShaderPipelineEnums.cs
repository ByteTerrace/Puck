using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>Identifies a compute or fullscreen graphics pipeline pass.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelinePassKind>))]
public enum ShaderPipelinePassKind : byte {
    /// <summary>A compute dispatch.</summary>
    Compute = 1,
    /// <summary>A fullscreen graphics pass.</summary>
    Fullscreen = 2,
}

/// <summary>Identifies a pipeline image, structured buffer, or depth resource.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineResourceKind>))]
public enum ShaderPipelineResourceKind : byte {
    /// <summary>A two-dimensional image.</summary>
    Image = 1,
    /// <summary>A structured byte buffer.</summary>
    Buffer = 2,
    /// <summary>A depth attachment.</summary>
    Depth = 3,
}
