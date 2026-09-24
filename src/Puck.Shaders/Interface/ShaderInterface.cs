using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.Assets;

namespace Puck.Shaders;

/// <summary>
/// A pass interface declared as data: the named values, arrays, images and samplers one pass reads and writes, each in
/// the frequency group its data changes with. The engine assigns every descriptor set, binding, register and offset
/// from it (<see cref="ShaderInterfaceLayout"/>), <see cref="ShaderInterfaceHlsl"/> generates the declarations a pass
/// includes, and the interface's <see cref="Hash"/> versions both.
/// <para>Member order is significant: a group's constant block and its bindings follow declaration order, so reordering
/// members moves offsets and bindings and changes the hash.</para>
/// <para>A pass that binds no descriptor set for its frame group receives the frame group's block as push constants
/// instead (<see cref="PushConstants"/>): Vulkan push constants, and Direct3D 12 root constants at register <c>b0</c>,
/// space 0. The block's offsets are the same either way.</para>
/// </summary>
public sealed partial class ShaderInterface {
    private ContentPin m_hash;

    /// <summary>Initializes a new instance of the <see cref="ShaderInterface"/> class.</summary>
    /// <param name="name">The interface's name: lowercase ASCII words joined by hyphens, such as <c>film-grain</c>.</param>
    /// <param name="members">The members in declaration order; at least one.</param>
    /// <param name="pushConstants">The group whose constant block is delivered as push constants rather than bound as a
    /// constant buffer, or <see langword="null"/> when every block is a constant buffer. Only the frame group can be
    /// pushed, and a pushed group holds values and arrays only.</param>
    /// <exception cref="InvalidDataException">The name or a member is malformed, a name repeats or collides with a
    /// generated declaration, a member carries a field its kind does not take or lacks one it needs, or the pushed group
    /// is not the frame group, holds an image or sampler, or holds no value.</exception>
    [JsonConstructor]
    public ShaderInterface(string name, IReadOnlyList<ShaderInterfaceMember> members, ShaderInterfaceGroup? pushConstants = null) {
        if (
            (name is null) ||
            !InterfaceNamePattern().IsMatch(input: name)
        ) {
            throw new InvalidDataException(message: $"A shader interface name must be lowercase ASCII words joined by hyphens; '{name}' is not.");
        }
        if (
            (members is null) ||
            (members.Count == 0)
        ) {
            throw new InvalidDataException(message: $"Shader interface '{name}' declares no members.");
        }

        var identifiers = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var member in members) {
            Validate(
                identifiers: identifiers,
                interfaceName: name,
                member: member
            );
        }
        foreach (var group in members.Where(predicate: static member => member.IsBlockMember).Select(selector: static member => member.Group).Distinct()) {
            foreach (var generated in ((ReadOnlySpan<string>)[BlockVariableName(group: group), BlockTypeName(
                group: group,
                interfaceName: name
            )])) {
                if (!identifiers.Add(item: generated)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' declares '{generated}', which its generated {group} block declares.");
                }
            }
        }

        if (pushConstants is { } pushed) {
            if (pushed != ShaderInterfaceGroup.Frame) {
                throw new InvalidDataException(message: $"Shader interface '{name}' pushes the {pushed} group; only the frame group's block can be pushed.");
            }
            if (members.Any(predicate: member => ((member.Group == pushed) && !member.IsBlockMember))) {
                throw new InvalidDataException(message: $"Shader interface '{name}' pushes the {pushed} group, which then holds values and arrays only.");
            }
            if (!members.Any(predicate: member => ((member.Group == pushed) && member.IsBlockMember))) {
                throw new InvalidDataException(message: $"Shader interface '{name}' pushes the {pushed} group, which holds no value.");
            }
        }

