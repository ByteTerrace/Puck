using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>The document schema understood by the shader-pipeline planner.</summary>
public static class ShaderPipelineSchemas {
    /// <summary>The current shader-pipeline document schema.</summary>
    public const string Pipeline = "puck.shader.pipeline.v1";
}
/// <summary>Whether an image's dimensions are tied to the output extent or fixed.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineDimensionMode>))]
public enum ShaderPipelineDimensionMode {
    /// <summary>Dimensions are a scale of the frame extent.</summary>
    Relative,
    /// <summary>Dimensions are absolute pixels.</summary>
    Absolute,
}
/// <summary>How a resource is made valid before its first read.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineInitialization>))]
public enum ShaderPipelineInitialization {
    /// <summary>No initial contents. A first-frame read is refused.</summary>
    Undefined,
    /// <summary>Clear the resource to zero before the first pass.</summary>
    Zero,
    /// <summary>The resource is supplied by the host.</summary>
    External,
}
/// <summary>Dimensions for an image resource.</summary>
public sealed record ShaderPipelineDimensions(
    ShaderPipelineDimensionMode Mode,
    double Width,
    double Height
) {
    private static uint ResolveExtent(double value, string name) {
        if (
            !double.IsFinite(d: value) ||
            (value <= 0) ||
            (value > uint.MaxValue)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: value,
                message: "Resolved shader pipeline extent must be finite, positive, and fit in UInt32.",
                paramName: name
            );
        }

        return Math.Max(
            val1: 1u,
            val2: checked((uint)Math.Round(
                mode: MidpointRounding.ToEven,
                value: value
            ))
        );
    }

    /// <summary>Creates fixed pixel dimensions.</summary>
    public static ShaderPipelineDimensions Absolute(double width, double height) =>
        new(
            Height: height,
            Mode: ShaderPipelineDimensionMode.Absolute,
            Width: width
        );
    /// <summary>Creates frame-relative dimensions.</summary>
    public static ShaderPipelineDimensions Relative(double width = 1, double height = 1) =>
        new(
            Height: height,
            Mode: ShaderPipelineDimensionMode.Relative,
            Width: width
        );
    /// <summary>Resolves this declaration against a frame extent, clamping each positive extent to one pixel.</summary>
    public (uint Width, uint Height) Resolve(uint frameWidth, uint frameHeight) {
        ArgumentOutOfRangeException.ThrowIfZero(value: frameWidth);
        ArgumentOutOfRangeException.ThrowIfZero(value: frameHeight);
        var width = ((Mode == ShaderPipelineDimensionMode.Relative)
            ? (frameWidth * Width)
            : Width
        );
        var height = ((Mode == ShaderPipelineDimensionMode.Relative)
            ? (frameHeight * Height)
            : Height
        );

        return (ResolveExtent(
            value: width,
            nameof(Width)
        ), ResolveExtent(
            value: height,
            nameof(Height)
        ));
    }
}
/// <summary>A pass's reference to one resource version: which version it reads or writes, which frame's instance, and
/// its descriptor binding.</summary>
/// <param name="Name">The version name.</param>
/// <param name="PreviousFrame">Reads the contents the previous frame left rather than this frame's. Only a pass input
/// sets it, and only for a version declared <see cref="ShaderPipelineResource.History"/>.</param>
/// <param name="Binding">The descriptor binding, or <see langword="null"/> for the planner to assign one.</param>
public sealed record ResourceReference(
    string Name,
    bool PreviousFrame = false,
    uint? Binding = null
) {
    /// <summary>Converts the convenient document spelling <c>"name"</c> to a current-frame reference.</summary>
    public static implicit operator ResourceReference(string name) => new(Name: name);
}
/// <summary>One resource version declared by a <see cref="ShaderPipelineDefinition"/>. Each version has exactly one
/// writer. A version that names <see cref="From"/> forwards that predecessor: its writer continues the predecessor's
/// storage and contents, so the predecessor is consumed and every pass that samples it runs before the overwrite.</summary>
/// <param name="Name">The unique version name.</param>
/// <param name="Kind">Image, buffer, or depth resource.</param>
/// <param name="Format">The backend-neutral format spelling. Required for images and depth resources.</param>
/// <param name="Dimensions">Image dimensions; omitted for buffers.</param>
/// <param name="History">Retains this version's contents into the next frame, where a pass input reads them with
/// <see cref="ResourceReference.PreviousFrame"/>. Only the last version of a forwarding chain can be history, because a
/// forward would overwrite what is retained.</param>
/// <param name="Initialization">How the first frame obtains valid contents. Only a version that forwards nothing
/// declares it; a forwarded version's contents come from its predecessor.</param>
/// <param name="SizeBytes">A fixed buffer's capacity in bytes, a multiple of four and of <paramref name="StrideBytes"/>, or
/// <see langword="null"/> for an image or a counted buffer. A buffer without a stride is a raw buffer of 32-bit words,
/// read and written by byte address.</param>
/// <param name="From">The predecessor version this one forwards, or <see langword="null"/> for a version whose writer
/// starts from discarded contents. A predecessor has at most one successor, and its kind, format, extent and sample
/// count equal this version's.</param>
/// <param name="Samples">The sample count of an image. Only single-sampled images are executable, so any other count is
/// refused by name.</param>
/// <param name="StrideBytes">A structured buffer's element stride in bytes, a positive multiple of four, or
/// <see langword="null"/> for a raw buffer. Only a package pass reaches a structured buffer.</param>
/// <param name="Count">A counted buffer's size in elements, a sum of terms that each scale with a product of counts the
/// host resolves, in place of <paramref name="SizeBytes"/>. Only a package pass reaches a counted buffer.</param>
public sealed record ShaderPipelineResource(
    string Name,
    ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image,
    string? Format = null,
    ShaderPipelineDimensions? Dimensions = null,
    bool History = false,
    ShaderPipelineInitialization Initialization = ShaderPipelineInitialization.Undefined,
    ulong? SizeBytes = null,
    string? From = null,
    uint Samples = 1,
    uint? StrideBytes = null,
    IReadOnlyList<ShaderPipelineCountTerm>? Count = null
) {
    /// <summary>Gets whether the host supplies the resource rather than a pass producing it.</summary>
    [JsonIgnore]
    public bool IsExternal => (Initialization == ShaderPipelineInitialization.External);
    /// <summary>Gets the bytes of one buffer element: a structured buffer's stride, or one 32-bit word of a raw
    /// buffer.</summary>
    [JsonIgnore]
    public uint ElementBytes => (StrideBytes ?? 4U);
    /// <summary>Gets whether only a package pass can reach the buffer: it is structured or counted, and the pipeline
    /// node binds raw buffers of fixed size.</summary>
    [JsonIgnore]
    public bool IsPackageStorage => ((StrideBytes is not null) || (Count is not null));

    /// <summary>Resolves a buffer's capacity: its fixed <see cref="SizeBytes"/>, or its <see cref="Count"/> of elements
    /// against the host's counts. A term whose bases resolve to zero units adds nothing; a counted buffer whose terms
    /// all resolve to zero is refused by name, because a zero-byte buffer cannot be bound.</summary>
    /// <param name="counts">The counts a counted buffer scales with.</param>
    /// <returns>The capacity in bytes.</returns>
    /// <exception cref="InvalidOperationException">The resource is not a buffer, or declares neither size.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counts"/> resolves every term of the buffer's
    /// count to zero units, which would size it at zero bytes.</exception>
    /// <exception cref="OverflowException">The capacity does not fit in 64 bits.</exception>
    public ulong ResolveSizeBytes(ShaderPipelineStorageCounts counts) {
        if (Kind != ShaderPipelineResourceKind.Buffer) {
            throw new InvalidOperationException(message: $"Resource '{Name}' is not a buffer.");
        }
        if (SizeBytes is { } fixedBytes) {
            return fixedBytes;
        }
        if (Count is not { } count) {
            throw new InvalidOperationException(message: $"Buffer '{Name}' declares neither sizeBytes nor a count.");
        }

        var elements = 0UL;

        foreach (var term in count) {
            var product = term.Elements;

            foreach (var basis in term.Per) {
                product = checked((product * counts.UnitsOf(basis: basis)));
            }

            elements = checked((elements + product));
        }

        if (elements == 0) {
            var bases = string.Join(
                separator: " + ",
                values: count.Select(selector: static term => string.Join(
                    separator: " * ",
                    values: term.Per
                ))
            );

            throw new ArgumentOutOfRangeException(
                actualValue: counts,
                message: $"Buffer '{Name}' counts by {bases}, and the counts resolve every term to zero units, so it would hold zero bytes.",
                paramName: nameof(counts)
            );
        }

        return checked((((ulong)ElementBytes) * elements));
    }
}
/// <summary>One term of a counted buffer's size: <see cref="Elements"/> elements per unit of the product of the bases
/// in <see cref="Per"/>, each element <see cref="ShaderPipelineResource.ElementBytes"/> long. A buffer's count is the
/// sum of its terms.</summary>
/// <param name="Per">The bases whose units multiply, at least one, each at most once.</param>
/// <param name="Elements">The elements per unit of the product, at least one.</param>
public sealed record ShaderPipelineCountTerm(
    IReadOnlyList<ShaderPipelineCountBasis> Per,
    ulong Elements = 1
) {
    /// <summary>Returns whether two counts are the same terms in the same order, or both absent.</summary>
    /// <param name="left">The first count.</param>
    /// <param name="right">The second count.</param>
    /// <returns>Whether the counts are equal.</returns>
    public static bool SameCount(IReadOnlyList<ShaderPipelineCountTerm>? left, IReadOnlyList<ShaderPipelineCountTerm>? right) => (
        ReferenceEquals(
            objA: left,
            objB: right
        ) ||
        ((left is not null) && (right is not null) && left.SequenceEqual(second: right))
    );
    /// <summary>Returns whether another term scales with the same bases, in the same order, by the same elements.</summary>
    /// <param name="other">The term to compare.</param>
    /// <returns>Whether the terms are equal.</returns>
    public bool Equals(ShaderPipelineCountTerm? other) => (
        (other is not null) &&
        (Elements == other.Elements) &&
        Per.SequenceEqual(second: other.Per)
    );
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Elements);

        foreach (var basis in Per) {
            hash.Add(value: basis);
        }

        return hash.ToHashCode();
    }
}
/// <summary>The counts a host resolves counted buffers against. A basis the host leaves at zero zeroes every term that
/// scales with it, and a counted buffer whose terms all resolve to zero is refused.</summary>
/// <param name="Width">The frame extent's width in pixels.</param>
/// <param name="Height">The frame extent's height in pixels.</param>
public readonly record struct ShaderPipelineStorageCounts(uint Width, uint Height) {
    /// <summary>Gets the instances of the program the host renders.</summary>
    public ulong Instances { get; init; }
    /// <summary>Gets the words of the program the host renders.</summary>
    public ulong ProgramWords { get; init; }
    /// <summary>Gets the viewports the host renders into one frame.</summary>
    public ulong Viewports { get; init; }
    /// <summary>Gets the tiles of one viewport, at the host's tile size.</summary>
    public ulong Tiles { get; init; }
    /// <summary>Gets the dynamic transforms the host provisions.</summary>
    public ulong DynamicTransforms { get; init; }
    /// <summary>Gets the words of one tile's instance mask, which the host derives from its instances.</summary>
    public ulong InstanceMaskWords { get; init; }
    /// <summary>Gets the words of the instance grid, which the host derives from its instances.</summary>
    public ulong InstanceGridWords { get; init; }

    /// <summary>Returns the units a basis counts.</summary>
    /// <param name="basis">The basis.</param>
    /// <returns>The pixels of the extent, or the count the basis names.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="basis"/> is not a declared basis.</exception>
    public ulong UnitsOf(ShaderPipelineCountBasis basis) => basis switch {
        ShaderPipelineCountBasis.Extent => (((ulong)Width) * Height),
        ShaderPipelineCountBasis.Instances => Instances,
        ShaderPipelineCountBasis.ProgramWords => ProgramWords,
        ShaderPipelineCountBasis.Viewports => Viewports,
        ShaderPipelineCountBasis.Tiles => Tiles,
        ShaderPipelineCountBasis.DynamicTransforms => DynamicTransforms,
        ShaderPipelineCountBasis.InstanceMaskWords => InstanceMaskWords,
        ShaderPipelineCountBasis.InstanceGridWords => InstanceGridWords,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: basis,
            message: "Unknown count basis.",
            paramName: nameof(basis)
        ),
    };
}
/// <summary>How a compute or package pass chooses its workgroup counts.</summary>
/// <param name="Kind">The dispatch shape.</param>
/// <param name="GroupCountX">A <see cref="ShaderPipelineDispatchKind.Groups"/> dispatch's group count in x, otherwise
/// one.</param>
/// <param name="GroupCountY">A <see cref="ShaderPipelineDispatchKind.Groups"/> dispatch's group count in y, otherwise
/// one.</param>
/// <param name="GroupCountZ">A <see cref="ShaderPipelineDispatchKind.Groups"/> dispatch's group count in z, otherwise
/// one.</param>
/// <param name="Arguments">An <see cref="ShaderPipelineDispatchKind.Indirect"/> dispatch's buffer version, which the
/// pass reaches in the indirect-argument state and names in neither its inputs nor its outputs; otherwise
/// <see langword="null"/>.</param>
/// <param name="ArgumentsOffsetBytes">The byte offset of the three group-count words in <paramref name="Arguments"/>, a
/// multiple of four.</param>
public sealed record ShaderPipelineDispatch(
    ShaderPipelineDispatchKind Kind,
    uint GroupCountX = 1,
    uint GroupCountY = 1,
    uint GroupCountZ = 1,
    string? Arguments = null,
    ulong ArgumentsOffsetBytes = 0
) {
    /// <summary>The bytes an indirect dispatch reads: three 32-bit group counts.</summary>
    public const ulong ArgumentBytes = 12;

    /// <summary>Creates a dispatch of fixed group counts.</summary>
    /// <param name="x">The group count in x.</param>
    /// <param name="y">The group count in y.</param>
    /// <param name="z">The group count in z.</param>
    /// <returns>The dispatch.</returns>
    public static ShaderPipelineDispatch Groups(uint x, uint y = 1, uint z = 1) => new(
        GroupCountX: x,
        GroupCountY: y,
        GroupCountZ: z,
        Kind: ShaderPipelineDispatchKind.Groups
    );
    /// <summary>Creates a dispatch whose group counts a buffer version holds.</summary>
    /// <param name="arguments">The buffer version.</param>
    /// <param name="offsetBytes">The byte offset of the group counts.</param>
    /// <returns>The dispatch.</returns>
    public static ShaderPipelineDispatch Indirect(string arguments, ulong offsetBytes = 0) => new(
        Arguments: arguments,
        ArgumentsOffsetBytes: offsetBytes,
        Kind: ShaderPipelineDispatchKind.Indirect
    );
}
/// <summary>One attribute of a geometry pass's vertices. The vertex stage reads attribute <c>n</c> as the
/// <c>POSITION{n}</c> semantic, the <c>n</c>th input it declares.</summary>
/// <param name="Location">The attribute's input location, equal to its position in the attribute list.</param>
/// <param name="Format">The attribute's format, spelled as a <see cref="GpuVertexFormat"/> name.</param>
/// <param name="OffsetBytes">The attribute's byte offset within one vertex, a multiple of four.</param>
public sealed record ShaderPipelineVertexAttribute(
    uint Location,
    string Format,
    uint OffsetBytes = 0
);
/// <summary>The indexed triangle list a geometry pass draws, with the layout its vertex stage reads. The vertices are
/// 32-bit floats, each vertex <see cref="StrideBytes"/> long; every three indices name one triangle, drawn in index
/// order.</summary>
/// <param name="VertexEntryPoint">The vertex stage's entry point in the pass's source, which also holds the fragment
/// stage's <see cref="ShaderPipelinePass.EntryPoint"/>. The vertex stage receives no parameters: the pass's parameter
/// block reaches only the fragment stage.</param>
/// <param name="StrideBytes">The bytes of one vertex, a positive multiple of four.</param>
/// <param name="Attributes">The vertex attributes, one per location from zero.</param>
/// <param name="Vertices">The vertex data as 32-bit floats, a whole number of vertices.</param>
/// <param name="Indices">The triangle list's indices, three per triangle, each naming a declared vertex.</param>
/// <param name="IndexFormat">The width of each index; a 16-bit index is at most 65535.</param>
public sealed record ShaderPipelineGeometry(
    string VertexEntryPoint,
    uint StrideBytes,
    IReadOnlyList<ShaderPipelineVertexAttribute> Attributes,
    IReadOnlyList<float> Vertices,
    IReadOnlyList<uint> Indices,
    ShaderPipelineIndexFormat IndexFormat = ShaderPipelineIndexFormat.UInt16
) {
    /// <summary>Gets the bytes of one index.</summary>
    [JsonIgnore]
    public uint IndexBytes => ((IndexFormat == ShaderPipelineIndexFormat.UInt32)
        ? 4U
        : 2U);
    /// <summary>Gets the number of whole vertices the data holds.</summary>
    [JsonIgnore]
    public uint VertexCount => ((StrideBytes == 0)
        ? 0U
        : ((uint)((((ulong)Vertices.Count) * 4UL) / StrideBytes)));
    /// <summary>Gets the bytes of the vertex data, which a geometry buffer holds ahead of the indices.</summary>
    [JsonIgnore]
    public ulong VertexBytes => (((ulong)Vertices.Count) * 4UL);
    /// <summary>Gets the bytes of the geometry buffer: the vertex data, then the indices.</summary>
    [JsonIgnore]
    public ulong SizeBytes => (VertexBytes + (((ulong)Indices.Count) * IndexBytes));

    /// <summary>Returns the bytes a geometry buffer holds: the vertices as little-endian 32-bit floats, then the
    /// indices at their declared width, in declared order, starting at <see cref="VertexBytes"/>.</summary>
    /// <returns>The <see cref="SizeBytes"/> bytes.</returns>
    public byte[] BufferData() {
        var data = new byte[SizeBytes];
        var span = data.AsSpan();

        for (var index = 0; (index < Vertices.Count); index++) {
            BinaryPrimitives.WriteSingleLittleEndian(
                destination: span[(index * 4)..],
                value: Vertices[index]
            );
        }

        var indices = span[((int)VertexBytes)..];

        for (var position = 0; (position < Indices.Count); position++) {
            if (IndexFormat == ShaderPipelineIndexFormat.UInt16) {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    destination: indices[(position * 2)..],
                    value: checked((ushort)Indices[position])
                );
            } else {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination: indices[(position * 4)..],
                    value: Indices[position]
                );
            }
        }

        return data;
    }
}
/// <summary>One executable pass in a shader pipeline.</summary>
/// <param name="Name">The unique pass name.</param>
/// <param name="Source">The HLSL source's path, relative to the pipeline document; a package pass's package id.</param>
/// <param name="EntryPoint">The entry point compiled by the shader compiler: a compute pass's kernel, or a graphics
/// pass's fragment stage. A package pass compiles nothing and leaves it empty.</param>
/// <param name="Kind">Compute, fullscreen graphics, or indexed geometry; a frame graph's package passes are
/// <see cref="ShaderPipelinePassKind.Package"/>.</param>
/// <param name="Inputs">Named resource bindings. Set <see cref="ResourceReference.PreviousFrame"/> explicitly for feedback.</param>
/// <param name="Outputs">The versions the pass writes. A graphics pass writes one color image and, for a geometry pass,
/// at most one depth version.</param>
/// <param name="Config">Optional config fields, using the shared shader-set config vocabulary.</param>
/// <param name="GroupSizeX">Compute workgroup width; ignored for graphics passes.</param>
/// <param name="GroupSizeY">Compute workgroup height; ignored for graphics passes.</param>
/// <param name="GroupSizeZ">Compute workgroup depth; ignored for graphics passes.</param>
/// <param name="Vertex">How a fullscreen pass's vertex stage obtains the triangle's corners;
/// <see langword="null"/> means <see cref="ShaderPipelineVertexInput.VertexId"/>. Only a fullscreen pass declares
/// it.</param>
/// <param name="Geometry">A geometry pass's vertices, indices and vertex layout. Only a geometry pass declares it, and
/// it must.</param>
/// <param name="DepthCompare">A geometry pass's depth test, which it declares exactly when it writes a depth version;
/// <see langword="null"/> there means <see cref="ShaderPipelineDepthCompare.Less"/>. A passing fragment writes its
/// depth.</param>
/// <param name="Blend">A graphics pass's blend policy; <see langword="null"/> means
/// <see cref="ShaderPipelineBlend.Opaque"/>, the only policy the planner admits.</param>
/// <param name="AlphaTest">An alpha-test cutoff. The planner refuses every value by name.</param>
/// <param name="Dispatch">A compute or package pass's dispatch shape; <see langword="null"/> means
/// <see cref="ShaderPipelineDispatchKind.Extent"/>. A graphics pass declares none.</param>
public sealed record ShaderPipelinePass(
    string Name,
    string Source,
    string EntryPoint,
    ShaderPipelinePassKind Kind,
    IReadOnlyList<ResourceReference>? Inputs = null,
    IReadOnlyList<ResourceReference>? Outputs = null,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null,
    uint GroupSizeX = 8,
    uint GroupSizeY = 8,
    uint GroupSizeZ = 1,
    ShaderPipelineVertexInput? Vertex = null,
    ShaderPipelineGeometry? Geometry = null,
    ShaderPipelineDepthCompare? DepthCompare = null,
    ShaderPipelineBlend? Blend = null,
    double? AlphaTest = null,
    ShaderPipelineDispatch? Dispatch = null
) {
    /// <summary>Gets the version an indirect dispatch reads its group counts from, or <see langword="null"/>.</summary>
    [JsonIgnore]
    public string? DispatchArguments => ((Dispatch?.Kind == ShaderPipelineDispatchKind.Indirect)
        ? Dispatch.Arguments
        : null);
    /// <summary>Gets whether the pass draws through a render pass rather than dispatching.</summary>
    [JsonIgnore]
    public bool IsGraphics => (Kind is ShaderPipelinePassKind.Fullscreen or ShaderPipelinePassKind.Geometry);
    /// <summary>Gets an immutable empty input list when no resources are read.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> InputReferences => (Inputs ?? Array.Empty<ResourceReference>());
    /// <summary>Gets an immutable empty output list when no resources are written.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> OutputReferences => (Outputs ?? Array.Empty<ResourceReference>());
}
/// <summary>A complete data-authored, multi-pass shader pipeline.</summary>
[method: JsonConstructor]
public sealed record ShaderPipelineDefinition(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    IReadOnlyList<ShaderPipelineResource> Resources,
    IReadOnlyList<ShaderPipelinePass> Passes,
    IReadOnlyList<string> Outputs,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null
) {
    /// <summary>Initializes a pipeline using <see cref="ShaderPipelineSchemas.Pipeline"/>.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <param name="resources">The resource versions.</param>
    /// <param name="passes">The passes, in any order; the planner orders them.</param>
    /// <param name="outputs">The public versions, each named by its version name; the first is published by
    /// default.</param>
    public ShaderPipelineDefinition(
        string name,
        IReadOnlyList<ShaderPipelineResource> resources,
        IReadOnlyList<ShaderPipelinePass> passes,
        IReadOnlyList<string> outputs
    ) : this(
        Schema: ShaderPipelineSchemas.Pipeline,
        Name: name,
        Resources: resources,
        Passes: passes,
        Outputs: outputs,
        Config: null
    ) { }

    /// <summary>The schema tag expected by the planner.</summary>
    public const string SchemaTag = ShaderPipelineSchemas.Pipeline;

    /// <summary>Creates the minimal single-pass definition for a file-backed shader source.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <param name="sourcePath">The shader source path.</param>
    /// <param name="kind">The pass kind; when omitted, the pass is a compute pass.</param>
    /// <param name="entryPoint">The compiler entry point.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="sourcePath"/> is empty, or the source
    /// is not an <c>.hlsl</c> file.</exception>
    public static ShaderPipelineDefinition FromShaderSource(
        string name,
        string sourcePath,
        ShaderPipelinePassKind? kind = null,
        string entryPoint = "main"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        var extension = Path.GetExtension(path: sourcePath);

        if (!extension.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".hlsl"
        )) {
            throw new ArgumentException(
                message: $"Shader source extension '{extension}' is unsupported; a one-off shader is an .hlsl file.",
                paramName: nameof(sourcePath)
            );
        }
        var output = new ShaderPipelineResource(
            Name: "output",
            Kind: ShaderPipelineResourceKind.Image,
            Format: "R8G8B8A8Unorm",
            Dimensions: ShaderPipelineDimensions.Relative()
        );
        var pass = new ShaderPipelinePass(
            Name: name,
            Source: Path.GetFullPath(path: sourcePath),
            EntryPoint: entryPoint,
            Kind: (kind ?? ShaderPipelinePassKind.Compute),
            Outputs: [new ResourceReference(Name: output.Name)]
        );

        return new ShaderPipelineDefinition(
            name: name,
            outputs: [output.Name],
            passes: [pass],
            resources: [output]
        );
    }
}
/// <summary>Limits applied while compiling an execution plan. <c>MaxFrameBlockBytes</c> bounds a pass's frame block,
/// frame members and config together: 128 bytes is the push-constant size every Vulkan device guarantees.</summary>
public sealed record ShaderPipelineLimits(
    int MaxResources = 128,
    int MaxPasses = 128,
    int MaxInputsPerPass = 32,
    int MaxOutputsPerPass = 8,
    uint MaxFrameBlockBytes = 128,
    uint MaxComputeWorkGroupSizeX = 128,
    uint MaxComputeWorkGroupSizeY = 128,
    uint MaxComputeWorkGroupSizeZ = 64,
    uint MaxComputeWorkGroupInvocations = 128,
    int MaxVertexAttributes = 8,
    ulong MaxGeometryBytes = (1UL << 20)
);
/// <summary>A planner diagnostic with an actionable code and optional pass/resource name.</summary>
public sealed record ShaderPipelineDiagnostic(
    string Code,
    string Message,
    string? Name = null
);
/// <summary>Thrown when a pipeline cannot be made into a valid immutable execution plan.</summary>
public sealed class ShaderPipelineCompilationException : Exception {
    /// <summary>Initializes an exception with the planner's complete diagnostic set.</summary>
    public ShaderPipelineCompilationException(IReadOnlyList<ShaderPipelineDiagnostic> diagnostics)
        : base(message: string.Join(
        separator: Environment.NewLine,
        values: diagnostics.Select(selector: static diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}")
    )) {
        Diagnostics = new ReadOnlyCollection<ShaderPipelineDiagnostic>(list: diagnostics.ToList());
    }

    /// <summary>Gets all diagnostics collected before planning stopped.</summary>
    public IReadOnlyList<ShaderPipelineDiagnostic> Diagnostics { get; }
}
/// <summary>Source-generated metadata used by <see cref="ShaderPipelineLoader"/>.</summary>
[JsonSerializable(typeof(ShaderPipelineDefinition))]
[JsonSerializable(typeof(ShaderPipelineResource))]
[JsonSerializable(typeof(ShaderPipelinePass))]
[JsonSerializable(typeof(ResourceReference))]
[JsonSerializable(typeof(ShaderPipelineGeometry))]
[JsonSerializable(typeof(ShaderPipelineVertexAttribute))]
[JsonSerializable(typeof(ShaderPipelineDispatch))]
[JsonSerializable(typeof(ShaderPipelineCountTerm))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class ShaderPipelineJsonContext : JsonSerializerContext {
}
