using System.Buffers.Binary;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Reads the descriptor bindings a SPIR-V module declares, in the neutral <see cref="ShaderInterfaceBinding"/> shape,
/// from the module's own decorations: <c>DescriptorSet</c> and <c>Binding</c> on each variable, <c>Offset</c> on each
/// constant block member, and the <c>OpName</c> and <c>OpMemberName</c> debug names DXC emits. A variable in the
/// <c>PushConstant</c> storage class carries no set or binding and is reported as a pushed
/// <see cref="GpuBindingKind.ConstantBuffer"/> block (<see cref="ShaderInterfaceBinding.Pushed"/>) at set 0, binding 0. Pure C#, no GPU and no native tool; it
/// follows the Khronos SPIR-V specification's instruction encoding and nothing else.
/// </summary>
public static class SpirvInterfaceReader {
    private const uint DecorationBinding = 33;
    private const uint DecorationBlock = 2;
    private const uint DecorationBufferBlock = 3;
    private const uint DecorationDescriptorSet = 34;
    private const uint DecorationNonWritable = 24;
    private const uint DecorationOffset = 35;
    private const uint DimBuffer = 5;
    private const int HeaderWords = 5;
    private const uint MagicNumber = 0x07230203;
    private const uint OpConstant = 43;
    private const uint OpDecorate = 71;
    private const uint OpMemberDecorate = 72;
    private const uint OpMemberName = 6;
    private const uint OpName = 5;
    private const uint OpTypeArray = 28;
    private const uint OpTypeFloat = 22;
    private const uint OpTypeImage = 25;
    private const uint OpTypeInt = 21;
    private const uint OpTypePointer = 32;
    private const uint OpTypeRuntimeArray = 29;
    private const uint OpTypeSampledImage = 27;
    private const uint OpTypeSampler = 26;
    private const uint OpTypeStruct = 30;
    private const uint OpTypeVector = 23;
    private const uint OpVariable = 59;
    private const uint StorageClassPushConstant = 9;
    private const uint StorageClassStorageBuffer = 12;
    private const uint StorageClassUniform = 2;

    private sealed class Module {
        public Dictionary<uint, uint> Bindings { get; } = [];
        public HashSet<uint> Blocks { get; } = [];
        public HashSet<uint> BufferBlocks { get; } = [];
        public Dictionary<uint, uint> Constants { get; } = [];
        public Dictionary<(uint Struct, uint Member), string> MemberNames { get; } = [];
        public HashSet<(uint Struct, uint Member)> MemberNonWritable { get; } = [];
        public Dictionary<(uint Struct, uint Member), uint> MemberOffsets { get; } = [];
        public Dictionary<uint, string> Names { get; } = [];
        public Dictionary<uint, uint> Sets { get; } = [];
        public Dictionary<uint, (uint Kind, uint[] Operands)> Types { get; } = [];
        public List<(uint Id, uint PointerType, uint StorageClass)> Variables { get; } = [];
    }

    /// <summary>Reads every variable that carries a descriptor set, and the push-constant block, ordered by set and then
    /// binding.</summary>
    /// <param name="module">The SPIR-V module's bytes, little-endian words.</param>
    /// <returns>The bindings.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a SPIR-V module, an instruction overruns the module, or a
    /// decorated variable's type is one the neutral shape has no kind for.</exception>
    public static IReadOnlyList<ShaderInterfaceBinding> Read(ReadOnlySpan<byte> module) {
        if (
            ((module.Length % 4) != 0) ||
            (module.Length < (HeaderWords * 4)) ||
            (BinaryPrimitives.ReadUInt32LittleEndian(source: module) != MagicNumber)
        ) {
            throw new InvalidDataException(message: "The bytes are not a little-endian SPIR-V module.");
        }

        var words = new uint[(module.Length / 4)];

        for (var index = 0; (index < words.Length); index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: module.Slice(
                length: 4,
                start: (index * 4)
            ));
        }

        var parsed = Parse(words: words);
        var bindings = new List<ShaderInterfaceBinding>();

