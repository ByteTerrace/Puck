using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>Identifies what a document's pass is: a compute, fullscreen graphics, or indexed geometry pass. A document
/// cannot name package work, so a <c>kind</c> of anything else is refused by the JSON reader;
/// <see cref="ShaderPipelinePassKind"/> is the planner's kind, which adds packages.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineDocumentPassKind>))]
public enum ShaderPipelineDocumentPassKind : byte {
    /// <summary>A compute dispatch.</summary>
    Compute = 1,
    /// <summary>A fullscreen graphics pass: one triangle covering its color attachment.</summary>
    Fullscreen = 2,
    /// <summary>A graphics pass that draws its declared vertices as an indexed triangle list into its color attachment,
    /// optionally tested against and writing a depth attachment.</summary>
    Geometry = 3,
}
/// <summary>Identifies what the planner orders: a document's compute, fullscreen or geometry pass, or an engine
/// package's pass. A planned pass carries it (<see cref="ShaderPipelinePlannedPass.Kind"/>); no document names
/// it.</summary>
public enum ShaderPipelinePassKind : byte {
    /// <summary>A compute dispatch.</summary>
    Compute = 1,
    /// <summary>A fullscreen graphics pass.</summary>
    Fullscreen = 2,
    /// <summary>An indexed geometry pass.</summary>
    Geometry = 3,
    /// <summary>Engine work a frame graph names under <c>packages</c> (<see cref="RenderGraphPackagePass"/>), never as a
    /// shader pass. The package records its own work and binds its own descriptors, so its planned pass has no
    /// declaration; the planner orders, versions and barriers it by the versions it reads and writes, which it reaches as
    /// a compute pass does.</summary>
    Package = 4,
}
/// <summary>Identifies how a fullscreen pass's vertex stage obtains the triangle's corners.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineVertexInput>))]
public enum ShaderPipelineVertexInput : byte {
    /// <summary>The vertex stage derives each corner from <c>SV_VertexID</c>; no vertex buffer is bound.</summary>
    VertexId = 1,
    /// <summary>The vertex stage reads each corner's clip-space <c>float2</c> from a <c>POSITION</c> attribute, fed by
    /// the shared fullscreen-triangle vertex buffer.</summary>
    Position = 2,
}
/// <summary>Identifies the width of a geometry pass's indices.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineIndexFormat>))]
public enum ShaderPipelineIndexFormat : byte {
    /// <summary>16-bit unsigned indices.</summary>
    UInt16 = 1,
    /// <summary>32-bit unsigned indices.</summary>
    UInt32 = 2,
}
/// <summary>Identifies the comparison by which a geometry pass's depth test passes a fragment, comparing the fragment's
/// depth with the depth attachment's.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineDepthCompare>))]
public enum ShaderPipelineDepthCompare : byte {
    /// <summary>The fragment's depth is less.</summary>
    Less = 1,
    /// <summary>The fragment's depth is less or equal.</summary>
    LessOrEqual = 2,
    /// <summary>The fragment's depth is greater.</summary>
    Greater = 3,
    /// <summary>The fragment's depth is greater or equal.</summary>
    GreaterOrEqual = 4,
    /// <summary>The fragment's depth is equal.</summary>
    Equal = 5,
    /// <summary>Every fragment passes, so the last one drawn at a pixel wins.</summary>
    Always = 6,
}
/// <summary>Identifies how a graphics pass's fragments combine with what its color attachment holds. Only
/// <see cref="Opaque"/> executes on every backend; the planner refuses every other policy by name.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineBlend>))]
public enum ShaderPipelineBlend : byte {
    /// <summary>A fragment replaces the attachment's contents.</summary>
    Opaque = 1,
    /// <summary>A fragment blends over the attachment by its alpha. Refused.</summary>
    AlphaOver = 2,
    /// <summary>A fragment adds to the attachment. Refused.</summary>
    Additive = 3,
}
/// <summary>Identifies how a compute or package pass chooses its workgroup counts. Only <see cref="Extent"/> executes
/// in a shader pass; a package records its own dispatches, so the planner admits every shape there and refuses the
/// others on a shader pass with <c>SHADERPIPE_DISPATCH_PACKAGE</c>.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineDispatchKind>))]
public enum ShaderPipelineDispatchKind : byte {
    /// <summary>Enough workgroups to cover the frame extent, one invocation per pixel.</summary>
    Extent = 1,
    /// <summary>The declared group counts.</summary>
    Groups = 2,
    /// <summary>Group counts a buffer version holds: three 32-bit words at a byte offset, which an earlier pass writes
    /// and the dispatch reads as indirect arguments.</summary>
    Indirect = 3,
}
/// <summary>Identifies a count a term of a counted buffer's size scales with.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineCountBasis>))]
public enum ShaderPipelineCountBasis : byte {
    /// <summary>Per pixel of the frame extent.</summary>
    Extent = 1,
    /// <summary>Per instance of the program the host renders.</summary>
    Instances = 2,
    /// <summary>Per word of the program the host renders.</summary>
    ProgramWords = 3,
    /// <summary>Per viewport the host renders into one frame.</summary>
    Viewports = 4,
    /// <summary>Per tile of one viewport, at the host's tile size.</summary>
    Tiles = 5,
    /// <summary>Per dynamic transform the host provisions.</summary>
    DynamicTransforms = 6,
    /// <summary>Per word of one tile's instance mask, which the host derives from its instances.</summary>
    InstanceMaskWords = 7,
    /// <summary>Per word of the instance grid, which the host derives from its instances.</summary>
    InstanceGridWords = 8,
    /// <summary>Per voxel of the SDF brick pool the host provisions for its world.</summary>
    BrickPoolVoxels = 9,
}
