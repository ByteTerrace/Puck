using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>How a member of a <c>$type</c> arm is written when a source spells the arm as a call.</summary>
public enum WorldArgumentForm : byte {
    /// <summary>The model holds no opinion: a number, a flag, a nested arm, or a member two arms disagree on.</summary>
    Unclassified,
    /// <summary>A reference to a declared name.</summary>
    Name,
    /// <summary>A cell key in the dynamic-key grammar.</summary>
    Key,
    /// <summary>A value expression.</summary>
    Expression,
    /// <summary>One value of an enumeration.</summary>
    Choice,
    /// <summary>Free text.</summary>
    Text,
}
/// <summary>The form of every member of every <c>$type</c> arm the document model declares, read from the model:
/// a member's <see cref="WorldNameRegistry"/> role where it carries one, and its C# type otherwise.</summary>
/// <remarks>A source spells an arm as <c>discriminator(member: value, …)</c>, and the discriminator alone names the
/// arm there, so arms that share a discriminator and a member name must agree on its form;
/// <see cref="Conflicts"/> lists the ones that do not.</remarks>
public static class WorldCallArguments {
    private sealed record CallTable(
        FrozenDictionary<(string Discriminator, string Member), WorldArgumentForm> Forms,
        FrozenDictionary<(string Discriminator, string Member), FrozenSet<string>> Words,
        FrozenDictionary<string, Type> Arms,
        FrozenDictionary<(Type Context, string Discriminator), Type> PositionedArms,
        IReadOnlyList<string> Conflicts
    );
    private static readonly Lazy<CallTable> Table = new(valueFactory: Build);