        foreach (var (id, pointerType, storageClass) in parsed.Variables) {
            var pushed = (storageClass == StorageClassPushConstant);

            if (
                !parsed.Sets.TryGetValue(
                    key: id,
                    value: out var set
                ) &&
                !pushed
            ) {
                continue;
            }

            // An OpTypePointer's operands after its result id are the storage class and the pointee type.
            var pointee = Unwrap(
                module: parsed,
                typeId: Operand(
                    index: 1,
                    module: parsed,
                    typeId: pointerType
                )
            );
            var name = (parsed.Names.GetValueOrDefault(key: id) ?? $"%{id}");
            var kind = (pushed
                ? GpuBindingKind.ConstantBuffer
                : Kind(
                    module: parsed,
                    name: name,
                    storageClass: storageClass,
                    typeId: pointee
                ));

            bindings.Add(item: new ShaderInterfaceBinding(
                Binding: parsed.Bindings.GetValueOrDefault(key: id),
                Kind: kind,
                Members: ((kind == GpuBindingKind.ConstantBuffer)
                    ? BlockMembers(
                        module: parsed,
                        structId: pointee
                    )
                    : []),
                Name: name,
                Pushed: pushed,
                Set: set
            ));
        }

        bindings.Sort(comparison: static (a, b) => ((a.Set != b.Set)
            ? a.Set.CompareTo(value: b.Set)
            : a.Binding.CompareTo(value: b.Binding)));

