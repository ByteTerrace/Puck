using System.Collections.ObjectModel;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Where every member of a <see cref="ShaderInterface"/> lands. The engine assigns all of it, so no backend's packing
/// rule or binding allocator is consulted:
/// <list type="bullet">
/// <item><description>Each group is Vulkan descriptor set and Direct3D 12 register space
/// <see cref="ShaderInterfaceGroup"/>'s ordinal.</description></item>
/// <item><description>A group with values owns one constant block at binding 0; its images, buffers, arrays and
/// samplers follow at bindings 1, 2, … in declaration order, or from 0 when the group has no block. A binding's Direct3D 12
/// register number equals its Vulkan binding number, in the register class its kind takes (<c>b</c>, <c>t</c>,
/// <c>u</c> or <c>s</c>).</description></item>
/// <item><description>A block places its values in declaration order: a scalar on a 4-byte boundary, a two-component
/// vector on an 8-byte boundary, and a three- or four-component vector on a 16-byte boundary. Every gap is filled with a
/// named <c>uint</c> padding member, so Direct3D 12's sequential constant-buffer packing lands each member exactly where
/// the explicit Vulkan offset puts it.</description></item>
/// <item><description>An array is a binding of its own: a read-only structured buffer of its scalar element type
/// (<see cref="GpuBindingKind.ReadOnlyBuffer"/>), whose stride is the element's size on both backends, so element
/// <c>i</c> lies at byte <c>4i</c> of the buffer bound there.</description></item>
/// <item><description>A buffer binding carries its element stride (<see cref="ShaderInterfaceBinding.ElementStride"/>): a
/// structured buffer's element size on both backends, and for a raw buffer the stride each backend's reflection reports
/// for a byte-address buffer, <see cref="SpirvRawBufferStride"/> and <see cref="DxilRawBufferStride"/>. SPIR-V declares
/// a raw buffer as a runtime array of <c>uint</c>, so it reflects a raw buffer and a structured buffer of a 4-byte
/// element alike; only DXIL's reflection tells those two apart.</description></item>
/// <item><description>A pushed index (<see cref="ShaderInterface.PushesIndex"/>) is a one-member block,
/// <c>pushedIndex.index</c>: SPIR-V reflects it as a pushed constant block at set 0, binding 0, as it reflects any push
/// constant, and DXIL as the constant buffer at register <c>b0</c> in space
/// <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>.</description></item>
/// </list>
/// </summary>
public sealed class ShaderInterfaceLayout {
    /// <summary>The element stride a SPIR-V module reflects for a raw (byte-address) buffer: DXC declares its block as a
    /// runtime array of <c>uint</c>, whose <c>ArrayStride</c> is 4.</summary>
    public const uint SpirvRawBufferStride = ShaderValueTypes.ComponentBytes;
    /// <summary>The element stride a DXIL container reflects for a raw (byte-address) buffer: DXC's reflection reports
    /// <c>NumSamples</c> as 0 for a <c>ByteAddressBuffer</c> and an <c>RWByteAddressBuffer</c>, the zero stride a raw
    /// Direct3D 12 view is written with.</summary>
    public const uint DxilRawBufferStride = 0;

    private const uint RowBytes = 16;

