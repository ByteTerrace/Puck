namespace Puck.Shaders;

/// <summary>A registered sky kind's native resource, generated from the authoring inventory. Consumers use the
/// member's declared structure and stride rather than carrying a second upload layout.</summary>
/// <param name="Name">The authored discriminator.</param>
/// <param name="Tag">The dispatcher tag for this generated shader revision.</param>
/// <param name="Member">The native read-only parameter table declaration.</param>
public readonly record struct SdfSkyKindBinding(string Name, uint Tag, ShaderInterfaceMember Member);
