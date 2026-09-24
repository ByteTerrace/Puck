using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>
/// Where every member of a <see cref="ShaderInterface"/> lands. The engine assigns all of it, so no backend's packing
/// rule or binding allocator is consulted:
/// <list type="bullet">
/// <item><description>Each group is Vulkan descriptor set and Direct3D 12 register space
/// <see cref="ShaderInterfaceGroup"/>'s ordinal.</description></item>
/// <item><description>A group with values or arrays owns one constant block at binding 0; its images and samplers
/// follow at bindings 1, 2, … in declaration order, or from 0 when the group has no block. A binding's Direct3D 12
/// register number equals its Vulkan binding number, in the register class its kind takes (<c>b</c>, <c>t</c>,
/// <c>u</c> or <c>s</c>).</description></item>
/// <item><description>A block places its members in declaration order: a scalar on a 4-byte boundary, a two-component
/// vector on an 8-byte boundary, a three- or four-component vector and every array on a 16-byte boundary. An array
/// element is stored as one whole 16-byte row, so an array's stride is 16 on both backends, and the member after an
/// array starts past its last row. Every gap is filled with a named <c>uint</c> padding member, so Direct3D 12's
/// sequential constant-buffer packing lands each member exactly where the explicit Vulkan offset puts
/// it.</description></item>
/// <item><description>A pushed group's block (<see cref="ShaderInterface.PushConstants"/>) is a
/// <see cref="ShaderBindingKind.PushConstants"/> binding at binding 0 of its set, placed by the same rule.</description></item>
/// </list>
/// </summary>
public sealed class ShaderInterfaceLayout {
    private const uint RowBytes = 16;

    /// <summary>Initializes a new instance of the <see cref="ShaderInterfaceLayout"/> class.</summary>
    /// <param name="shaderInterface">The interface to lay out.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    public ShaderInterfaceLayout(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        var groups = new List<ShaderInterfaceGroupLayout>();

        foreach (var group in Enum.GetValues<ShaderInterfaceGroup>()) {
            var members = shaderInterface.Members.Where(predicate: member => (member.Group == group)).ToArray();

            if (members.Length != 0) {
                groups.Add(item: LayOutGroup(
                    group: group,
                    interfaceName: shaderInterface.Name,
                    members: members,
                    pushed: (shaderInterface.PushConstants == group)
                ));
            }
        }