        return bindings.AsReadOnly();
    }

    private static IReadOnlyList<ShaderInterfaceBlockMember> BlockMembers(Module module, uint structId) {
        var members = module.Types[structId].Operands;
        var result = new List<ShaderInterfaceBlockMember>(capacity: members.Length);

        for (var index = 0u; (index < members.Length); index++) {
            var memberType = members[index];
            var length = 0u;

            if (module.Types[memberType].Kind == OpTypeArray) {
                var array = module.Types[memberType].Operands;

                length = module.Constants[array[1]];
                memberType = array[0];
            }

            result.Add(item: new ShaderInterfaceBlockMember(
                Length: length,
                Name: (module.MemberNames.GetValueOrDefault(key: (structId, index)) ?? $"member{index}"),
                Offset: module.MemberOffsets.GetValueOrDefault(key: (structId, index)),
                Type: ValueType(
                    module: module,
                    typeId: memberType
                )
            ));
        }

        result.Sort(comparison: static (a, b) => a.Offset.CompareTo(value: b.Offset));

        return result.AsReadOnly();
    }
    private static GpuBindingKind Kind(Module module, uint typeId, uint storageClass, string name) {
        var (kind, operands) = module.Types[typeId];

        switch (kind) {
            case OpTypeStruct when (
                (storageClass == StorageClassStorageBuffer) ||
                module.BufferBlocks.Contains(item: typeId)
            ):
                var readOnly = true;

                for (var member = 0u; (member < operands.Length); member++) {
                    readOnly &= module.MemberNonWritable.Contains(item: (typeId, member));
                }

                return (readOnly
                    ? GpuBindingKind.ReadOnlyBuffer
                    : GpuBindingKind.ReadWriteBuffer);
            case OpTypeStruct when (
                (storageClass == StorageClassUniform) &&
                module.Blocks.Contains(item: typeId)
            ):
                return GpuBindingKind.ConstantBuffer;
            case OpTypeImage:
                // Operands: sampled type, Dim, Depth, Arrayed, MS, Sampled (1 read through a sampler, 2 read and
                // written by coordinate), Image Format.
                var buffer = (operands[1] == DimBuffer);

                return ((operands[5] == 2)
                    ? (buffer
                        ? GpuBindingKind.ReadWriteBuffer
                        : GpuBindingKind.StorageImage)
                    : (buffer
                        ? GpuBindingKind.ReadOnlyBuffer
                        : GpuBindingKind.SampledImage));
            case OpTypeSampledImage:
                return GpuBindingKind.SampledImage;
            case OpTypeSampler:
                return GpuBindingKind.Sampler;
            default:
                throw new InvalidDataException(message: $"SPIR-V variable '{name}' carries a descriptor set but its type (opcode {kind}) is no binding kind.");
        }
    }
    private static uint Operand(Module module, uint typeId, int index) =>
        (module.Types.TryGetValue(
            key: typeId,
            value: out var type
        )
            ? type.Operands[index]
            : throw new InvalidDataException(message: $"SPIR-V type %{typeId} is referenced but never declared."));
    private static Module Parse(uint[] words) {
        var module = new Module();

        for (var cursor = HeaderWords; (cursor < words.Length);) {
            var wordCount = ((int)(words[cursor] >> 16));
            var opcode = words[cursor] & 0xFFFF;

            if (
                (wordCount == 0) ||
                ((cursor + wordCount) > words.Length)
            ) {
                throw new InvalidDataException(message: $"The SPIR-V instruction at word {cursor} overruns the module.");
            }

            var operands = words.AsSpan(
                length: (wordCount - 1),
                start: (cursor + 1)
            );

            switch (opcode) {
                case OpName:
                    module.Names[operands[0]] = ReadString(words: operands[1..]);
                    break;
                case OpMemberName:
                    module.MemberNames[(operands[0], operands[1])] = ReadString(words: operands[2..]);
                    break;
                case OpTypeInt or OpTypeFloat or OpTypeVector or OpTypeImage or OpTypeSampler or OpTypeSampledImage or OpTypeArray or OpTypeRuntimeArray or OpTypeStruct or OpTypePointer:
                    module.Types[operands[0]] = (opcode, operands[1..].ToArray());
                    break;
                case OpConstant:
                    module.Constants[operands[1]] = operands[2];
                    break;
                case OpVariable:
                    module.Variables.Add(item: (operands[1], operands[0], operands[2]));
                    break;
                case OpDecorate:
                    Decorate(
                        decoration: operands[1],
                        module: module,
                        operands: operands,
                        target: operands[0]
                    );
                    break;
                case OpMemberDecorate:
                    if (operands[2] == DecorationOffset) {
                        module.MemberOffsets[(operands[0], operands[1])] = operands[3];
                    } else if (operands[2] == DecorationNonWritable) {
                        _ = module.MemberNonWritable.Add(item: (operands[0], operands[1]));
                    }
                    break;
            }

            cursor += wordCount;
        }

        return module;
    }
    private static void Decorate(Module module, uint target, uint decoration, ReadOnlySpan<uint> operands) {
        switch (decoration) {
            case DecorationBlock:
                _ = module.Blocks.Add(item: target);
                break;
            case DecorationBufferBlock:
                _ = module.BufferBlocks.Add(item: target);
                break;
            case DecorationBinding:
                module.Bindings[target] = operands[2];
                break;
            case DecorationDescriptorSet:
                module.Sets[target] = operands[2];
                break;
        }
    }
    private static string ReadString(ReadOnlySpan<uint> words) {
        var text = new StringBuilder();

        foreach (var word in words) {
            for (var shift = 0; (shift < 32); shift += 8) {
                var value = ((byte)(word >> shift));

                if (value == 0) {
                    return text.ToString();
                }

                _ = text.Append(value: ((char)value));
            }
        }

        return text.ToString();
    }
    // A binding array's variable points at an array of its element; the element type decides the kind.
    private static uint Unwrap(Module module, uint typeId) {
        while (
            module.Types.TryGetValue(
                key: typeId,
                value: out var type
            ) &&
            (type.Kind is OpTypeArray or OpTypeRuntimeArray)
        ) {
            typeId = type.Operands[0];
        }

        return typeId;
    }
    private static ShaderValueType ValueType(Module module, uint typeId) {
        var (kind, operands) = module.Types[typeId];
        var count = 1u;

        if (kind == OpTypeVector) {
            count = operands[1];
            (kind, operands) = module.Types[operands[0]];
        }

        // OpTypeInt operands: width, signedness; OpTypeFloat: width.
        var scalar = kind switch {
            OpTypeFloat when (operands[0] == 32) => ShaderScalarKind.Float,
            OpTypeInt when ((operands[0] == 32) && (operands[1] == 0)) => ShaderScalarKind.Uint,
            OpTypeInt when (operands[0] == 32) => ShaderScalarKind.Int,
            _ => throw new InvalidDataException(message: $"SPIR-V type %{typeId} is not a 32-bit scalar or vector."),
        };

        return ShaderValueTypes.FromComponents(
            count: count,
            kind: scalar
        );
    }
}
