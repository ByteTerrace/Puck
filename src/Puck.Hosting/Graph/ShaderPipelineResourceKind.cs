using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Hosting;

/// <summary>Identifies what a graph version carries: an image, a raw or structured buffer, or a depth attachment. A
/// package port, an instance's output and an instance's read carry one too, so an edge binds only versions of one
/// kind.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineResourceKind>))]
public enum ShaderPipelineResourceKind : byte {
    /// <summary>A two-dimensional image.</summary>
    Image = 1,
    /// <summary>A raw buffer of 32-bit words, bound as a <c>ByteAddressBuffer</c> or <c>RWByteAddressBuffer</c>, or a
    /// structured or counted buffer a package reaches.</summary>
    Buffer = 2,
    /// <summary>A depth attachment, which only a geometry pass writes and nothing samples.</summary>
    Depth = 3,
}
