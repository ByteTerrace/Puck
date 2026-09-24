using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>How a model member may be read and written, and how its serializer treats it.</summary>
[Flags]
public enum WorldModelAccess : byte {
    /// <summary>The member is neither read nor written.</summary>
    None = 0,
    /// <summary>The member has a getter.</summary>
    Read = 1,
    /// <summary>The member has a setter or an init accessor.</summary>
    Write = 2,
    /// <summary>The member is the type's extension-data bag, which holds every unmapped JSON member.</summary>
    ExtensionData = 4,
    /// <summary>The member carries <c>[JsonIgnore]</c>.</summary>
    Ignored = 8,
}
/// <summary>One member of a world document model type.</summary>
/// <param name="Name">The member's JSON name.</param>
/// <param name="Type">The member's declared type.</param>
/// <param name="DeclaringType">The type that declares the C# member, which a derived record inherits it from.</param>
/// <param name="Member">The C# member's name.</param>
/// <param name="Access">How the member is read and written.</param>
public sealed record WorldModelMember(string Name, Type Type, Type DeclaringType, string Member, WorldModelAccess Access);
/// <summary>One arm of a polymorphic world document model type.</summary>
/// <param name="Discriminator">The arm's <c>$type</c> value; <see langword="null"/> for an arm with no string
/// discriminator.</param>
/// <param name="Type">The arm's type.</param>
public sealed record WorldModelArm(string? Discriminator, Type Type);
/// <summary>What the world document model declares about one type: the description its serializer's resolver gives
/// it, before the type is prepared for serialization.</summary>
/// <param name="Type">The type.</param>
/// <param name="Described">Whether the serializer describes the type at all; a type it cannot describe carries only
/// its <paramref name="Properties"/>.</param>
/// <param name="Kind">The serializer's kind for the type.</param>
/// <param name="ElementType">The element type of a collection or the value type of a dictionary; otherwise
/// <see langword="null"/>.</param>
/// <param name="Arms">The polymorphic arms the serializer resolves under the type, the ones
/// <see cref="WorldJsonVocabulary"/> adds included, in resolution order.</param>
/// <param name="Members">The serializer's members of an object type, in its order.</param>
/// <param name="Properties">The public instance properties of a type the serializer does not describe as an object,
/// each named by its camel-cased JSON name: the members a converter-backed shape reads.</param>
public sealed record WorldModelType(Type Type, bool Described, JsonTypeInfoKind Kind, Type? ElementType, IReadOnlyList<WorldModelArm> Arms, IReadOnlyList<WorldModelMember> Members, IReadOnlyList<WorldModelMember> Properties);
/// <summary>
/// The world document model's shape — every type reachable from <see cref="WorldDefinition"/>, with its members,
/// polymorphic arms and element types — as a table generated from <see cref="WorldJsonContext"/>'s description of
/// the model, so a walk over the model (the call-argument forms, the module namespace rewrite) reads it without
/// describing a type at run time. <c>puck schema</c> writes the table beside this file and <c>puck schema --check</c>
/// fails when it disagrees with the model.
/// </summary>
public static partial class WorldModelShape {
    private static readonly Lazy<Dictionary<Type, WorldModelType>> Table = new(valueFactory: static () => {
        var types = Generated();
        var table = new Dictionary<Type, WorldModelType>(capacity: types.Length);

        foreach (var type in types) {
            table.Add(key: type.Type, value: type);
        }

        return table;
    });

    /// <summary>Gets every type the table holds.</summary>
    public static IReadOnlyCollection<WorldModelType> Types =>
        Table.Value.Values;

    /// <summary>Returns what the model declares about a type.</summary>
    /// <param name="type">The type; a nullable value type answers for its underlying type.</param>
    /// <returns>The type's shape, or <see langword="null"/> when the type is not reachable from
    /// <see cref="WorldDefinition"/> or is a primitive, an enumeration or a string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static WorldModelType? Of(Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        return Table.Value.GetValueOrDefault(key: (Nullable.GetUnderlyingType(nullableType: type) ?? type));
    }

    private static WorldModelArm A(string? discriminator, Type type) =>
        new(Discriminator: discriminator, Type: type);
    private static WorldModelMember M(string name, Type type, Type declaringType, string member, WorldModelAccess access) =>
        new(Access: access, DeclaringType: declaringType, Member: member, Name: name, Type: type);
    private static WorldModelType T(Type type, bool described, JsonTypeInfoKind kind, Type? elementType, WorldModelArm[] arms, WorldModelMember[] members, WorldModelMember[] properties) =>
        new(Arms: arms, Described: described, ElementType: elementType, Kind: kind, Members: members, Properties: properties, Type: type);
}
