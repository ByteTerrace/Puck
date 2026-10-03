using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every sky kind's parameters, packed into a layer record's payload by <see cref="SdfSky.Add{T}"/>,
/// decode in the kernels to the same bits, field for field, through the decoder <c>puck shaders generate</c> writes from
/// the kind's C# record (<see cref="SdfSkyKindsHlsl"/>), and the generated structure declares the record's fields in its
/// offset order. The decoder is read from the generated text and run over the packed record's lanes, so a decoder that
/// reads a member from another lane (two members out of order) fails by name. Every kind of the table is checked, and
/// every kind fits a layer's payload.
/// </summary>
public sealed partial class SkyLayerTableLawTests {
    [Fact]
    public void EveryKindsPackedRecordDecodesToItsParametersThroughItsGeneratedDecoder() {
        var text = SdfSkyKindsHlsl.Generate();
        var checkedKinds = new List<SdfSkyLayerKind>();

        foreach (var check in Checks) {
            var (kind, mismatches) = check(text);

            Assert.Empty(collection: mismatches);
            checkedKinds.Add(item: kind);
        }

        Assert.Equal(
            actual: checkedKinds.Order(),
            expected: SdfSkyKindsHlsl.Kinds.Select(selector: static kind => kind.Kind).Order()
        );
        Assert.Equal(actual: checkedKinds.Order(), expected: Enum.GetValues<SdfSkyLayerKind>().Order());
    }
    [Fact]
    public void EveryKindsStructureDeclaresItsFieldsInOffsetOrderAndFitsThePayload() {
        var text = SdfSkyKindsHlsl.Generate();

        foreach (var kind in SdfSkyKindsHlsl.Kinds) {
            var declared = Regex.Match(input: text, pattern: $@"struct {kind.Parameters.Name} \{{\n(?<members>(    [a-z0-9]+ \w+;\n)+)\}};");

            Assert.True(condition: declared.Success, userMessage: $"no structure for the {kind.Name} kind");
            Assert.Equal(
                actual: Regex.Matches(input: declared.Groups["members"].Value, pattern: @"(\w+);\n").Select(selector: static match => match.Groups[1].Value),
                expected: kind.Parameters.Members.OrderBy(keySelector: static member => member.Offset).Select(selector: static member => member.Name)
            );
            Assert.InRange(actual: kind.Parameters.SizeBytes, high: ((uint)SdfSkyLayer.PayloadBytes), low: 1u);
        }
    }
    // The red leg: a decoder generated from a structure whose two members trade offsets (out of order against the record
    // the host packs) reads each from the other's lanes, and the check names exactly those two.
    [Fact]
    public void ADecoderWithTwoMembersOutOfOrderFails() {
        var clouds = SdfSkyKindsHlsl.Kinds.Single(predicate: static kind => (kind.Kind == SdfSkyLayerKind.Clouds));
        var swapped = new ShaderInterfaceStructure(
            members: [.. clouds.Parameters.Members.Select(selector: static member => member with {
                Offset = member.Name switch {
                    nameof(SdfSkyClouds.Coverage) => ((uint)Marshal.OffsetOf<SdfSkyClouds>(fieldName: nameof(SdfSkyClouds.Softness))),
                    nameof(SdfSkyClouds.Softness) => ((uint)Marshal.OffsetOf<SdfSkyClouds>(fieldName: nameof(SdfSkyClouds.Coverage))),
                    _ => member.Offset,
                },
            }).OrderBy(keySelector: static member => member.Offset)],
            name: clouds.Parameters.Name,
            sizeBytes: clouds.Parameters.SizeBytes
        );
        var text = SdfSkyKindsHlsl.Generate(kinds: [clouds with { Parameters = swapped }]);

        Assert.Equal(
            actual: Check<SdfSkyClouds>(text: text).Mismatches,
            expected: [nameof(SdfSkyClouds.Coverage), nameof(SdfSkyClouds.Softness)]
        );
    }

    // One check a kind of the table.
    private static readonly Func<string, (SdfSkyLayerKind Kind, string[] Mismatches)>[] Checks = [
        Check<SdfSkyGradient>,
        Check<SdfSkyStars>,
        Check<SdfSkyClouds>,
        Check<SdfSkyAurora>,
        Check<SdfSkyNoise>,
        Check<SdfSkyPattern>,
        Check<SdfSkyPanorama>,
        Check<SdfSkyDisc>,
        Check<SdfSkyView>,
    ];

    // Packs a kind's record with a distinct sentinel in every field, runs the generated decoder over the record's lanes,
    // and names every field whose decoded bits differ from the packed ones.
    private static (SdfSkyLayerKind Kind, string[] Mismatches) Check<T>(string text) where T : unmanaged, ISdfSkyKind {
        var structure = ShaderInterfaceStructure.From<T>();
        var parameters = default(T);
        var bytes = MemoryMarshal.AsBytes(span: MemoryMarshal.CreateSpan(length: 1, reference: ref parameters));
        var sentinel = 1u;

        foreach (var member in structure.Members) {
            for (var component = 0u; (component < member.Type.ComponentCount()); component++) {
                var value = ((member.Type.ScalarKind() == ShaderScalarKind.Float) ? BitConverter.SingleToUInt32Bits(value: (sentinel + 0.25f)) : (0x5000u + sentinel));

                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[((int)(member.Offset + (component * 4u)))..], value: value);
                sentinel++;
            }
        }

        var sky = new SdfSky();
        var index = sky.Add(label: "law", parameters: parameters);
        var record = sky.LayerAt(index: index);
        var lanes = new[] { record.P0, record.P1, record.P2, record.P3, record.P4, record.P5, record.P6, record.P7 };
        var decoder = Regex.Match(input: text, options: RegexOptions.Singleline, pattern: $@"{structure.Name} {LowerFirst(text: structure.Name)}Of\(SdfSkyLayer layer\) \{{(?<body>.*?)return parameters;");
        var mismatches = new List<string>();

        Assert.True(condition: decoder.Success, userMessage: $"no decoder for {structure.Name}");
        foreach (var member in structure.Members) {
            var line = Regex.Match(input: decoder.Groups["body"].Value, pattern: $@"parameters\.{member.Name} = (?:as(?:uint|int)\()?layer\.P(?<vector>\d)\.(?<lanes>[xyzw]+)\)?;");

            if (!line.Success) {
                mismatches.Add(item: member.Name);

                continue;
            }

            var vector = lanes[int.Parse(s: line.Groups["vector"].Value, provider: System.Globalization.CultureInfo.InvariantCulture)];
            var decoded = line.Groups["lanes"].Value.Select(selector: lane => BitConverter.SingleToUInt32Bits(value: lane switch {
                'x' => vector.X,
                'y' => vector.Y,
                'z' => vector.Z,
                _ => vector.W,
            })).ToArray();
            var packed = new uint[member.Type.ComponentCount()];

            for (var component = 0; (component < packed.Length); component++) {
                packed[component] = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[((int)(member.Offset + (((uint)component) * 4u)))..]);
            }

            if (!decoded.SequenceEqual(second: packed)) {
                mismatches.Add(item: member.Name);
            }
        }

        return (T.Kind, [.. mismatches]);
    }
    private static string LowerFirst(string text) => (char.ToLowerInvariant(c: text[0]) + text[1..]);
}
