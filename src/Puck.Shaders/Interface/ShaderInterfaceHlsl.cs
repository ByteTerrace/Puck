using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Generates the HLSL include a pass reads its <see cref="ShaderInterface"/> through. Every declaration carries its
/// placement explicitly and for both backends at once: a block member carries <c>[[vk::offset(n)]]</c> and sits after
/// named padding that makes Direct3D 12's packing land on the same offset, and a binding carries
/// <c>[[vk::binding(b, set)]]</c> paired with <c>register(xb, spaceset)</c>. Values reach a pass as members of a named struct per group, and an array is read through a generated accessor that hides how
/// its elements are stored. A buffer with an element type is a <c>StructuredBuffer&lt;T&gt;</c> or
/// <c>RWStructuredBuffer&lt;T&gt;</c>, and one without is a <c>ByteAddressBuffer</c> or <c>RWByteAddressBuffer</c>. A
/// pushed index (<see cref="ShaderInterface.PushesIndex"/>) follows the groups as a one-member struct carrying
/// <c>[[vk::push_constant]]</c> paired with <c>register(b0, space4)</c>, the space
/// <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/> names.
/// <para>The text is a pure function of the interface: the same interface generates the same bytes, with LF line
/// endings, on every host.</para>
/// </summary>
public static class ShaderInterfaceHlsl {
    /// <summary>Returns the file name a generated include takes: the interface name followed by
    /// <c>.interface.hlsli</c>.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    public static string FileName(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        return (shaderInterface.Name + ".interface.hlsli");
    }
    /// <summary>Generates the include for an interface.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    public static string Generate(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        var layout = shaderInterface.Layout();
        var guard = ("PUCK_SHADER_INTERFACE_" + shaderInterface.Name.Replace(
            newChar: '_',
            oldChar: '-'
        ).ToUpperInvariant());
        var text = new StringBuilder();

        Line(
            line: $"// Generated from shader interface '{shaderInterface.Name}' ({shaderInterface.Hash}). Regenerate it from the interface; never edit it.",
            text: text
        );
        Line(
            line: $"#ifndef {guard}",
            text: text
        );
        Line(
            line: $"#define {guard}",
            text: text
        );

        foreach (var group in layout.Groups) {
            Line(
                line: "",
                text: text
            );
            Line(
                line: $"// The {group.Group} group: descriptor set {group.Set}, register space {group.Set}.",
                text: text
            );

            if (group.BlockTypeName is not null) {
                Line(
                    line: $"struct {group.BlockTypeName} {{",
                    text: text
                );

                foreach (var member in group.BlockMembers) {
                    Line(
                        line: $"    [[vk::offset({Number(value: member.Offset)})]] {member.Type.Spelling()} {member.Name}{((member.Length == 0)
                            ? ""
                            : $"[{Number(value: member.Length)}]")};",
                        text: text
                    );
                }

                Line(
                    line: "};",
                    text: text
                );
                Line(
                    line: $"{Binding(binding: 0, set: group.Set)} ConstantBuffer<{group.BlockTypeName}> {group.BlockVariableName}{Register(binding: 0, register: 'b', set: group.Set)};",
                    text: text
                );
            }

            foreach (var resource in group.Resources) {
                Line(
                    line: Declaration(
                        resource: resource,
                        set: group.Set
                    ),
                    text: text
                );
            }
        }

        if (shaderInterface.PushesIndex) {
            var typeName = ShaderInterface.PushedIndexTypeName(interfaceName: shaderInterface.Name);
            var space = Number(value: GpuPipelineLayoutDescription.PushIndexSpace);

            Line(
                line: "",
                text: text
            );
            Line(
                line: $"// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space {space}.",
                text: text
            );
            Line(
                line: $"struct {typeName} {{",
                text: text
            );
            Line(
                line: $"    [[vk::offset(0)]] uint {ShaderInterface.PushedIndexMemberName};",
                text: text
            );
            Line(
                line: "};",
                text: text
            );
            Line(
                line: $"[[vk::push_constant]] ConstantBuffer<{typeName}> {ShaderInterface.PushedIndexVariableName}{Register(binding: 0, register: 'b', set: GpuPipelineLayoutDescription.PushIndexSpace)};",
                text: text
            );
        }

        // An array reads zero past its declared length, whatever the buffer bound for it holds there.
        var arrays = layout.Groups.SelectMany(selector: static group => group.Resources)
            .Select(selector: static resource => resource.Member)
            .Where(predicate: static member => (member.Kind == ShaderInterfaceMemberKind.Array))
            .ToArray();

        if (arrays.Length != 0) {
            Line(
                line: "",
                text: text
            );
        }

        foreach (var member in arrays) {
            var type = member.Type!.Value.Spelling();

            Line(
                line: $"{type} {ShaderInterface.AccessorName(member: member)}(uint index) {{ return ((index < {Number(value: member.Length!.Value)}u) ? {member.Name}[index] : (({type})0)); }}",
                text: text
            );
        }

        Line(
            line: "",
            text: text
        );
        Line(
            line: $"#endif // {guard}",
            text: text
        );

        return text.ToString();
    }

    private static string Binding(uint binding, uint set) =>
        $"[[vk::binding({Number(value: binding)}, {Number(value: set)})]]";
    private static string Declaration(ShaderInterfaceResourceLayout resource, uint set) {
        var member = resource.Member;

        var (register, declaration) = resource.Kind switch {
            GpuBindingKind.SampledImage => ('t', $"Texture2D<{member.Type!.Value.Spelling()}> {member.Name}"),
            GpuBindingKind.StorageImage => ('u', $"[[vk::image_format(\"{ShaderInterface.StorageFormatSpelling(format: member.Format!.Value)}\")]] RWTexture2D<{member.Type!.Value.Spelling()}> {member.Name}"),
            GpuBindingKind.ReadOnlyBuffer => ('t', ((member.Type is { } element)
                ? $"StructuredBuffer<{element.Spelling()}> {member.Name}"
                : $"ByteAddressBuffer {member.Name}")),
            GpuBindingKind.ReadWriteBuffer => ('u', ((member.Type is { } element)
                ? $"RWStructuredBuffer<{element.Spelling()}> {member.Name}"
                : $"RWByteAddressBuffer {member.Name}")),
            GpuBindingKind.Sampler => ('s', $"SamplerState {member.Name}"),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: resource.Kind,
                message: "The binding kind is not generated for an interface member.",
                paramName: nameof(resource)
            ),
        };

        return $"{Binding(binding: resource.Binding, set: set)} {declaration}{Register(binding: resource.Binding, register: register, set: set)};";
    }
    private static void Line(StringBuilder text, string line) =>
        text.Append(value: line).Append(value: '\n');
    private static string Number(uint value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
    private static string Register(uint binding, uint set, char register) =>
        $" : register({register}{Number(value: binding)}, space{Number(value: set)})";
}
