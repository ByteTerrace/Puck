using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Graphics passes and their attachments. A graphics pass writes exactly one color image and, for a geometry pass, at most
// one depth version, all of one declared extent; only opaque, single-sampled drawing executes on every backend, so every
// other blend policy, an alpha test and a multisampled attachment are refused by name. A geometry pass's vertex layout,
// vertices and indices are validated here, before any backend sees them.
public sealed partial class ShaderPipelineCompiler {
    private void ValidateGraphics(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelineResource> resources, List<ShaderPipelineDiagnostic> diagnostics) {
        if (
            (pass.Kind == ShaderPipelinePassKind.Fullscreen) &&
            (pass.Vertex is { } vertex) &&
            !Enum.IsDefined(value: vertex)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_VERTEX_INPUT",
                $"Fullscreen pass '{pass.Name}' has an unsupported vertex input.",
                pass.Name
            );
        } else if (
            (pass.Kind == ShaderPipelinePassKind.Geometry) &&
            (pass.Vertex is not null)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_VERTEX_INPUT",
                $"Geometry pass '{pass.Name}' declares vertex; its vertex stage reads the attributes its geometry declares.",
                pass.Name
            );
        }
        if (pass.Blend is { } blend) {
            if (!Enum.IsDefined(value: blend)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_BLEND",
                    $"Graphics pass '{pass.Name}' has an unknown blend policy.",
                    pass.Name
                );
            } else if (blend != ShaderPipelineBlend.Opaque) {
                Add(
                    diagnostics,
                    "SHADERPIPE_UNSUPPORTED_BLEND",
                    $"Graphics pass '{pass.Name}' declares blend '{blend}'; only opaque drawing executes on every backend.",
                    pass.Name
                );
            }
        }
        if (pass.AlphaTest is not null) {
            Add(
                diagnostics,
                "SHADERPIPE_UNSUPPORTED_ALPHA_TEST",
                $"Graphics pass '{pass.Name}' declares an alpha test; no backend executes one.",
                pass.Name
            );
        }

        var attachments = pass.OutputReferences.Where(predicate: output => resources.ContainsKey(key: output.Name)).Select(selector: output => resources[output.Name]).ToArray();
        var colors = attachments.Count(predicate: static resource => (resource.Kind == ShaderPipelineResourceKind.Image));
        var depths = attachments.Count(predicate: static resource => (resource.Kind == ShaderPipelineResourceKind.Depth));

        if (colors != 1) {
            Add(
                diagnostics,
                "SHADERPIPE_UNSUPPORTED_MRT",
                $"Graphics pass '{pass.Name}' writes {colors} color images; every backend executes exactly one color attachment per pass.",
                pass.Name
            );
        }
        if (depths > 1) {
            Add(
                diagnostics,
                "SHADERPIPE_DEPTH_OUTPUTS",
                $"Geometry pass '{pass.Name}' writes {depths} depth versions; a pass has at most one depth attachment.",
                pass.Name
            );
        }
        if (attachments.Where(predicate: static resource => (resource.Kind != ShaderPipelineResourceKind.Buffer)).Select(selector: static resource => resource.Dimensions).Distinct().Count() > 1) {
            Add(
                diagnostics,
                "SHADERPIPE_ATTACHMENT_EXTENT",
                $"Graphics pass '{pass.Name}' writes attachments of different declared extents; a render pass draws every attachment at one extent.",
                pass.Name
            );
        }
        if (pass.Kind == ShaderPipelinePassKind.Fullscreen) {
            if (
                (pass.Geometry is not null) ||
                (pass.DepthCompare is not null)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_GRAPHICS_FIELDS",
                    $"Fullscreen pass '{pass.Name}' declares geometry or depthCompare, which belong to a geometry pass.",
                    pass.Name
                );
            }

            return;
        }
        if (pass.DepthCompare is { } compare) {
            if (!Enum.IsDefined(value: compare)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_DEPTH_STATE",
                    $"Geometry pass '{pass.Name}' has an unknown depth comparison.",
                    pass.Name
                );
            } else if (depths == 0) {
                Add(
                    diagnostics,
                    "SHADERPIPE_DEPTH_STATE",
                    $"Geometry pass '{pass.Name}' declares depthCompare but writes no depth version to test against.",
                    pass.Name
                );
            }
        }
        if (pass.Geometry is not { } geometry) {
            Add(
                diagnostics,
                "SHADERPIPE_GEOMETRY_SHAPE",
                $"Geometry pass '{pass.Name}' declares no geometry.",
                pass.Name
            );

            return;
        }
        ValidateGeometry(
            diagnostics: diagnostics,
            geometry: geometry,
            pass: pass
        );
    }
    private void ValidateGeometry(ShaderPipelinePass pass, ShaderPipelineGeometry geometry, List<ShaderPipelineDiagnostic> diagnostics) {
        if (
            string.IsNullOrWhiteSpace(value: geometry.VertexEntryPoint) ||
            (geometry.Attributes is null) ||
            geometry.Attributes.Any(predicate: static attribute => ((attribute is null) || string.IsNullOrWhiteSpace(value: attribute.Format))) ||
            (geometry.Vertices is null) ||
            (geometry.Indices is null)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_GEOMETRY_SHAPE",
                $"Geometry pass '{pass.Name}' geometry requires a vertex entry point, attributes with formats, vertices, and indices.",
                pass.Name
            );

            return;
        }
        if (
            (geometry.StrideBytes == 0) ||
            ((geometry.StrideBytes & 3) != 0)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_VERTEX_LAYOUT",
                $"Geometry pass '{pass.Name}' declares a vertex stride of {geometry.StrideBytes} bytes; a stride is a positive multiple of four.",
                pass.Name
            );
        }
        if (
            (geometry.Attributes.Count == 0) ||
            (geometry.Attributes.Count > m_limits.MaxVertexAttributes)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_VERTEX_LAYOUT",
                $"Geometry pass '{pass.Name}' declares {geometry.Attributes.Count} vertex attributes; it declares from one to {m_limits.MaxVertexAttributes}.",
                pass.Name
            );
        }
        for (var index = 0; (index < geometry.Attributes.Count); index++) {
            var attribute = geometry.Attributes[index];

            if (attribute.Location != ((uint)index)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_VERTEX_LAYOUT",
                    $"Geometry pass '{pass.Name}' attribute {index} declares location {attribute.Location}; attribute n is at location n.",
                    pass.Name
                );
            }
            if (
                char.IsAsciiDigit(c: attribute.Format[0]) ||
                !Enum.TryParse<GpuVertexFormat>(
                    ignoreCase: true,
                    result: out var format,
                    value: attribute.Format
                ) ||
                !Enum.IsDefined(value: format)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_VERTEX_LAYOUT",
                    $"Geometry pass '{pass.Name}' attribute {index} has unsupported format '{attribute.Format}'.",
                    pass.Name
                );

                continue;
            }
            if (
                ((attribute.OffsetBytes & 3) != 0) ||
                ((((ulong)attribute.OffsetBytes) + GpuVertexFormats.SizeBytes(format: format)) > geometry.StrideBytes)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_VERTEX_LAYOUT",
                    $"Geometry pass '{pass.Name}' attribute {index} at offset {attribute.OffsetBytes} is unaligned or extends past the {geometry.StrideBytes}-byte vertex.",
                    pass.Name
                );
            }
        }
        if (
            (geometry.Vertices.Count == 0) ||
            ((geometry.StrideBytes != 0) && (((((ulong)geometry.Vertices.Count) * 4UL) % geometry.StrideBytes) != 0)) ||
            geometry.Vertices.Any(predicate: static value => !float.IsFinite(f: value))
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_GEOMETRY_VERTICES",
                $"Geometry pass '{pass.Name}' declares {geometry.Vertices.Count} vertex floats; the data is a positive whole number of {geometry.StrideBytes}-byte vertices of finite values.",
                pass.Name
            );
        }
        if (!Enum.IsDefined(value: geometry.IndexFormat)) {
            Add(
                diagnostics,
                "SHADERPIPE_INDEX_FORMAT",
                $"Geometry pass '{pass.Name}' has an unknown index format.",
                pass.Name
            );
        } else if (
            (geometry.IndexFormat == ShaderPipelineIndexFormat.UInt16) &&
            geometry.Indices.Any(predicate: static index => (index > ushort.MaxValue))
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_INDEX_FORMAT",
                $"Geometry pass '{pass.Name}' declares 16-bit indices but an index exceeds {ushort.MaxValue}; declare UInt32.",
                pass.Name
            );
        }
        if (
            (geometry.Indices.Count == 0) ||
            ((geometry.Indices.Count % 3) != 0)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_INDEX_COUNT",
                $"Geometry pass '{pass.Name}' declares {geometry.Indices.Count} indices; a triangle list has a positive multiple of three.",
                pass.Name
            );
        }
        var vertexCount = geometry.VertexCount;

        for (var position = 0; (position < geometry.Indices.Count); position++) {
            if (geometry.Indices[position] >= vertexCount) {
                Add(
                    diagnostics,
                    "SHADERPIPE_INDEX_RANGE",
                    $"Geometry pass '{pass.Name}' index {position} names vertex {geometry.Indices[position]}, but the geometry declares {vertexCount} vertices.",
                    pass.Name
                );

                break;
            }
        }
        if (geometry.SizeBytes > m_limits.MaxGeometryBytes) {
            Add(
                diagnostics,
                "SHADERPIPE_LIMIT_GEOMETRY",
                $"Geometry pass '{pass.Name}' declares {geometry.SizeBytes} bytes of vertices and indices; the limit is {m_limits.MaxGeometryBytes}.",
                pass.Name
            );
        }
    }
    private static void ValidateComputeFields(ShaderPipelinePass pass, List<ShaderPipelineDiagnostic> diagnostics) {
        if (pass.Vertex is not null) {
            Add(
                diagnostics,
                "SHADERPIPE_VERTEX_INPUT",
                $"Compute pass '{pass.Name}' declares vertex; only a fullscreen pass declares its vertex input.",
                pass.Name
            );
        }
        if (
            (pass.Geometry is not null) ||
            (pass.DepthCompare is not null) ||
            (pass.Blend is not null) ||
            (pass.AlphaTest is not null)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_GRAPHICS_FIELDS",
                $"Compute pass '{pass.Name}' declares geometry, depthCompare, blend or alphaTest, which belong to graphics passes.",
                pass.Name
            );
        }
    }
    // A depth version is a D32Float attachment whose contents a geometry pass clears or loads: it has no initialization
    // of its own and is never retained into the next frame.
    private static void ValidateDepth(ShaderPipelineResource resource, List<ShaderPipelineDiagnostic> diagnostics) {
        if (
            !string.IsNullOrWhiteSpace(value: resource.Format) &&
            !string.Equals(
                a: resource.Format,
                b: nameof(GpuPixelFormat.D32Float),
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DEPTH_FORMAT",
                $"Depth resource '{resource.Name}' has format '{resource.Format}'; a depth attachment is D32Float.",
                resource.Name
            );
        }
        if (resource.Initialization != ShaderPipelineInitialization.Undefined) {
            Add(
                diagnostics,
                "SHADERPIPE_DEPTH_INITIALIZATION",
                $"Depth resource '{resource.Name}' declares initialization {resource.Initialization}; a geometry pass clears or loads its depth attachment.",
                resource.Name
            );
        }
        if (resource.History) {
            Add(
                diagnostics,
                "SHADERPIPE_DEPTH_HISTORY",
                $"Depth resource '{resource.Name}' declares history; a depth attachment is not retained into the next frame.",
                resource.Name
            );
        }
    }
}