        Name = name;
        Members = new ReadOnlyCollection<ShaderInterfaceMember>(list: members.ToArray());
        PushConstants = pushConstants;
    }

    /// <summary>Gets the interface's name.</summary>
    public string Name { get; }
    /// <summary>Gets the members in declaration order.</summary>
    public IReadOnlyList<ShaderInterfaceMember> Members { get; }
    /// <summary>Gets the group whose constant block is delivered as push constants, or <see langword="null"/> when every
    /// block is bound as a constant buffer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShaderInterfaceGroup? PushConstants { get; }
    /// <summary>Gets the content pin of the interface's canonical JSON (<see cref="ToJson"/>), which versions its
    /// layout and its generated declarations.</summary>
    [JsonIgnore]
    public ContentPin Hash {
        get {
            if (m_hash == default) {
                m_hash = ContentPin.Compute(content: JsonSerializer.SerializeToUtf8Bytes(
                    jsonTypeInfo: ShaderInterfaceJsonContext.Default.ShaderInterface,
                    value: this
                ));
            }

            return m_hash;
        }
    }

    [GeneratedRegex(pattern: "^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex InterfaceNamePattern();
    [GeneratedRegex(pattern: "^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex MemberNamePattern();
    private static void Validate(ShaderInterfaceMember member, string interfaceName, HashSet<string> identifiers) {
        if (member is null) {
            throw new InvalidDataException(message: $"Shader interface '{interfaceName}' declares a null member.");
        }

        var where = $"Shader interface '{interfaceName}' member '{member.Name}'";

        if (
            (member.Name is null) ||
            !MemberNamePattern().IsMatch(input: member.Name)
        ) {
            throw new InvalidDataException(message: $"{where}: a member name is an ASCII letter followed by ASCII letters and digits.");
        }
        if (!Enum.IsDefined(value: member.Group)) {
            throw new InvalidDataException(message: $"{where}: group {((int)member.Group)} is not a frequency group.");
        }
        if (!identifiers.Add(item: member.Name)) {
            throw new InvalidDataException(message: $"{where}: the name is declared twice.");
        }
        if (
            (member.Kind == ShaderInterfaceMemberKind.Array) &&
            !identifiers.Add(item: AccessorName(member: member))
        ) {
            throw new InvalidDataException(message: $"{where}: its accessor '{AccessorName(member: member)}' collides with another name.");
        }

        var needsType = (member.Kind != ShaderInterfaceMemberKind.Sampler);

        if (!Enum.IsDefined(value: member.Kind)) {
            throw new InvalidDataException(message: $"{where}: kind {((int)member.Kind)} is not a member kind.");
        }
        if (needsType != member.Type.HasValue) {
            throw new InvalidDataException(message: (needsType
                ? $"{where}: a {member.Kind} names its type."
                : $"{where}: a {member.Kind} takes no type."));
        }
        if (
            member.Type.HasValue &&
            !Enum.IsDefined(value: member.Type.Value)
        ) {
            throw new InvalidDataException(message: $"{where}: type {((int)member.Type.Value)} is not a value type.");
        }
        if ((member.Kind == ShaderInterfaceMemberKind.Array) != member.Length.HasValue) {
            throw new InvalidDataException(message: ((member.Kind == ShaderInterfaceMemberKind.Array)
                ? $"{where}: an array names its length."
                : $"{where}: only an array takes a length."));
        }
        if (member.Length == 0) {
            throw new InvalidDataException(message: $"{where}: an array holds at least one element.");
        }
        if ((member.Kind == ShaderInterfaceMemberKind.StorageImage) != member.Format.HasValue) {
            throw new InvalidDataException(message: ((member.Kind == ShaderInterfaceMemberKind.StorageImage)
                ? $"{where}: a storage image names its format."
                : $"{where}: only a storage image takes a format."));
        }
        if (
            member.Format.HasValue &&
            (StorageFormatSpelling(format: member.Format.Value) is null)
        ) {
            throw new InvalidDataException(message: $"{where}: a storage image cannot be {member.Format.Value}; it is R8G8B8A8Unorm, R16G16B16A16Float or R32G32B32A32Float.");
        }
    }

    /// <summary>Returns the HLSL name of the accessor generated for an array member.</summary>
    /// <param name="member">The array member.</param>
    /// <returns>The member's name followed by <c>At</c>.</returns>
    public static string AccessorName(ShaderInterfaceMember member) {
        ArgumentNullException.ThrowIfNull(argument: member);

        return (member.Name + "At");
    }
    /// <summary>Returns the HLSL name of the constant buffer variable generated for a group's block.</summary>
    /// <param name="group">The frequency group.</param>
    /// <returns>The group's name in lower camel case followed by <c>Group</c>, such as <c>frameGroup</c>.</returns>
    public static string BlockVariableName(ShaderInterfaceGroup group) =>
        group switch {
            ShaderInterfaceGroup.Frame => "frameGroup",
            ShaderInterfaceGroup.World => "worldGroup",
            ShaderInterfaceGroup.Instance => "instanceGroup",
            ShaderInterfaceGroup.Pass => "passGroup",
            _ => throw new ArgumentOutOfRangeException(
                actualValue: group,
                message: "The value is not a frequency group.",
                paramName: nameof(group)
            ),
        };
    /// <summary>Returns the HLSL name of the struct generated for a group's block.</summary>
    /// <param name="interfaceName">The interface's hyphenated name.</param>
    /// <param name="group">The frequency group.</param>
    /// <returns>The interface name in upper camel case followed by the group's name, such as
    /// <c>FilmGrainFrame</c>.</returns>
    public static string BlockTypeName(string interfaceName, ShaderInterfaceGroup group) {
        ArgumentNullException.ThrowIfNull(argument: interfaceName);

        return string.Concat(
            str0: string.Concat(values: interfaceName.Split(separator: '-').Select(selector: static word => (char.ToUpperInvariant(c: word[0]) + word[1..]))),
            str1: group.ToString()
        );
    }
    /// <summary>Returns the image-format spelling a storage image of <paramref name="format"/> declares in SPIR-V, or
    /// <see langword="null"/> when a storage image cannot have that format.</summary>
    /// <param name="format">The texel format.</param>
    /// <returns>The <c>vk::image_format</c> spelling, or <see langword="null"/>.</returns>
    public static string? StorageFormatSpelling(GpuPixelFormat format) =>
        format switch {
            GpuPixelFormat.R8G8B8A8Unorm => "rgba8",
            GpuPixelFormat.R16G16B16A16Float => "rgba16f",
            GpuPixelFormat.R32G32B32A32Float => "rgba32f",
            _ => null,
        };
    /// <summary>Reads an interface from its JSON document, refusing an unknown property, a missing required one, and an
    /// enum written as a number or an undeclared name.</summary>
    /// <param name="json">The document text.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="InvalidDataException">The document is not valid JSON for the model, or the interface it
    /// declares is malformed.</exception>
    public static ShaderInterface Parse(string json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        try {
            return (JsonSerializer.Deserialize(
                json: json,
                jsonTypeInfo: ShaderInterfaceJsonContext.Default.ShaderInterface
            ) ?? throw new InvalidDataException(message: "A shader interface document is null."));
        } catch (JsonException exception) {
            throw new InvalidDataException(
                innerException: exception,
                message: $"The shader interface document is malformed: {exception.Message}"
            );
        }
    }
    /// <summary>Computes where every member lands: descriptor sets, bindings, registers and block offsets.</summary>
    /// <returns>The layout.</returns>
    public ShaderInterfaceLayout Layout() =>
        new(shaderInterface: this);
    /// <summary>Writes the interface's canonical JSON: camel-case properties in declaration order, enums by their
    /// declared names, absent optional fields omitted, no whitespace.</summary>
    /// <returns>The document text.</returns>
    public string ToJson() =>
        JsonSerializer.Serialize(
            jsonTypeInfo: ShaderInterfaceJsonContext.Default.ShaderInterface,
            value: this
        );
}
/// <summary>Source-generated, strict JSON metadata for <see cref="ShaderInterface"/>.</summary>
[JsonSerializable(typeof(ShaderInterface))]
[JsonSourceGenerationOptions(
    AllowDuplicateProperties = false,
    Converters = [typeof(StrictEnumConverter<GpuPixelFormat>)],
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
public sealed partial class ShaderInterfaceJsonContext : JsonSerializerContext {
}