    private static readonly IReadOnlyList<ShaderInterfaceBlockMember> PushedIndexMembers = [new ShaderInterfaceBlockMember(
        Length: 0,
        Name: ShaderInterface.PushedIndexMemberName,
        Offset: 0,
        Type: ShaderValueType.Uint
    )];

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
                    members: members
                ));
            }
        }

        var spirv = groups.SelectMany(selector: static group => group.Bindings).ToList();
        var dxil = groups.SelectMany(selector: static group => group.Bindings.Select(selector: binding => (binding with {
            ElementStride = (IsRawBuffer(
                binding: binding,
                group: group
            )
                ? DxilRawBufferStride
                : binding.ElementStride),
        }))).ToList();

        if (shaderInterface.PushesIndex) {
            spirv.Add(item: new ShaderInterfaceBinding(
                Binding: 0,
                Kind: GpuBindingKind.ConstantBuffer,
                Members: PushedIndexMembers,
                Name: ShaderInterface.PushedIndexVariableName,
                Pushed: true,
                Set: 0
            ));
            dxil.Add(item: new ShaderInterfaceBinding(
                Binding: 0,
                Kind: GpuBindingKind.ConstantBuffer,
                Members: PushedIndexMembers,
                Name: ShaderInterface.PushedIndexVariableName,
                Set: GpuPipelineLayoutDescription.PushIndexSpace
            ));
        }

        Interface = shaderInterface;
        Groups = new ReadOnlyCollection<ShaderInterfaceGroupLayout>(list: groups);
        Bindings = Ordered(bindings: spirv);
        DxilBindings = Ordered(bindings: dxil);
    }

    /// <summary>Gets every binding the interface declares as a SPIR-V module reflects them, in
    /// <see cref="Ordered"/> order: the pushed index, when the interface declares one, at set 0, binding 0, and a raw
    /// buffer at <see cref="SpirvRawBufferStride"/>.</summary>
    public IReadOnlyList<ShaderInterfaceBinding> Bindings { get; }
    /// <summary>Gets <see cref="Bindings"/> as a DXIL container reflects them: identical, except that no block is pushed
    /// and a raw buffer's stride is <see cref="DxilRawBufferStride"/>. A pushed index is the constant buffer at
    /// register <c>b0</c> in space <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>.</summary>
    public IReadOnlyList<ShaderInterfaceBinding> DxilBindings { get; }
    /// <summary>Gets the groups the interface uses, in set order.</summary>
    public IReadOnlyList<ShaderInterfaceGroupLayout> Groups { get; }
    /// <summary>Gets the interface this layout places.</summary>
    public ShaderInterface Interface { get; }

    /// <summary>Returns the neutral pipeline layout of the interface's groups: each group at its set, each binding at
    /// its number with its kind and one descriptor, visible to the pipeline's stages, and the 4-byte index pushed when
    /// the interface declares one (<see cref="ShaderInterface.PushesIndex"/>).</summary>
    /// <param name="stages">The pipeline's shader stages, from its pass kind
    /// (<see cref="ShaderPipelinePassKinds.Stages"/>).</param>
    /// <returns>The pipeline layout.</returns>
    /// <exception cref="ArgumentException"><paramref name="stages"/> is not compute alone or graphics stages
    /// alone.</exception>
    public GpuPipelineLayoutDescription PipelineLayout(GpuShaderStage stages) =>
        new(
            groups: Groups.Select(selector: static group => new GpuGroupLayoutDescription(
                bindings: group.Bindings.Select(selector: static binding => new GpuGroupBinding(
                    binding: binding.Binding,
                    kind: binding.Kind
                )).ToArray(),
                ordinal: group.Set
            )).ToArray(),
            pushesIndex: Interface.PushesIndex,
            stages: stages
        );
    /// <summary>Returns why a compiled module binds something other than this layout places, or <see langword="null"/>
    /// when every binding it reads sits where the layout puts it. The module's bindings are held to one backend's view
    /// at a time, <see cref="Bindings"/> as SPIR-V reflects the layout or <see cref="DxilBindings"/> as DXIL does, and
    /// fit when every one of them fits the same view: each must be a binding of that view at its set and number, of the
    /// same kind, as pushed or bound as the view places it, with the same element stride; and a constant block's members
    /// must be the view's. A binding the module does not read is not reflected, so it is not checked. A DXIL container
    /// cannot tell root constants from a bound constant buffer, so in its view the pushed index is the constant buffer
    /// at register <c>b0</c> in space <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>. When neither
    /// view fits, the disagreement is named against the view that fits more of the module's bindings in order.</summary>
    /// <param name="reflected">The module's bindings, as <see cref="SpirvInterfaceReader"/> or
    /// <see cref="DxilInterfaceReader"/> reads them.</param>
    /// <returns>The disagreement, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reflected"/> is <see langword="null"/>.</exception>
    public string? Mismatch(IReadOnlyList<ShaderInterfaceBinding> reflected) {
        ArgumentNullException.ThrowIfNull(argument: reflected);

        if (FirstMismatch(
            expected: Bindings,
            reflected: reflected
        ) is not { } spirv) {
            return null;
        }
        if (FirstMismatch(
            expected: DxilBindings,
            reflected: reflected
        ) is not { } dxil) {
            return null;
        }

        var (_, binding, expected) = ((dxil.Index > spirv.Index)
            ? dxil
            : spirv);

        return $"the module reads {binding}; interface '{Interface.Name}' ({Interface.Hash}) lays out {((expected is null)
            ? $"no {(binding.Pushed ? "pushed " : "")}{binding.Kind} at set {binding.Set} binding {binding.Binding}"
            : expected.ToString())}.";
    }
    /// <summary>Orders bindings as both bytecode readers and a layout's views list them: by set, then binding, then a
    /// bound block before a pushed one at the same place, which is where SPIR-V reports a pushed index beside a bound
    /// frame block.</summary>
    /// <param name="bindings">The bindings.</param>
    /// <returns>The bindings in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ShaderInterfaceBinding> Ordered(IEnumerable<ShaderInterfaceBinding> bindings) {
        ArgumentNullException.ThrowIfNull(argument: bindings);

        return new ReadOnlyCollection<ShaderInterfaceBinding>(list: bindings
            .OrderBy(keySelector: static binding => binding.Set)
            .ThenBy(keySelector: static binding => binding.Binding)
            .ThenBy(keySelector: static binding => binding.Pushed)
            .ToArray());
    }

    private static uint AlignUp(uint value, uint alignment) =>
        ((((value + alignment) - 1) / alignment) * alignment);
    // The first reflected binding one view does not place, with the view's binding at its place, if any.
    private static (int Index, ShaderInterfaceBinding Binding, ShaderInterfaceBinding? Expected)? FirstMismatch(IReadOnlyList<ShaderInterfaceBinding> expected, IReadOnlyList<ShaderInterfaceBinding> reflected) {
        for (var index = 0; (index < reflected.Count); index++) {
            var binding = reflected[index];
            var candidate = expected.FirstOrDefault(predicate: candidate => (
                (candidate.Set == binding.Set) &&
                (candidate.Binding == binding.Binding) &&
                (candidate.Kind == binding.Kind) &&
                (candidate.Pushed == binding.Pushed)
            ));

            if (
                (candidate is null) ||
                (candidate.ElementStride != binding.ElementStride) ||
                !binding.Members.SequenceEqual(second: candidate.Members)
            ) {
                return (index, binding, candidate);
            }
        }

        return null;
    }
    // Whether a group's binding is a buffer with no element type, whose reflected stride is each backend's raw stride.
    private static bool IsRawBuffer(ShaderInterfaceGroupLayout group, ShaderInterfaceBinding binding) =>
        group.Resources.Any(predicate: resource => (
            (resource.Binding == binding.Binding) &&
            (resource.Kind is GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer) &&
            (resource.Member.Type is null)
        ));
    private static GpuBindingKind BindingKind(ShaderInterfaceMemberKind kind) =>
        kind switch {
            ShaderInterfaceMemberKind.SampledImage => GpuBindingKind.SampledImage,
            ShaderInterfaceMemberKind.StorageImage => GpuBindingKind.StorageImage,
            ShaderInterfaceMemberKind.ReadOnlyBuffer or ShaderInterfaceMemberKind.Array => GpuBindingKind.ReadOnlyBuffer,
            ShaderInterfaceMemberKind.ReadWriteBuffer => GpuBindingKind.ReadWriteBuffer,
            ShaderInterfaceMemberKind.Sampler => GpuBindingKind.Sampler,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The member kind is not a binding of its own.",
                paramName: nameof(kind)
            ),
        };
    private static ShaderInterfaceGroupLayout LayOutGroup(ShaderInterfaceGroup group, string interfaceName, IReadOnlyList<ShaderInterfaceMember> members) {
        var set = ((uint)group);
        var blockMembers = new List<ShaderInterfaceBlockMember>();
        var cursor = 0u;

        foreach (var member in members.Where(predicate: static member => member.IsBlockMember)) {
            var type = member.Type!.Value;
            var alignment = type.ComponentCount() switch {
                1 => 4u,
                2 => 8u,
                _ => RowBytes,
            };
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

            blockMembers.Add(item: new ShaderInterfaceBlockMember(
                Length: 0,
                Name: member.Name,
                Offset: offset,
                Type: type
            ));
            cursor = (offset + type.SizeBytes());
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
                Kind: GpuBindingKind.ConstantBuffer,
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
                ElementStride: (kind switch {
                    GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer => (member.Type?.SizeBytes() ?? SpirvRawBufferStride),
                    _ => 0,
                }),
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
            Resources: resources.AsReadOnly(),
            Set: set
        );
    }
}
/// <summary>One image, buffer, array or sampler member placed at its binding.</summary>
/// <param name="Member">The interface member.</param>
/// <param name="Binding">The Vulkan binding number, which is also the Direct3D 12 register number.</param>
/// <param name="Kind">The binding kind.</param>
public sealed record ShaderInterfaceResourceLayout(
    ShaderInterfaceMember Member,
    uint Binding,
    GpuBindingKind Kind
);
/// <summary>One frequency group of a <see cref="ShaderInterfaceLayout"/>.</summary>
/// <param name="Group">The frequency group.</param>
/// <param name="Set">The Vulkan descriptor set and Direct3D 12 register space.</param>
/// <param name="BlockTypeName">The HLSL struct name of the group's constant block, or <see langword="null"/> when the
/// group has no values.</param>
/// <param name="BlockVariableName">The HLSL variable name of the group's constant block, or <see langword="null"/>
/// when the group has no values.</param>
/// <param name="BlockMembers">The block's members in offset order, padding included; empty when the group has no
/// block.</param>
/// <param name="BlockSizeBytes">The block's size in bytes, a multiple of 16; zero when the group has no block.</param>
/// <param name="Resources">The group's images, buffers, arrays and samplers in binding order.</param>
/// <param name="Bindings">Every binding of the group, block first, in binding order.</param>
public sealed record ShaderInterfaceGroupLayout(
    ShaderInterfaceGroup Group,
    uint Set,
    string? BlockTypeName,
    string? BlockVariableName,
    IReadOnlyList<ShaderInterfaceBlockMember> BlockMembers,
    uint BlockSizeBytes,
    IReadOnlyList<ShaderInterfaceResourceLayout> Resources,
    IReadOnlyList<ShaderInterfaceBinding> Bindings
);
