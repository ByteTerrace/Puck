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
/// <para>A pass binds every group it uses as a descriptor set, and can push one 4-byte index
/// (<see cref="PushesIndex"/>), which it reads as <c>pushedIndex.index</c>: Vulkan push constants at offset 0, and
/// Direct3D 12 root constants at register <c>b0</c> in space
/// <see cref="GpuPipelineLayoutDescription.PushIndexSpace"/>.</para>
/// <para>An interface can carry a stamp (<see cref="Stamp"/>): a token naming what its owner built it against, which the
/// generated pass block's variable carries in its name (<see cref="BlockVariableNameOf"/>), so every module compiled
/// from the generated declarations reflects it, and <see cref="ShaderInterfaceLayout.Mismatch"/> refuses a module whose
/// pass block carries another stamp or none.</para>
/// </summary>
public sealed partial class ShaderInterface {
    /// <summary>The HLSL name of the constant buffer variable generated for the pushed index.</summary>
    public const string PushedIndexVariableName = "pushedIndex";
    /// <summary>The name of the pushed index's one member, which a pass reads as <c>pushedIndex.index</c>.</summary>
    public const string PushedIndexMemberName = "index";

    private ContentPin m_hash;

    /// <summary>Initializes a new instance of the <see cref="ShaderInterface"/> class.</summary>
    /// <param name="name">The interface's name: lowercase ASCII words joined by hyphens, such as <c>film-grain</c>.</param>
    /// <param name="members">The members in declaration order; at least one.</param>
    /// <param name="pushesIndex">Whether the pass's pipeline pushes one 4-byte index, which the generated include
    /// declares as <see cref="PushedIndexVariableName"/>.</param>
    /// <param name="stamp">The interface's stamp (<see cref="Stamp"/>), or <see langword="null"/> for none: an ASCII
    /// capital letter followed by ASCII letters and digits.</param>
    /// <exception cref="InvalidDataException">The name, the stamp or a member is malformed, a name repeats or collides
    /// with a generated declaration, a member carries a field its kind does not take or lacks one it needs, a buffer's
    /// element is a three-component vector, or a stamped interface has no pass block to carry its stamp.</exception>
    [JsonConstructor]
    public ShaderInterface(string name, IReadOnlyList<ShaderInterfaceMember> members, bool pushesIndex = false, string? stamp = null) {
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

        if (stamp is not null) {
            if (!StampPattern().IsMatch(input: stamp)) {
                throw new InvalidDataException(message: $"Shader interface '{name}' stamp '{stamp}' is not an ASCII capital letter followed by ASCII letters and digits.");
            }
            if (!members.Any(predicate: static member => (
                (member is not null) &&
                member.IsBlockMember &&
                (member.Group == ShaderInterfaceGroup.Pass)
            ))) {
                throw new InvalidDataException(message: $"Shader interface '{name}' is stamped '{stamp}' but declares no pass block to carry the stamp.");
            }
        }

        Stamp = stamp;

        var identifiers = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var member in members) {
            Validate(
                identifiers: identifiers,
                interfaceName: name,
                member: member
            );
        }
        var structures = new Dictionary<string, ShaderInterfaceStructure>(comparer: StringComparer.Ordinal);

        foreach (var member in members) {
            if (member.Structure is not { } structure) {
                continue;
            }
            if (structures.TryGetValue(key: structure.Name, value: out var previous)) {
                if (!previous.Equals(other: structure)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' declares different layouts for structure '{structure.Name}'.");
                }
            } else {
                if (!identifiers.Add(item: structure.Name)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' structure '{structure.Name}' collides with another declaration.");
                }
                structures.Add(key: structure.Name, value: structure);
            }
            if (!identifiers.Add(item: member.ResourceName)) {
                throw new InvalidDataException(message: $"Shader interface '{name}' resource '{member.ResourceName}' collides with another declaration.");
            }
        }
        foreach (var group in members.Where(predicate: static member => member.IsBlockMember).Select(selector: static member => member.Group).Distinct()) {
            foreach (var generated in ((ReadOnlySpan<string>)[BlockVariableNameOf(group: group), BlockTypeName(
                group: group,
                interfaceName: name
            )])) {
                if (!identifiers.Add(item: generated)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' declares '{generated}', which its generated {group} block declares.");
                }
            }
        }

