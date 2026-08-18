using System.Buffers.Binary;
using System.Text;

namespace Puck.Shaders.Tests;

public sealed class ShaderPushConstantLayoutTests {
    private const int SpirvDecorationOffset = 35;
    private const int SpirvOpMemberDecorate = 72;
    private const int SpirvOpName = 5;

    // `dxc -Wno-ignored-attributes -enable-16bit-types -O3 -T cs_6_6 -E CSMain -Fc out.txt push-constant-layout.comp.hlsl`
    // prints these under `cbuffer pc`, one `; Offset:` per member and `Size: 96` for the block.
    private static readonly uint[] DxilOffsets = [0, 4, 12, 16, 28, 32, 36, 48, 64, 68, 80, 88, 92];

    private const uint DxilSizeBytes = 96;

    private static string ShaderDirectory =>
        Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders");

    [Fact]
    public void Computed_layout_matches_the_offsets_dxc_assigned_in_the_compiled_spirv() {
        var manifest = ShaderSetManifest.Load(manifestPath: Path.Combine(path1: ShaderDirectory, path2: "push-constant-layout.puck.shader.json"));
        var layout = Assert.IsType<ShaderPushConstantLayout>(@object: manifest.PushConstantLayout);
        var spirvOffsets = ReadSpirvMemberOffsets(path: manifest.BytecodePath(stem: manifest.Stages.Compute!, bytecodeExtension: ".spv"), structName: "type.ConstantBuffer.LayoutPushData");

        Assert.Equal(expected: layout.Slots.Count, actual: spirvOffsets.Count);

        for (var index = 0; (index < layout.Slots.Count); index++) {
            Assert.Equal(expected: spirvOffsets[index], actual: layout.Slots[index].Offset);
        }
    }
    [Fact]
    public void Computed_layout_matches_the_offsets_dxc_assigned_in_the_dxil() {
        var manifest = ShaderSetManifest.Load(manifestPath: Path.Combine(path1: ShaderDirectory, path2: "push-constant-layout.puck.shader.json"));
        var layout = Assert.IsType<ShaderPushConstantLayout>(@object: manifest.PushConstantLayout);

        Assert.Equal(expected: DxilOffsets, actual: layout.Slots.Select(selector: slot => slot.Offset).ToArray());
        Assert.Equal(expected: DxilSizeBytes, actual: layout.SizeBytes);
    }
    [Fact]
    public void A_vector_is_bumped_to_the_next_row_only_when_it_would_straddle_one() {
        Assert.Equal(expected: [0u, 4u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out var size1, types: [ShaderValueType.Float, ShaderValueType.Float2]));
        Assert.Equal(actual: size1, expected: 12u);
        Assert.Equal(expected: [0u, 4u, 8u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out _, types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float2]));
        Assert.Equal(expected: [0u, 4u, 8u, 16u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out _, types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float2]));
        Assert.Equal(expected: [0u, 4u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out _, types: [ShaderValueType.Float, ShaderValueType.Float3]));
        Assert.Equal(expected: [0u, 4u, 16u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out _, types: [ShaderValueType.Float, ShaderValueType.Float, ShaderValueType.Float3]));
        Assert.Equal(expected: [0u, 16u, 32u], actual: ShaderPushConstantLayout.ComputeOffsets(sizeBytes: out var size2, types: [ShaderValueType.Float, ShaderValueType.Float4, ShaderValueType.Uint]));
        Assert.Equal(actual: size2, expected: 36u);
    }
    [Fact]
    public void Every_slot_source_resolves_as_declared() {
        var manifest = ShaderSetManifest.Load(manifestPath: Path.Combine(path1: ShaderDirectory, path2: "push-constant-layout.puck.shader.json"));
        var slots = manifest.PushConstantLayout!.Slots;

        Assert.Equal(expected: ShaderPushConstantSourceKind.Config, actual: slots[0].Kind);
        Assert.Equal(expected: "a", actual: slots[0].ConfigField);
        Assert.Equal(expected: ShaderPushConstantSourceKind.Tick, actual: slots[4].Kind);
        Assert.Equal(expected: 24u, actual: slots[4].QuantizeHzLiteral);
        Assert.Equal(expected: ShaderPushConstantSourceKind.Resolution, actual: slots[6].Kind);
        Assert.Equal(expected: ShaderPushConstantSourceKind.Tick, actual: slots[10].Kind);
        Assert.Equal(expected: "rate", actual: slots[10].QuantizeHzConfigField);
        Assert.Equal(expected: ShaderPushConstantSourceKind.Frame, actual: slots[11].Kind);
    }

    private static IReadOnlyList<uint> ReadSpirvMemberOffsets(string path, string structName) {
        var bytes = File.ReadAllBytes(path: path);
        var words = new uint[(bytes.Length / 4)];

        for (var index = 0; (index < words.Length); index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(length: 4, start: (index * 4)));
        }

        uint? structId = null;
        var offsets = new SortedDictionary<uint, uint>();

        for (var cursor = 5; (cursor < words.Length);) {
            var wordCount = ((int)(words[cursor] >> 16));
            var opcode = ((int)(words[cursor] & 0xFFFF));

            if ((opcode == SpirvOpName) && (wordCount >= 3)) {
                var name = ReadLiteralString(end: (cursor + wordCount), start: (cursor + 2), words: words);

                if (string.Equals(a: name, b: structName, comparisonType: StringComparison.Ordinal)) {
                    structId = words[(cursor + 1)];
                }
            } else if ((opcode == SpirvOpMemberDecorate) && (wordCount >= 5) && (words[(cursor + 3)] == SpirvDecorationOffset) && (structId == words[(cursor + 1)])) {
                offsets[words[(cursor + 2)]] = words[(cursor + 4)];
            }

            cursor += wordCount;
        }

        Assert.NotNull(value: structId);

        return offsets.Values.ToList();
    }
    private static string ReadLiteralString(uint[] words, int start, int end) {
        var builder = new StringBuilder();

        for (var index = start; (index < end); index++) {
            var word = words[index];

            for (var shift = 0; (shift < 32); shift += 8) {
                var value = ((byte)(word >> shift));

                if (value == 0) {
                    return builder.ToString();
                }

                builder.Append(value: ((char)value));
            }
        }

        return builder.ToString();
    }
}
