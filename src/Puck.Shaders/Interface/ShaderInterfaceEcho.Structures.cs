using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Puck.Shaders;

public static partial class ShaderInterfaceEcho {
    /// <summary>The two consecutive records an echo reads, so it checks element stride as well as field offsets.</summary>
    public const int RecordElements = 2;

    /// <summary>Writes the two structured records read by an interface echo, including zeroed padding.</summary>
    /// <param name="buffer">At least two elements of the resource's native record layout.</param>
    /// <param name="resource">A structured-record resource of the echoed interface.</param>
    /// <param name="set">The resource's descriptor set.</param>
    /// <exception cref="ArgumentException">The resource is not a structured record.</exception>
    public static void WriteRecordSentinels(Span<byte> buffer, ShaderInterfaceResourceLayout resource, uint set) {
        var structure = (resource.Member.Structure ?? throw new ArgumentException(message: "The echo resource has no structured record.", paramName: nameof(resource)));

        RequireRecordEcho(resource: resource, set: set);
        buffer[..checked((int)(structure.SizeBytes * RecordElements))].Clear();
        for (var element = 0u; (element < RecordElements); element++) {
            foreach (var member in structure.Members) {
                for (var word = 0u; (word < member.Type.ComponentCount()); word++) {
                    var offset = (((element * structure.SizeBytes) + member.Offset) + (word * sizeof(uint)));

                    BinaryPrimitives.WriteUInt32LittleEndian(destination: buffer[checked((int)offset)..],
                        value: RecordSentinel(set: set, binding: resource.Binding, word: (offset / sizeof(uint))));
                }
            }
        }
    }

    private static void AppendRecordChecks(StringBuilder text, ShaderInterfaceGroupLayout group, ref uint pixel) {
        foreach (var resource in group.Resources) {
            if (resource.Member.Structure is not { } structure) { continue; }
            RequireRecordEcho(resource: resource, set: group.Set);
            for (var element = 0u; (element < RecordElements); element++) {
                foreach (var member in structure.Members) {
                    var checks = new List<string>();

                    for (var word = 0u; (word < member.Type.ComponentCount()); word++) {
                        var component = ((member.Type.ComponentCount() == 1) ? "" : $".{"xyzw"[((int)word)]}");
                        var expected = RecordSentinel(set: group.Set, binding: resource.Binding,
                            word: ((((element * structure.SizeBytes) + member.Offset) / sizeof(uint)) + word));

                        checks.Add(item: $"(asuint({resource.Member.Name}[{element}].{member.Name}{component}) == 0x{expected.ToString(format: "X8", provider: CultureInfo.InvariantCulture)}u)");
                    }
                    Line(text: text, line: $"    {OutputName}[uint2({Number(value: pixel)}, 0)] = ({string.Join(separator: " && ", values: checks)}) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);");
                    pixel++;
                }
            }
        }
    }
    // Keep every sentinel a normal float; distinguish set, binding and native word without involving shader arithmetic.
    private static uint RecordSentinel(uint set, uint binding, uint word) =>
        0x40000000u | ((set & 3u) << 20) | ((binding & 255u) << 12) | ((word + 1u) & 4095u);
    private static void RequireRecordEcho(ShaderInterfaceResourceLayout resource, uint set) {
        if ((set > 3) || (resource.Binding > 255) || (resource.Member.Structure!.SizeBytes > 8192)) {
            throw new ArgumentException(message: $"Echo record '{resource.Member.Name}' exceeds four sets, 256 bindings or 8192 bytes per element; its sentinel identity would repeat.", paramName: nameof(resource));
        }
    }
}