    private static CallTable Build() {
        var options = WorldJsonContext.Default.Options;
        var forms = new Dictionary<(string Discriminator, string Member), WorldArgumentForm>();
        var words = new Dictionary<(string Discriminator, string Member), HashSet<string>>();
        var arms = new Dictionary<string, Type>(comparer: StringComparer.Ordinal);
        var positionedArms = new Dictionary<(Type Context, string Discriminator), Type>();
        var conflicts = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>();

        pending.Push(item: typeof(WorldDefinition));
        while (pending.TryPop(result: out var type)) {
            type = WorldNameRegistry.Unwrap(type: type);
            if (type.IsArray) {
                type = type.GetElementType()!;
            }
            if (!visited.Add(item: type) || type.IsPrimitive || type.IsEnum || (type == typeof(string)) || ((type.Namespace?.StartsWith(comparisonType: StringComparison.Ordinal, value: "System") == true) && !type.IsGenericType)) {
                continue;
            }
            if (type.IsGenericType) {
                foreach (var argument in type.GetGenericArguments()) {
                    pending.Push(item: argument);
                }

                continue;
            }

            JsonTypeInfo? info;

            try {
                info = options.GetTypeInfo(type: type);
            } catch (Exception failure) when ((failure is NotSupportedException or InvalidOperationException)) {
                info = null;
            }

            if (info?.PolymorphismOptions is { } polymorphism) {
                foreach (var derived in polymorphism.DerivedTypes) {
                    if (derived.TypeDiscriminator is string discriminator) {
                        _ = arms.TryAdd(key: discriminator, value: derived.DerivedType);
                        positionedArms[(type, discriminator)] = derived.DerivedType;
                        positionedArms[(derived.DerivedType, discriminator)] = derived.DerivedType;
                        Record(arm: derived.DerivedType, discriminator: discriminator);
                    }
                    pending.Push(item: derived.DerivedType);
                }
            }
            if (info is { Kind: JsonTypeInfoKind.Object }) {
                foreach (var property in info.Properties) {
                    pending.Push(item: property.PropertyType);
                }
            } else {
                foreach (var property in type.GetProperties(bindingAttr: System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                    if (property.GetIndexParameters().Length == 0) {
                        pending.Push(item: property.PropertyType);
                    }
                }
            }
        }

        return new CallTable(
            Forms: forms.ToFrozenDictionary(),
            Words: words.ToFrozenDictionary(elementSelector: static pair => pair.Value.ToFrozenSet(comparer: StringComparer.Ordinal), keySelector: static pair => pair.Key),
            Arms: arms.ToFrozenDictionary(comparer: StringComparer.Ordinal),
            PositionedArms: positionedArms.ToFrozenDictionary(),
            Conflicts: [.. conflicts]
        );

        void Record(Type arm, string discriminator) {
            JsonTypeInfo info;

            try {
                info = options.GetTypeInfo(type: arm);
            } catch (Exception failure) when ((failure is NotSupportedException or InvalidOperationException)) {
                return;
            }

            foreach (var property in info.Properties) {
                if (property.IsExtensionData || (property.Get is null)) {
                    continue;
                }

                var key = (discriminator, property.Name);
                var form = FormOf(property: property);

                if (form == WorldArgumentForm.Choice) {
                    if (!words.TryGetValue(key: key, value: out var admitted)) {
                        words[key] = admitted = new(comparer: StringComparer.Ordinal);
                    }

                    admitted.UnionWith(other: Enum.GetNames(enumType: WorldNameRegistry.Unwrap(type: property.PropertyType)));
                }

                if (forms.TryGetValue(key: key, value: out var recorded) && (recorded != form)) {
                    _ = conflicts.Add(item: $"{discriminator}.{property.Name}");
                    forms[key] = WorldArgumentForm.Unclassified;
                } else if (!conflicts.Contains(item: $"{discriminator}.{property.Name}")) {
                    forms[key] = form;
                }
            }
        }
    }
    private static WorldArgumentForm FormOf(JsonPropertyInfo property) {
        var (declaringType, member) = WorldNameRegistry.ResolveMember(property: property);

        if (WorldNameRegistry.TryResolve(declaringType: declaringType, field: out var field, member: member, propertyType: property.PropertyType)) {
            return (field.Role switch {
                WorldNameRole.Names => WorldArgumentForm.Name,
                WorldNameRole.Key => WorldArgumentForm.Key,
                WorldNameRole.Expression => WorldArgumentForm.Expression,
                _ => WorldArgumentForm.Unclassified,
            });
        }

        var leaf = WorldNameRegistry.Unwrap(type: property.PropertyType);

        // A holder's own `name` declares it; a declaration is no reference, and a declared name need not be one
        // bare word.
        if (string.Equals(a: property.Name, b: "name", comparisonType: StringComparison.Ordinal)) {
            return WorldArgumentForm.Unclassified;
        }
        if ((leaf == typeof(StateChannelRef)) || (leaf == typeof(CellName))) {
            return WorldArgumentForm.Name;
        }
        if (leaf == typeof(ExpressionProgram)) {
            return WorldArgumentForm.Expression;
        }
        if (leaf.IsEnum) {
            return WorldArgumentForm.Choice;
        }

        return ((leaf == typeof(string))
            ? WorldArgumentForm.Text
            : WorldArgumentForm.Unclassified
        );
    }

    /// <summary>Gets every <c>discriminator.member</c> whose arms disagree on its form, in ordinal order. Such a
    /// member reads as <see cref="WorldArgumentForm.Unclassified"/>.</summary>
    public static IReadOnlyList<string> Conflicts => Table.Value.Conflicts;
    /// <summary>Gets every classified member, keyed by its arm's discriminator and its JSON name.</summary>
    public static IReadOnlyDictionary<(string Discriminator, string Member), WorldArgumentForm> Forms => Table.Value.Forms;

    /// <summary>Returns a value indicating whether <paramref name="word"/> is one value of the enumeration a
    /// <see cref="WorldArgumentForm.Choice"/> member holds.</summary>
    /// <param name="discriminator">The arm's <c>$type</c> value.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <param name="word">The word to test, in the enumeration's own case.</param>
    /// <returns><see langword="true"/> when the member is a choice and admits the word.</returns>
    public static bool IsChoiceWord(string discriminator, string member, string word) => (
        Table.Value.Words.TryGetValue(key: (discriminator, member), value: out var admitted) &&
        admitted.Contains(item: word)
    );

    // Cache the finite schema, never authored lookup keys. An editor can ask about arbitrarily many unknown
    // members over its lifetime; a negative entry for each would retain their source text indefinitely.
    private sealed record MemberMetadata(Type Type, WorldArgumentForm Form, FrozenSet<string>? Choices);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FrozenDictionary<string, MemberMetadata>> Members = new();

    private static MemberMetadata? FindMember(Type owner, string member) => Members.GetOrAdd(
        key: WorldNameRegistry.Unwrap(type: owner),
        valueFactory: static type => BuildMembers(owner: type)
    ).GetValueOrDefault(key: member);
    private static FrozenDictionary<string, MemberMetadata> BuildMembers(Type owner) {
        JsonTypeInfo info;

        try {
            info = WorldJsonContext.Default.Options.GetTypeInfo(type: owner);
        } catch (Exception failure) when ((failure is NotSupportedException or InvalidOperationException)) {
            return FrozenDictionary<string, MemberMetadata>.Empty;
        }

        var members = new Dictionary<string, MemberMetadata>(comparer: StringComparer.Ordinal);

        foreach (var property in info.Properties) {
            if (!property.IsExtensionData) {
                var type = WorldNameRegistry.Unwrap(type: property.PropertyType);

                members[property.Name] = new MemberMetadata(
                    Type: type,
                    Form: FormOf(property: property),
                    Choices: (type.IsEnum ? Enum.GetNames(enumType: type).ToFrozenSet(comparer: StringComparer.OrdinalIgnoreCase) : null)
                );
            }
        }

        return members.ToFrozenDictionary(comparer: StringComparer.Ordinal);
    }

    /// <summary>Returns the arm a <c>$type</c> value names.</summary>
    /// <param name="baseType">The polymorphic type the arm fills, or <see langword="null"/> when the position is not
    /// known; the first arm any base declares under the discriminator then answers.</param>
    /// <param name="discriminator">The arm's <c>$type</c> value.</param>
    /// <returns>The arm's type, or <see langword="null"/> when the model declares no such arm at the requested position.</returns>
    public static Type? ArmType(Type? baseType, string discriminator) {
        ArgumentNullException.ThrowIfNull(argument: discriminator);

        var table = Table.Value;
        return ((baseType is null)
            ? table.Arms.GetValueOrDefault(key: discriminator)
            : table.PositionedArms.GetValueOrDefault(key: (WorldNameRegistry.Unwrap(type: baseType), discriminator)));
    }
    /// <summary>Returns the form of one member of a model type.</summary>
    /// <param name="owner">The type declaring the member.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <returns>The member's form, or <see cref="WorldArgumentForm.Unclassified"/> when the type declares no such
    /// member.</returns>
    public static WorldArgumentForm Classify(Type owner, string member) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: member);

