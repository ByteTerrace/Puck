namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The native light table's count and current shadow selection.</summary>
    public const string LightFrame = "lightFrame";
    /// <summary>The generated native light records.</summary>
    public const string Lights = "lights";
    /// <summary>The native sky frame, containing resolved motion rather than rates.</summary>
    public const string SkyFrame = "skyFrame";
    /// <summary>The generated native gradient stops.</summary>
    public const string SkyStops = "skyStops";
    /// <summary>The generated native reflection panels.</summary>
    public const string SkySoftboxes = "skySoftboxes";

    /// <summary>The World-group tables read by shadow and hit shading.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> LightTables => LightingDeclarations.LightTables;
    /// <summary>The World-group tables read by sky and hit shading.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> SkyTables => LightingDeclarations.SkyTables;
    /// <summary>The sky interface adds sky records to the common traversal members.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> SkyMembers => LightingDeclarations.SkyMembers;
    /// <summary>The shadow interface adds only the light records.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ShadowMembers => LightingDeclarations.ShadowMembers;
    /// <summary>The hit-shading interface reads both the lights and sky records.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ViewsMembers => LightingDeclarations.ViewsMembers;

    // A nested holder keeps these arrays independent of partial-file static initialization order.
    private static class LightingDeclarations {
        internal static readonly ShaderInterfaceMember[] LightTables = [
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: LightFrame,
                structure: ShaderInterfaceStructure.From<SdfLightFrameData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: Lights,
                structure: ShaderInterfaceStructure.From<SdfLightData>()),
        ];
        internal static readonly ShaderInterfaceMember[] SkyTables = [
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: SkyFrame,
                structure: ShaderInterfaceStructure.From<SdfSkyFrameData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: SkyStops,
                structure: ShaderInterfaceStructure.From<SdfSkyStopData>()),
            ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: SkySoftboxes,
                structure: ShaderInterfaceStructure.From<SdfSoftboxData>()),
        ];
        internal static readonly ShaderInterfaceMember[] SkyMembers = [.. Members, .. SkyTables];
        internal static readonly ShaderInterfaceMember[] ShadowMembers = [.. Members, .. LightTables];
        internal static readonly ShaderInterfaceMember[] ViewsMembers = [.. Members, .. LightTables, .. SkyTables];
    }
}
