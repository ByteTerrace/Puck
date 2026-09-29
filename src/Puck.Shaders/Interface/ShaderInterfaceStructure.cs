using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;

namespace Puck.Shaders;

/// <summary>A flat unmanaged record used as a structured-buffer element. Public fields are the contract;
/// generated padding makes its native layout identical in C#, SPIR-V, and DXIL.</summary>
public sealed record ShaderInterfaceStructure {
    /// <summary>Creates a structure description read from an interface document.</summary>
    /// <param name="name">The generated HLSL type name.</param>
    /// <param name="sizeBytes">The native element stride, including trailing padding.</param>
    /// <param name="members">The fields in ascending offset order, excluding generated padding.</param>
    /// <exception cref="InvalidDataException">The description cannot have one layout on both backends.</exception>
    [JsonConstructor]
    public ShaderInterfaceStructure(string name, uint sizeBytes, IReadOnlyList<ShaderInterfaceBlockMember> members) {
        if (!Identifier(value: name) || (members is null) || (members.Count == 0)) {
            throw new InvalidDataException(message: $"Shader structure '{name}' needs an identifier and at least one field.");
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var cursor = 0u;
        var alignment = 4u;

        foreach (var member in members) {
            if (!Identifier(value: member.Name) || !names.Add(item: member.Name) ||
                !Enum.IsDefined(value: member.Type) || (member.Length != 0)) {
                throw new InvalidDataException(message: $"Shader structure '{name}' field '{member.Name}' is not a unique scalar or vector field.");
            }

            var fieldAlignment = member.Type.ComponentCount() switch { 1 => 4u, 2 => 8u, _ => 16u };

            alignment = Math.Max(val1: alignment, val2: fieldAlignment);
            if ((member.Offset < cursor) || ((member.Offset % fieldAlignment) != 0)) {
                throw new InvalidDataException(message: $"Shader structure '{name}' field '{member.Name}' at byte {member.Offset} overlaps a field or needs {fieldAlignment}-byte alignment.");
            }

            cursor = checked((member.Offset + member.Type.SizeBytes()));
        }
        if ((sizeBytes < cursor) || ((sizeBytes % alignment) != 0)) {
            throw new InvalidDataException(message: $"Shader structure '{name}' stride {sizeBytes} must contain its fields and be a multiple of {alignment} bytes.");
        }

        Name = name;
        SizeBytes = sizeBytes;
        Members = new ReadOnlyCollection<ShaderInterfaceBlockMember>(list: members.ToArray());
    }

    /// <summary>Gets the generated HLSL type name.</summary>
    public string Name { get; }
    /// <summary>Gets the element stride in bytes.</summary>
    public uint SizeBytes { get; }
    /// <summary>Gets the native fields in ascending offset order.</summary>
    public IReadOnlyList<ShaderInterfaceBlockMember> Members { get; }
    /// <summary>Gets the identity carried by a resource's reflected name, so a same-sized field reorder refuses stale bytecode.</summary>
    [JsonIgnore]
    public ContentPin Hash => ContentPin.Compute(content: JsonSerializer.SerializeToUtf8Bytes(
        value: this,
        jsonTypeInfo: ShaderInterfaceJsonContext.Default.ShaderInterfaceStructure
    ));

    /// <summary>Describes a native record once, when its interface is created. The record has only public instance fields
    /// of <see cref="float"/>, <see cref="int"/>, <see cref="uint"/>, or two-, three-, or four-component float vectors.
    /// Its explicit or sequential layout must align fields for both graphics backends.</summary>
    /// <typeparam name="T">The unmanaged record whose native field offsets are the source of truth.</typeparam>
    /// <returns>The immutable description used by the existing interface generator.</returns>
    /// <exception cref="InvalidDataException">A field or the record's native layout is unsupported.</exception>
    public static ShaderInterfaceStructure From<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] T>() where T : unmanaged {
        var type = typeof(T);
        var members = new List<ShaderInterfaceBlockMember>();

        foreach (var field in type.GetFields(bindingAttr: BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
            if (!field.IsPublic) {
                throw new InvalidDataException(message: $"Shader structure '{type.Name}' field '{field.Name}' must be public; hidden storage is not shader padding.");
            }
            var valueType = ((field.FieldType == typeof(float)) ? ShaderValueType.Float :
                ((field.FieldType == typeof(int)) ? ShaderValueType.Int :
                ((field.FieldType == typeof(uint)) ? ShaderValueType.Uint :
                ((field.FieldType == typeof(Vector2)) ? ShaderValueType.Float2 :
                ((field.FieldType == typeof(Vector3)) ? ShaderValueType.Float3 :
                ((field.FieldType == typeof(Vector4)) ? ShaderValueType.Float4 :
                throw new InvalidDataException(message: $"Shader structure '{type.Name}' field '{field.Name}' has unsupported native type '{field.FieldType.Name}'.")))))));

            members.Add(item: new ShaderInterfaceBlockMember(
                Name: field.Name,
                Offset: checked((uint)Marshal.OffsetOf<T>(fieldName: field.Name).ToInt64()),
                Type: valueType,
                Length: 0
            ));
        }
        members.Sort(comparison: static (left, right) => left.Offset.CompareTo(value: right.Offset));
        return new ShaderInterfaceStructure(name: type.Name, sizeBytes: checked((uint)Unsafe.SizeOf<T>()), members: members);
    }
    /// <inheritdoc/>
    public bool Equals(ShaderInterfaceStructure? other) => ((other is not null) &&
        string.Equals(a: Name, b: other.Name, comparisonType: StringComparison.Ordinal) &&
        (SizeBytes == other.SizeBytes) && Members.SequenceEqual(second: other.Members));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(value1: Name, value2: SizeBytes, value3: Members.Count);

    internal static bool Identifier(string? value) => (!string.IsNullOrEmpty(value: value) &&
        char.IsAsciiLetter(c: value[0]) && value.All(predicate: static c => char.IsAsciiLetterOrDigit(c: c)));
}
