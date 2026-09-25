using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Puck.Shaders;

/// <summary>
/// The echo pass of a pushed-block interface: a compute pass that reads every word of every block member through the
/// generated declarations and compares it with the sentinel <see cref="WriteSentinels"/> writes at that word, so a
/// generator or packing mistake shows on a real driver. Pixel <c>i</c> of its one-row output image is green when every
/// word of the block's <c>i</c>th member (padding excluded) reads back exactly, and red otherwise.
/// <para>The source is a pure function of the interface: the same interface generates the same bytes, with LF line
/// endings, on every host.</para>
/// </summary>
public static class ShaderInterfaceEcho {
    /// <summary>The name of the storage image the echo pass writes its verdicts to, at set 0, binding 0, register
    /// <c>u0</c>.</summary>
    public const string OutputName = "echo";

    /// <summary>Returns the number of pixels the echo pass's output row holds: one per block member, padding
    /// excluded.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The width, in pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="shaderInterface"/> pushes no block, or declares a binding
    /// the echo pass's output would collide with.</exception>
    public static uint Width(ShaderInterface shaderInterface) =>
        ((uint)Members(group: PushedGroup(shaderInterface: shaderInterface)).Count());
    /// <summary>Returns the sentinel an echo expects at one word of a block: a distinct normal float's bits for every
    /// word, so no word reads as a denormal or a NaN a driver might flush or canonicalize.</summary>
    /// <param name="word">The word's index: its byte offset from the start of the block divided by four.</param>
    /// <returns>The sentinel.</returns>
    public static uint Sentinel(uint word) =>
        0x40000000u | ((word + 1u) << 12) | 0xA5u;
    /// <summary>Writes every member word of a frame block's sentinel and zeroes its padding.</summary>
    /// <param name="block">The block, at least <see cref="ShaderPipelineParameterLayout.SizeBytes"/> long.</param>
    /// <param name="layout">The block's layout.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    public static void WriteSentinels(Span<byte> block, ShaderPipelineParameterLayout layout) {
        ArgumentNullException.ThrowIfNull(argument: layout);

        block[..((int)layout.SizeBytes)].Clear();

        foreach (var member in Members(group: layout.Layout.PushedGroup!)) {
            for (var word = 0u; (word < Words(member: member)); word++) {
                var index = ((member.Offset / 4) + word);

                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination: block[((int)(index * 4))..],
                    value: Sentinel(word: index)
                );
            }
        }
    }
    /// <summary>Generates the echo pass's HLSL for an interface. It includes the interface's generated declarations
    /// (<see cref="ShaderFrameInterface.IncludeFileName"/>) and declares one compute entry point, <c>main</c>.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="shaderInterface"/> pushes no block, or declares a binding
    /// the echo pass's output would collide with.</exception>
    public static string Generate(ShaderInterface shaderInterface) {
        var group = PushedGroup(shaderInterface: shaderInterface);
        var text = new StringBuilder();

        Line(
            line: $"// The echo pass of shader interface '{shaderInterface.Name}' ({shaderInterface.Hash}), generated from the interface; never edit it.",
            text: text
        );
        Line(
            line: $"// Pixel i of '{OutputName}' is green when every word of the block's ith member reads back as the sentinel the host wrote there.",
            text: text
        );
        Line(
            line: $"#include \"{ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)}\"",
            text: text
        );
        Line(
            line: "",
            text: text
        );
        Line(
            line: $"[[vk::binding(0, 0)]] [[vk::image_format(\"rgba8\")]] RWTexture2D<float4> {OutputName} : register(u0, space0);",
            text: text
        );
        Line(
            line: "",
            text: text
        );
        Line(
            line: "[numthreads(8, 8, 1)]",
            text: text
        );
        Line(
            line: "void main(uint3 id : SV_DispatchThreadID) {",
            text: text
        );
        Line(
            line: "    if (any(id.xy != 0)) {",
            text: text
        );
        Line(
            line: "        return;",
            text: text
        );
        Line(
            line: "    }",
            text: text
        );

        var pixel = 0u;

        foreach (var member in Members(group: group)) {
            var checks = new List<string>();

            for (var word = 0u; (word < Words(member: member)); word++) {
                checks.Add(item: $"(asuint({Access(group: group, member: member, word: word)}) == 0x{Sentinel(word: ((member.Offset / 4) + word)).ToString(format: "X8", provider: CultureInfo.InvariantCulture)}u)");
            }

            Line(
                line: $"    {OutputName}[uint2({Number(value: pixel)}, 0)] = ({string.Join(separator: " && ", values: checks)}) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);",
                text: text
            );
            pixel++;
        }

        Line(
            line: "}",
            text: text
        );

        return text.ToString();
    }

    private static string Access(ShaderInterfaceGroupLayout group, ShaderInterfaceBlockMember member, uint word) {
        var components = member.Type.ComponentCount();
        var element = ((member.Length == 0)
            ? ""
            : $"[{Number(value: (word / components))}]");
        var component = ((components == 1)
            ? ""
            : $".{"xyzw"[((int)(word % components))]}");

        return $"{group.BlockVariableName}.{member.Name}{element}{component}";
    }
    private static void Line(StringBuilder text, string line) =>
        text.Append(value: line).Append(value: '\n');
    private static IEnumerable<ShaderInterfaceBlockMember> Members(ShaderInterfaceGroupLayout group) =>
        group.BlockMembers.Where(predicate: static member => !member.Name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "_pad"
        ));
    private static string Number(uint value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
    private static ShaderInterfaceGroupLayout PushedGroup(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        var layout = shaderInterface.Layout();

        if (layout.PushedGroup is not { } group) {
            throw new ArgumentException(
                message: $"Shader interface '{shaderInterface.Name}' pushes no block for an echo pass to read.",
                paramName: nameof(shaderInterface)
            );
        }
        if (layout.Bindings.Any(predicate: static binding => (!binding.Pushed && (binding.Set == 0) && (binding.Binding == 0)))) {
            throw new ArgumentException(
                message: $"Shader interface '{shaderInterface.Name}' binds set 0, binding 0, where the echo pass writes its verdicts.",
                paramName: nameof(shaderInterface)
            );
        }

        return group;
    }
    private static uint Words(ShaderInterfaceBlockMember member) =>
        (member.Type.ComponentCount() * Math.Max(
            val1: 1u,
            val2: member.Length
        ));
}