        Interface = shaderInterface;
        Groups = new ReadOnlyCollection<ShaderInterfaceGroupLayout>(list: groups);
        Bindings = new ReadOnlyCollection<ShaderInterfaceBinding>(list: groups.SelectMany(selector: static group => group.Bindings).ToArray());
        DxilBindings = new ReadOnlyCollection<ShaderInterfaceBinding>(list: Bindings.Select(selector: static binding => ((binding.Kind == ShaderBindingKind.PushConstants)
            ? (binding with { Kind = ShaderBindingKind.ConstantBuffer })
            : binding)).ToArray());
    }

    /// <summary>Gets every binding the interface declares, ordered by set and then binding, as a SPIR-V module reflects
    /// them.</summary>
    public IReadOnlyList<ShaderInterfaceBinding> Bindings { get; }
    /// <summary>Gets <see cref="Bindings"/> as a DXIL container reflects them: identical, except that a pushed block is a
    /// root-constant block, which Direct3D 12 reflects as the constant buffer at register <c>b0</c>, space 0.</summary>
    public IReadOnlyList<ShaderInterfaceBinding> DxilBindings { get; }
    /// <summary>Gets the pushed group's layout, or <see langword="null"/> when the interface pushes no group.</summary>
    public ShaderInterfaceGroupLayout? PushedGroup =>
        Groups.FirstOrDefault(predicate: group => (group.Group == Interface.PushConstants));
    /// <summary>Gets the groups the interface uses, in set order.</summary>
    public IReadOnlyList<ShaderInterfaceGroupLayout> Groups { get; }
    /// <summary>Gets the interface this layout places.</summary>
    public ShaderInterface Interface { get; }

    /// <summary>Returns why a compiled module reads the pushed block somewhere other than this layout puts it, or
    /// <see langword="null"/> when it reads the block exactly as laid out or does not read it. The block is the binding
    /// at set 0, binding 0 named for the pushed group, whether a SPIR-V module reflects it as push constants or a DXIL
    /// container as a constant buffer.</summary>
    /// <param name="reflected">The module's bindings, as <see cref="SpirvInterfaceReader"/> or
    /// <see cref="DxilInterfaceReader"/> reads them.</param>
    /// <returns>The disagreement, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflected"/> is <see langword="null"/>.</exception>
    public string? PushedBlockMismatch(IReadOnlyList<ShaderInterfaceBinding> reflected) {
        ArgumentNullException.ThrowIfNull(argument: reflected);

        if (PushedGroup is not { } group) {
            return null;
        }

        var block = reflected.FirstOrDefault(predicate: binding => (
            (binding.Kind is ShaderBindingKind.PushConstants or ShaderBindingKind.ConstantBuffer) &&
            (binding.Set == group.Set) &&
            (binding.Binding == 0) &&
            string.Equals(
                a: binding.Name,
                b: group.BlockVariableName,
                comparisonType: StringComparison.Ordinal
            )
        ));

        if (block is null) {
            return null;
        }
        if (block.Members.SequenceEqual(second: group.BlockMembers)) {
            return null;
        }

        var expected = new ShaderInterfaceBinding(
            Binding: 0,
            Kind: block.Kind,
            Members: group.BlockMembers,
            Name: block.Name,
            Set: group.Set
        );

        return $"the module reads {block}; interface '{Interface.Name}' ({Interface.Hash}) lays out {expected}.";
    }

    private static uint AlignUp(uint value, uint alignment) =>
        ((((value + alignment) - 1) / alignment) * alignment);
    private static ShaderBindingKind BindingKind(ShaderInterfaceMemberKind kind) =>
        kind switch {
            ShaderInterfaceMemberKind.SampledImage => ShaderBindingKind.SampledImage,
            ShaderInterfaceMemberKind.StorageImage => ShaderBindingKind.StorageImage,
            ShaderInterfaceMemberKind.Sampler => ShaderBindingKind.Sampler,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The member kind is not a binding of its own.",
                paramName: nameof(kind)
            ),
        };
    private static ShaderInterfaceGroupLayout LayOutGroup(ShaderInterfaceGroup group, string interfaceName, IReadOnlyList<ShaderInterfaceMember> members, bool pushed) {
        var set = ((uint)group);
        var blockMembers = new List<ShaderInterfaceBlockMember>();
        var cursor = 0u;

        foreach (var member in members.Where(predicate: static member => member.IsBlockMember)) {
            var type = member.Type!.Value;
            var isArray = (member.Kind == ShaderInterfaceMemberKind.Array);
            var alignment = (isArray
                ? RowBytes
                : type.ComponentCount() switch {
                    1 => 4u,
                    2 => 8u,
                    _ => RowBytes,
                });
            var offset = AlignUp(
                alignment: alignment,
                value: cursor
            );

            for (; (cursor < offset); cursor += ShaderValueTypes.ComponentBytes) {
                blockMembers.Add(item: new ShaderInterfaceBlockMember(
                    Length: 0,
                    Name: $"_pad{cursor}",
                    Offset: cursor,
                    Type: ShaderValueType.Uint
                ));
            }

            if (isArray) {
                blockMembers.Add(item: new ShaderInterfaceBlockMember(
                    Length: member.Length!.Value,
                    Name: member.Name,
                    Offset: offset,
                    Type: RowType(type: type)
                ));
                cursor = (offset + (RowBytes * member.Length.Value));
            } else {
                blockMembers.Add(item: new ShaderInterfaceBlockMember(
                    Length: 0,
                    Name: member.Name,
                    Offset: offset,
                    Type: type
                ));
                cursor = (offset + type.SizeBytes());
            }
        }

        var bindings = new List<ShaderInterfaceBinding>();
        var resources = new List<ShaderInterfaceResourceLayout>();
        string? blockTypeName = null;
        string? blockVariableName = null;

        if (blockMembers.Count != 0) {
            blockTypeName = ShaderInterface.BlockTypeName(
                group: group,
                interfaceName: interfaceName
            );
            blockVariableName = ShaderInterface.BlockVariableName(group: group);
            bindings.Add(item: new ShaderInterfaceBinding(
                Binding: 0,
                Kind: (pushed
                    ? ShaderBindingKind.PushConstants
                    : ShaderBindingKind.ConstantBuffer),
                Members: blockMembers.AsReadOnly(),
                Name: blockVariableName,
                Set: set
            ));
        }

        foreach (var member in members.Where(predicate: static member => !member.IsBlockMember)) {
            var binding = ((uint)bindings.Count);
            var kind = BindingKind(kind: member.Kind);

            resources.Add(item: new ShaderInterfaceResourceLayout(
                Binding: binding,
                Kind: kind,
                Member: member
            ));
            bindings.Add(item: new ShaderInterfaceBinding(
                Binding: binding,
                Kind: kind,
                Members: [],
                Name: member.Name,
                Set: set
            ));
        }

        return new ShaderInterfaceGroupLayout(
            BlockMembers: blockMembers.AsReadOnly(),
            BlockSizeBytes: AlignUp(
                alignment: RowBytes,
                value: cursor
            ),
            BlockTypeName: blockTypeName,
            BlockVariableName: blockVariableName,
            Bindings: bindings.AsReadOnly(),
            Group: group,
            Pushed: pushed,
            Resources: resources.AsReadOnly(),
            Set: set
        );
    }
    private static ShaderValueType RowType(ShaderValueType type) =>
        ShaderValueTypes.FromComponents(
            count: 4,
            kind: type.ScalarKind()
        );
}
/// <summary>One image or sampler member placed at its binding.</summary>
/// <param name="Member">The interface member.</param>
/// <param name="Binding">The Vulkan binding number, which is also the Direct3D 12 register number.</param>
/// <param name="Kind">The binding kind.</param>
public sealed record ShaderInterfaceResourceLayout(
    ShaderInterfaceMember Member,
    uint Binding,
    ShaderBindingKind Kind
);
/// <summary>One frequency group of a <see cref="ShaderInterfaceLayout"/>.</summary>
/// <param name="Group">The frequency group.</param>
/// <param name="Set">The Vulkan descriptor set and Direct3D 12 register space.</param>
/// <param name="BlockTypeName">The HLSL struct name of the group's constant block, or <see langword="null"/> when the
/// group has no values or arrays.</param>
/// <param name="BlockVariableName">The HLSL variable name of the group's constant block, or <see langword="null"/>
/// when the group has no values or arrays.</param>
/// <param name="BlockMembers">The block's members in offset order, padding included; empty when the group has no
/// block.</param>
/// <param name="BlockSizeBytes">The block's size in bytes, a multiple of 16; zero when the group has no block.</param>
/// <param name="Resources">The group's images and samplers in binding order.</param>
/// <param name="Bindings">Every binding of the group, block first, in binding order.</param>
/// <param name="Pushed">Whether the group's block is delivered as push constants rather than bound as a constant
/// buffer.</param>
public sealed record ShaderInterfaceGroupLayout(
    ShaderInterfaceGroup Group,
    uint Set,
    string? BlockTypeName,
    string? BlockVariableName,
    IReadOnlyList<ShaderInterfaceBlockMember> BlockMembers,
    uint BlockSizeBytes,
    IReadOnlyList<ShaderInterfaceResourceLayout> Resources,
    IReadOnlyList<ShaderInterfaceBinding> Bindings,
    bool Pushed
);
