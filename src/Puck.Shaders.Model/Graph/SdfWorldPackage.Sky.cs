namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value the sky and composite passes read, one when the lit image and the surface transport
    /// the composite reads are the resolve's at the output extent (<see cref="Fragment"/>, <see cref="TemporalFragment"/>),
    /// zero when they are views' at the render extent with the visibility records, current only inside the dispatch box:
    /// the sky's in every fragment, and a <see cref="NativeFragment"/> composite's (<c>uint</c>).</summary>
    public const string ResolvedSurface = "resolvedSurface";
    /// <summary>The lit image the sky and composite passes read: views' color at the render extent for the sky, and the
    /// lit image at the output extent for the composite.</summary>
    public const string LitImage = "lit";
    /// <summary>The surface transport the composite reads in a reduced or temporal view (<see cref="Parts.Transport"/>).</summary>
    public const string TransportRead = "transport";
    /// <summary>The surface transport the resolve writes.</summary>
    public const string TransportWritten = "transportRW";
    /// <summary>The sky's lowest field run, read by the composite.</summary>
    public const string SkyBaseImage = "skyBase";
    /// <summary>The sky's lowest field run, written by the sky.</summary>
    public const string SkyBaseWritten = "skyBaseRW";
    /// <summary>The scale of the sky's upper field run, read by the composite.</summary>
    public const string SkyScaleImage = "skyScale";
    /// <summary>The scale of the sky's upper field run, written by the sky.</summary>
    public const string SkyScaleWritten = "skyScaleRW";
    /// <summary>The offset of the sky's upper field run, read by the composite.</summary>
    public const string SkyOffsetImage = "skyOffset";
    /// <summary>The offset of the sky's upper field run, written by the sky.</summary>
    public const string SkyOffsetWritten = "skyOffsetRW";

    /// <summary>The sky and composite interface: the common frame values and <see cref="ResolvedSurface"/>, the lit image,
    /// the visibility records and the dispatch box a native view's lit image is read through, the surface transport a
    /// reduced view's is, the sky's field runs as the sky writes them and as the composite reads them, the output, the
    /// work counters, and the World group's tables. Each pass binds a filler at every member it does not read or
    /// write.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> SkyMembers => SkyDeclaration.Members;

    // The sky's field runs, one image each, in the order the sky writes them: the base run, then the upper run's scale and
    // offset. A property, so the fragments that name them read them whatever order the partial files initialize in.
    private static string[] SkyRuns => [Parts.SkyBase, Parts.SkyScale, Parts.SkyOffset];
    // The images the sky's field runs are held in, at the render extent (the output extent in a native view): half
    // floats, one allocation every frame slot shares, since the composite reads them in the frame the sky writes them.
    // Each texel's base alpha says whether the sky evaluated it.
    private static ShaderPipelineResource[] SkyResources => [
        .. SkyRuns.Select(selector: static run => Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: run, transient: true)),
    ];

    private static class SkyDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: ResolvedSurface, type: ShaderValueType.Uint),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: LitImage, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: CullBounds),
            Read(element: ShaderValueType.Uint, name: TransportRead),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: SkyBaseImage, type: ShaderValueType.Float4),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: SkyScaleImage, type: ShaderValueType.Float4),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: SkyOffsetImage, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: SkyBaseWritten, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: SkyScaleWritten, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: SkyOffsetWritten, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            ShaderWorkCounters.BufferMember,
            .. Tables,
        ];
    }
}
