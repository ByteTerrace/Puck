using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Puck.Shaders;

/// <summary>
/// The echo pass of an interface: a compute pass that reads every word of every block member through the generated
/// declarations and compares it with the sentinel <see cref="WriteSentinels"/> writes at that word, so a generator or
/// packing mistake shows on a real driver. It reads each group's block in set order, such as a document pass's frame
/// group block and then its pass block. Pixel <c>i</c> of its one-row output image is green when every word of the
/// <c>i</c>th member (padding excluded) reads back exactly, and red otherwise.
/// <para>The echo writes through its own output port named <see cref="OutputName"/>, which its interface
/// declares.</para>
/// <para>The source is a pure function of the interface: the same interface generates the same bytes, with LF line
/// endings, on every host.</para>
/// </summary>
public static class ShaderInterfaceEcho {
    /// <summary>The name of the storage image the echo pass writes its verdicts to.</summary>
    public const string OutputName = "echo";

    /// <summary>Returns the number of pixels the echo pass's output row holds: one per block member of every group,
    /// padding excluded.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The width, in pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The interface holds no block, or its echo has nowhere to write
    /// (<see cref="Generate"/>).</exception>
    public static uint Width(ShaderInterface shaderInterface) =>
        ((uint)BlockGroups(shaderInterface: shaderInterface).Sum(selector: static group => Members(group: group).Count()));
    /// <summary>Returns the sentinel an echo expects at one word of a group's block: a distinct normal float's bits for
    /// every word of every group, so no word reads as a denormal or a NaN a driver might flush or canonicalize, and a
    /// block read in another group's place reads wrong.</summary>
    /// <param name="set">The group's set.</param>
    /// <param name="word">The word's index: its byte offset from the start of the block divided by four.</param>
    /// <returns>The sentinel.</returns>
    public static uint Sentinel(uint set, uint word) =>
        0x40000000u | ((word + 1u) << 12) | (set << 8) | 0xA5u;
    /// <summary>Writes every member word of one group's block's sentinel and zeroes its padding.</summary>
    /// <param name="block">The block, at least the group's <see cref="ShaderInterfaceGroupLayout.BlockSizeBytes"/>
    /// long.</param>
    /// <param name="group">The group whose block it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    public static void WriteSentinels(Span<byte> block, ShaderInterfaceGroupLayout group) {
        ArgumentNullException.ThrowIfNull(argument: group);

        block[..((int)group.BlockSizeBytes)].Clear();

        foreach (var member in Members(group: group)) {
            for (var word = 0u; (word < Words(member: member)); word++) {
                var index = ((member.Offset / 4) + word);

                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination: block[((int)(index * 4))..],
                    value: Sentinel(
                        set: group.Set,
                        word: index
                    )
                );
            }
        }
    }
    /// <summary>Returns the interface an echo of <paramref name="shaderInterface"/> compiles against: the interface itself
    /// when it already declares its <see cref="OutputName"/> port, and otherwise the same members followed
    /// by a pass-group storage image named <see cref="OutputName"/>. The blocks are the interface's either way, so an echo
    /// of any document pass holds its blocks to their layout.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The interface its echo compiles against.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    public static ShaderInterface InterfaceOf(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        if (shaderInterface.Members.Any(predicate: static member => string.Equals(
            a: member.Name,
            b: OutputName,
            comparisonType: StringComparison.Ordinal
        ))) {
            return shaderInterface;
        }

        return new ShaderInterface(
            members: [.. shaderInterface.Members, ShaderInterfaceMember.StorageImage(
                format: Abstractions.Gpu.GpuPixelFormat.R8G8B8A8Unorm,
                group: ShaderInterfaceGroup.Pass,
                name: OutputName,
                type: ShaderValueType.Float4
            )],
            name: shaderInterface.Name
        );
    }
    /// <summary>Generates the echo pass's HLSL for an interface. It includes the interface's generated declarations
    /// (<see cref="ShaderFrameInterface.IncludeFileName"/>) and declares one compute entry point, <c>main</c>.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The interface holds no block, or declares no storage image named
    /// <see cref="OutputName"/>.</exception>
    public static string Generate(ShaderInterface shaderInterface) {
        var groups = BlockGroups(shaderInterface: shaderInterface);
        var text = new StringBuilder();

        Line(
            line: $"// The echo pass of shader interface '{shaderInterface.Name}' ({shaderInterface.Hash}), generated from the interface; never edit it.",
            text: text
        );
        Line(
            line: $"// Pixel i of '{OutputName}' is green when every word of the ith block member, in set order, reads back as the sentinel the host wrote there.",
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

        foreach (var group in groups) {
            foreach (var member in Members(group: group)) {
                var checks = new List<string>();

                for (var word = 0u; (word < Words(member: member)); word++) {
                    checks.Add(item: $"(asuint({Access(group: group, member: member, word: word)}) == 0x{Sentinel(set: group.Set, word: ((member.Offset / 4) + word)).ToString(format: "X8", provider: CultureInfo.InvariantCulture)}u)");
                }

                Line(
                    line: $"    {OutputName}[uint2({Number(value: pixel)}, 0)] = ({string.Join(separator: " && ", values: checks)}) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);",
                    text: text
                );
                pixel++;
            }
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
    // The groups whose blocks an echo reads, in set order, refusing an interface its echo cannot write through.
    private static IReadOnlyList<ShaderInterfaceGroupLayout> BlockGroups(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        var layout = shaderInterface.Layout();
        var groups = layout.Groups.Where(predicate: static group => (group.BlockMembers.Count != 0)).ToArray();

        if (groups.Length == 0) {
            throw new ArgumentException(
                message: $"Shader interface '{shaderInterface.Name}' holds no block for an echo pass to read.",
                paramName: nameof(shaderInterface)
            );
        }
        if (!shaderInterface.Members.Any(predicate: static member => (
            (member.Kind == ShaderInterfaceMemberKind.StorageImage) &&
            string.Equals(
                a: member.Name,
                b: OutputName,
                comparisonType: StringComparison.Ordinal
            )
        ))) {
            throw new ArgumentException(
                message: $"Shader interface '{shaderInterface.Name}' declares no storage image '{OutputName}' for its echo pass to write its verdicts to.",
                paramName: nameof(shaderInterface)
            );
        }

        return groups;
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
    private static uint Words(ShaderInterfaceBlockMember member) =>
        (member.Type.ComponentCount() * Math.Max(
            val1: 1u,
            val2: member.Length
        ));
}