        return (FindMember(member: member, owner: owner)?.Form ?? WorldArgumentForm.Unclassified);
    }
    /// <summary>Returns a value indicating whether <paramref name="word"/> is one value of the enumeration a
    /// member of a model type holds.</summary>
    /// <param name="owner">The type declaring the member.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <param name="word">The word to test; a document reads an enumeration word in any case.</param>
    /// <returns><see langword="true"/> when the member is an enumeration and declares the word.</returns>
    public static bool IsChoiceWord(Type owner, string member, string word) {
        ArgumentNullException.ThrowIfNull(argument: owner);

        return (FindMember(member: member, owner: owner)?.Choices?.Contains(item: word) == true);
    }
    /// <summary>Returns the type a member of a model type holds, with a list reduced to its element.</summary>
    /// <param name="owner">The type declaring the member.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <returns>The member's type, or <see langword="null"/> when the type declares no such member.</returns>
    public static Type? MemberType(Type owner, string member) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: member);

        return FindMember(member: member, owner: owner)?.Type;
    }
    /// <summary>Returns the form of one member of a <c>$type</c> arm.</summary>
    /// <param name="discriminator">The arm's <c>$type</c> value.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <returns>The member's form, or <see cref="WorldArgumentForm.Unclassified"/> when the model declares no such
    /// member.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="discriminator"/> or <paramref name="member"/> is
    /// <see langword="null"/>.</exception>
    public static WorldArgumentForm Classify(string discriminator, string member) {
        ArgumentNullException.ThrowIfNull(argument: discriminator);
        ArgumentNullException.ThrowIfNull(argument: member);

        return Table.Value.Forms.GetValueOrDefault(key: (discriminator, member));
    }
}
