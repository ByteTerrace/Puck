using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>How often the data behind a pass interface member changes. Each group is one Vulkan descriptor set and one
/// Direct3D 12 register space, and a group's ordinal is that set and space number on both backends, so the frame
/// group is set 0 and space 0 in every pass.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderInterfaceGroup>))]
public enum ShaderInterfaceGroup {
    /// <summary>Changes every frame: resolution, presentation time, the deterministic tick, the paired camera.</summary>
    Frame = 0,
    /// <summary>Changes every tick at most: the state mirror's regions and the field lattices.</summary>
    World = 1,
    /// <summary>Changes when a pipeline instance is installed or resized: parameter blocks, persistent and history
    /// resources.</summary>
    Instance = 2,
    /// <summary>Changes every pass: transient inputs and outputs.</summary>
    Pass = 3,
}
