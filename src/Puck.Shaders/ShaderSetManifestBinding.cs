using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>One resource a <see cref="ShaderSetManifest"/>'s stages read from the set's pass group: its kind and the name
/// the stages read it by. Where it binds follows from the set's interface (<see cref="ShaderSetManifest.FrameLayout"/>),
/// never from the manifest: its members are the pass group's resources in the order the manifest lists them.</summary>
/// <param name="Kind">What the binding holds: a <see cref="GpuBindingKind.SampledImage"/> (a <c>Texture2D&lt;float4&gt;</c>),
/// a <see cref="GpuBindingKind.Sampler"/>, or a raw <see cref="GpuBindingKind.ReadOnlyBuffer"/> or
/// <see cref="GpuBindingKind.ReadWriteBuffer"/>. A set's block is its config, never a binding, and a storage image names
/// no format here, so neither is a manifest binding.</param>
/// <param name="Name">The HLSL identifier the stages read it by.</param>
public sealed record ShaderSetManifestBinding(
    GpuBindingKind Kind,
    string Name
) {
    /// <summary>Returns the pass-group member the binding declares.</summary>
    /// <returns>The member.</returns>
    /// <exception cref="InvalidDataException">The kind is one a manifest does not declare.</exception>
    public ShaderInterfaceMember ToMember() => Kind switch {
        GpuBindingKind.SampledImage => ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: Name,
            type: ShaderValueType.Float4
        ),
        GpuBindingKind.Sampler => ShaderInterfaceMember.Sampler(
            group: ShaderInterfaceGroup.Pass,
            name: Name
        ),
        GpuBindingKind.ReadOnlyBuffer => ShaderInterfaceMember.ReadOnlyBuffer(
            group: ShaderInterfaceGroup.Pass,
            name: Name
        ),
        GpuBindingKind.ReadWriteBuffer => ShaderInterfaceMember.ReadWriteBuffer(
            group: ShaderInterfaceGroup.Pass,
            name: Name
        ),
        _ => throw new InvalidDataException(message: $"Binding '{Name}' is a {Kind}, which a shader set does not declare: its block is its config, and a storage image needs a format a manifest does not state."),
    };
}