        if (pushesIndex) {
            foreach (var generated in ((ReadOnlySpan<string>)[PushedIndexVariableName, PushedIndexTypeName(interfaceName: name)])) {
                if (!identifiers.Add(item: generated)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' declares '{generated}', which its generated pushed index declares.");
                }
            }
        }

        var aliases = members.Where(predicate: static member => (member.Structure is not null))
            .Select(selector: static member => member.Name).ToHashSet(comparer: StringComparer.Ordinal);

        if (stamp is not null) {
            aliases.Add(item: BlockVariableName(group: ShaderInterfaceGroup.Pass));
        }
        foreach (var structure in structures.Values) {
            foreach (var field in structure.Members) {
                if (aliases.Contains(item: field.Name)) {
                    throw new InvalidDataException(message: $"Shader interface '{name}' structure '{structure.Name}' field '{field.Name}' collides with a generated alias; shader preprocessing would rename the field access.");
                }
            }
        }

        Name = name;
        Members = new ReadOnlyCollection<ShaderInterfaceMember>(list: members.ToArray());
        PushesIndex = pushesIndex;
    }

    /// <summary>Gets the interface's name.</summary>
    public string Name { get; }
    /// <summary>Gets the members in declaration order.</summary>
    public IReadOnlyList<ShaderInterfaceMember> Members { get; }
    /// <summary>Gets a value indicating whether the pass's pipeline pushes one 4-byte index, read as
    /// <c>pushedIndex.index</c>. The canonical JSON writes it only when it is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PushesIndex { get; }
    /// <summary>Gets the token naming what the interface's owner built it against, or <see langword="null"/> for none. The
    /// generated pass block's variable carries it (<see cref="BlockVariableNameOf"/>), and the canonical JSON writes it only
    /// when it is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stamp { get; }
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
    [GeneratedRegex(pattern: "^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex StampPattern();
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

        if (!Enum.IsDefined(value: member.Kind)) {
            throw new InvalidDataException(message: $"{where}: kind {((int)member.Kind)} is not a member kind.");
        }

        // A buffer's element type is optional: with one it is a structured buffer, without one a raw buffer.
        var isBuffer = (member.Kind is ShaderInterfaceMemberKind.ReadOnlyBuffer or ShaderInterfaceMemberKind.ReadWriteBuffer);

        if ((member.Structure is not null) && (!isBuffer || member.Type.HasValue)) {
            throw new InvalidDataException(message: $"{where}: a structure belongs to a buffer and replaces its primitive element type.");
        }

        if (
            !isBuffer &&
            ((member.Kind != ShaderInterfaceMemberKind.Sampler) != member.Type.HasValue)
        ) {
            throw new InvalidDataException(message: (member.Type.HasValue
                ? $"{where}: a {member.Kind} takes no type."
                : $"{where}: a {member.Kind} names its type."));
        }
        if (
            member.Type.HasValue &&
            !Enum.IsDefined(value: member.Type.Value)
        ) {
            throw new InvalidDataException(message: $"{where}: type {((int)member.Type.Value)} is not a value type.");
        }
        // DXIL's structured stride for a three-component element is its 12 bytes, where SPIR-V's buffer layout may pad
        // it to 16, so the two backends would disagree on where element i starts.
        if (
            isBuffer &&
            (member.Type?.ComponentCount() == 3)
        ) {
            throw new InvalidDataException(message: $"{where}: a buffer's element cannot be the three-component {member.Type.Value.Spelling()}, whose stride differs between Direct3D 12 (12 bytes) and Vulkan (16 when padded); use a scalar, a two-component or a four-component element.");
        }
        if (
            (member.Kind == ShaderInterfaceMemberKind.Array) &&
            !member.Length.HasValue
        ) {
            throw new InvalidDataException(message: $"{where}: an array names its length.");
        }
        if (
            member.Length.HasValue &&
            (member.Kind is not (ShaderInterfaceMemberKind.Array or ShaderInterfaceMemberKind.Value or ShaderInterfaceMemberKind.SampledImage or ShaderInterfaceMemberKind.Sampler))
        ) {
            throw new InvalidDataException(message: $"{where}: only an array, a block value, a sampled image or a sampler takes a length.");
        }
        // A block array's element is a whole 16-byte row on both backends only when it is a four-component vector: a
        // shorter element pads to a row in a constant block, which a host writing element after element would miss.
        if (
            (member.Kind == ShaderInterfaceMemberKind.Value) &&
            member.Length.HasValue &&
            (member.Type?.ComponentCount() != 4)
        ) {
            throw new InvalidDataException(message: $"{where}: a block array's element is a four-component vector, not {member.Type?.Spelling()}.");
        }
        if (member.Length == 0) {
            throw new InvalidDataException(message: $"{where}: an array holds at least one element.");
        }
        if (
            (member.Kind == ShaderInterfaceMemberKind.Array) &&
            (member.Type?.ComponentCount() != 1)
        ) {
            throw new InvalidDataException(message: $"{where}: an array's element is a scalar, not {member.Type?.Spelling()}.");
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
    /// <summary>Returns the HLSL name of the constant buffer variable this interface's generated declarations give a
    /// group's block: <see cref="BlockVariableName"/>, followed for the pass group by the interface's
    /// <see cref="Stamp"/>, such as <c>passGroupIsa1234ABCD</c>. The generated include defines the unstamped name as the
    /// stamped one, so a pass reads its pass block as <c>passGroup</c> either way.</summary>
    /// <param name="group">The frequency group.</param>
    /// <returns>The variable's name.</returns>
    public string BlockVariableNameOf(ShaderInterfaceGroup group) =>
        (((group == ShaderInterfaceGroup.Pass) && (Stamp is not null))
            ? (BlockVariableName(group: group) + Stamp)
            : BlockVariableName(group: group));
    /// <summary>Returns this interface carrying a stamp in place of its own.</summary>
    /// <param name="stamp">The stamp (<see cref="Stamp"/>).</param>
    /// <returns>The stamped interface.</returns>
    /// <exception cref="InvalidDataException">The stamp is malformed, or the interface has no pass block to carry
    /// it.</exception>
    public ShaderInterface Stamped(string stamp) =>
        new(
            members: Members,
            name: Name,
            pushesIndex: PushesIndex,
            stamp: stamp
        );
    /// <summary>Returns the HLSL name of the struct generated for a group's block.</summary>
    /// <param name="interfaceName">The interface's hyphenated name.</param>
    /// <param name="group">The frequency group.</param>
    /// <returns>The interface name in upper camel case followed by the group's name, such as
    /// <c>FilmGrainFrame</c>.</returns>
    public static string BlockTypeName(string interfaceName, ShaderInterfaceGroup group) {
        ArgumentNullException.ThrowIfNull(argument: interfaceName);

        return string.Concat(
            str0: TypePrefix(interfaceName: interfaceName),
            str1: group.ToString()
        );
    }
    /// <summary>Returns the HLSL name of the struct generated for the pushed index.</summary>
    /// <param name="interfaceName">The interface's hyphenated name.</param>
    /// <returns>The interface name in upper camel case followed by <c>PushedIndex</c>, such as
    /// <c>SdfBricksPushedIndex</c>.</returns>
    public static string PushedIndexTypeName(string interfaceName) {
        ArgumentNullException.ThrowIfNull(argument: interfaceName);

        return (TypePrefix(interfaceName: interfaceName) + "PushedIndex");
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

    // The interface name in upper camel case, which every generated struct name begins with.
    private static string TypePrefix(string interfaceName) =>
        string.Concat(values: interfaceName.Split(separator: '-').Select(selector: static word => (char.ToUpperInvariant(c: word[0]) + word[1..])));
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
