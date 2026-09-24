using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Puck.World;

namespace Puck.Cli.Schema;

// The generator behind src/Puck.World.Schema/WorldModelShape.generated.cs: it describes every type reachable from
// WorldDefinition through the resolver WorldJsonContext serializes with — the description a type has before it is
// prepared for serialization — and renders the table WorldModelShape reads at run time. It reaches every type either
// walk over the shape reads: each polymorphic arm, member, element and generic argument, and the public properties
// of a type the resolver does not describe as an object. `puck schema` writes the rendering and `--check` compares
// it; Reflect is also the oracle the shape's own law compares the compiled table with.
internal static class WorldModelShapeSource {
    public const string RelativePath = "src/Puck.World.Schema/WorldModelShape.generated.cs";

    private static readonly Assembly ModelAssembly = typeof(WorldDefinition).Assembly;

    // The resolver's description of a type, unconfigured; null when the context does not describe it.
    public static JsonTypeInfo? Describe(Type type) {
        var options = WorldJsonContext.Default.Options;
        JsonTypeInfo? info;

        try {
            info = options.TypeInfoResolver!.GetTypeInfo(
                options: options,
                type: type
            );
        } catch (Exception failure) when ((failure is NotSupportedException or InvalidOperationException)) {
            return null;
        }

        return info;
    }
    public static IReadOnlyList<WorldModelType> Reflect() {
        var shapes = new List<WorldModelType>();
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>();

        pending.Push(item: typeof(WorldDefinition));
        while (pending.TryPop(result: out var type)) {
            type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
            if (type.IsPrimitive || type.IsEnum || (type == typeof(string)) || (type == typeof(decimal)) || type.IsGenericParameter || !visited.Add(item: type)) {
                continue;
            }
            if (type.IsArray) {
                pending.Push(item: type.GetElementType()!);
            }
            if (type.IsGenericType) {
                foreach (var argument in type.GetGenericArguments()) {
                    pending.Push(item: argument);
                }
            }

            var info = Describe(type: type);
            var systemLeaf = (!type.IsGenericType && !type.IsArray && (type.Namespace?.StartsWith(comparisonType: StringComparison.Ordinal, value: "System") == true));
            var arms = new List<WorldModelArm>();
            var members = new List<WorldModelMember>();
            var properties = new List<WorldModelMember>();

            if (info?.PolymorphismOptions is { } polymorphism) {
                foreach (var derived in polymorphism.DerivedTypes) {
                    arms.Add(item: new WorldModelArm(
                        Discriminator: (derived.TypeDiscriminator as string),
                        Type: derived.DerivedType
                    ));
                    pending.Push(item: derived.DerivedType);
                }
            }
            if (info is { Kind: JsonTypeInfoKind.Object }) {
                foreach (var property in info.Properties) {
                    var (declaringType, member) = WorldNameRegistry.ResolveMember(property: property);

                    members.Add(item: new WorldModelMember(
                        Access: (((property.Get is null) ? WorldModelAccess.None : WorldModelAccess.Read) | ((property.Set is null) ? WorldModelAccess.None : WorldModelAccess.Write)) | (property.IsExtensionData ? WorldModelAccess.ExtensionData : WorldModelAccess.None),
                        DeclaringType: declaringType,
                        Member: member,
                        Name: property.Name,
                        Type: property.PropertyType
                    ));
                    pending.Push(item: property.PropertyType);
                }
            } else if (!systemLeaf && !type.IsGenericType && !type.IsArray) {
                foreach (var property in type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
                    if (property.GetIndexParameters().Length != 0) {
                        continue;
                    }

                    properties.Add(item: new WorldModelMember(
                        Access: (((property.GetMethod is null) ? WorldModelAccess.None : WorldModelAccess.Read) | ((property.SetMethod is null) ? WorldModelAccess.None : WorldModelAccess.Write)) | (property.IsDefined(attributeType: typeof(JsonIgnoreAttribute), inherit: true) ? WorldModelAccess.Ignored : WorldModelAccess.None),
                        DeclaringType: (property.DeclaringType ?? type),
                        Member: property.Name,
                        Name: JsonNamingPolicy.CamelCase.ConvertName(name: property.Name),
                        Type: property.PropertyType
                    ));
                    pending.Push(item: property.PropertyType);
                }
            }

            var elementType = ((info is { Kind: (JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary) })
                ? info.ElementType
                : null);

            if (elementType is not null) {
                pending.Push(item: elementType);
            }
            // A System type the serializer holds as a value reads as nothing to either walk, so it takes no row.
            if (systemLeaf && (info is null or { Kind: JsonTypeInfoKind.None }) && (arms.Count == 0)) {
                continue;
            }

            shapes.Add(item: new WorldModelType(
                Arms: arms,
                Described: (info is not null),
                ElementType: elementType,
                Kind: (info?.Kind ?? JsonTypeInfoKind.None),
                Members: members,
                Properties: properties,
                Type: type
            ));
        }

        shapes.Sort(comparison: static (a, b) => string.CompareOrdinal(
            strA: CSharpName(type: a.Type),
            strB: CSharpName(type: b.Type)
        ));

        return shapes;
    }
    public static string Render(IReadOnlyList<WorldModelType> shapes) {
        var text = new StringBuilder();

        _ = text.Append(value: "// <auto-generated/>\n");
        _ = text.Append(value: "// Generated by `puck schema` from WorldJsonContext's description of every type reachable from WorldDefinition;\n");
        _ = text.Append(value: "// do not hand-edit. `puck schema --check` fails when this table disagrees with the model.\n");
        _ = text.Append(value: "#nullable enable\n\n");
        _ = text.Append(value: "using System.Text.Json.Serialization.Metadata;\n\n");
        _ = text.Append(value: "using static Puck.World.WorldModelAccess;\n\n");
        _ = text.Append(value: "namespace Puck.World;\n\n");
        _ = text.Append(value: "public static partial class WorldModelShape {\n");
        _ = text.Append(value: "    private static WorldModelType[] Generated() => [\n");

        foreach (var shape in shapes) {
            _ = text.Append(value: $"        T(typeof({CSharpName(type: shape.Type)}), {(shape.Described ? "true" : "false")}, JsonTypeInfoKind.{shape.Kind}, {((shape.ElementType is { } element) ? $"typeof({CSharpName(type: element)})" : "null")},\n");
            _ = text.Append(value: "            [");
            _ = text.Append(value: string.Join(
                separator: ", ",
                values: shape.Arms.Select(selector: static arm => $"A({Literal(text: arm.Discriminator)}, typeof({CSharpName(type: arm.Type)}))")
            ));
            _ = text.Append(value: "],\n");
            AppendMembers(
                members: shape.Members,
                text: text
            );
            _ = text.Append(value: ",\n");
            AppendMembers(
                members: shape.Properties,
                text: text
            );
            _ = text.Append(value: "),\n");
        }

        _ = text.Append(value: "    ];\n}\n");

        return text.ToString();
    }

    private static void AppendMembers(StringBuilder text, IReadOnlyList<WorldModelMember> members) {
        if (members.Count == 0) {
            _ = text.Append(value: "            []");

            return;
        }

        _ = text.Append(value: "            [\n");
        foreach (var member in members) {
            _ = text.Append(value: $"                M({Literal(text: member.Name)}, typeof({CSharpName(type: member.Type)}), typeof({CSharpName(type: member.DeclaringType)}), {Literal(text: member.Member)}, {AccessText(access: member.Access)}),\n");
        }
        _ = text.Append(value: "            ]");
    }
    private static string AccessText(WorldModelAccess access) {
        if (access == WorldModelAccess.None) {
            return nameof(WorldModelAccess.None);
        }

        var names = new List<string>();

        foreach (var flag in ((ReadOnlySpan<WorldModelAccess>)[WorldModelAccess.Read, WorldModelAccess.Write, WorldModelAccess.ExtensionData, WorldModelAccess.Ignored])) {
            if ((access & flag) != 0) {
                names.Add(item: flag.ToString());
            }
        }

        return string.Join(
            separator: " | ",
            values: names
        );
    }

    // The C# spelling of a type the generated file can name: fully qualified, nested types dotted, generic arguments
    // spelled out. A type the model assembly cannot name refuses here rather than in a build of the generated file.
    public static string CSharpName(Type type) {
        if (type.IsArray) {
            return $"{CSharpName(type: type.GetElementType()!)}[{new string(c: ',', count: (type.GetArrayRank() - 1))}]";
        }
        if (!IsNameable(type: (type.IsGenericType ? type.GetGenericTypeDefinition() : type))) {
            throw new InvalidOperationException(message: $"The world document model reaches {type}, which {ModelAssembly.GetName().Name} cannot name.");
        }

        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition().FullName!;

            return $"global::{definition[..definition.IndexOf(value: '`')].Replace(newChar: '.', oldChar: '+')}<{string.Join(separator: ", ", values: type.GetGenericArguments().Select(selector: CSharpName))}>";
        }

        return $"global::{type.FullName!.Replace(newChar: '.', oldChar: '+')}";
    }

    private static bool IsNameable(Type type) {
        if (type.IsGenericParameter || (type.IsNested && type.DeclaringType!.IsGenericType)) {
            return false;
        }
        for (var level = type; (level is not null); level = level.DeclaringType) {
            if (level.IsNestedPrivate || level.IsNestedFamily || level.IsNestedFamANDAssem || (!level.IsVisible && (level.Assembly != ModelAssembly))) {
                return false;
            }
        }

        return true;
    }
    private static string Literal(string? text) {
        if (text is null) {
            return "null";
        }

        var literal = new StringBuilder(value: "\"");

        foreach (var character in text) {
            _ = (character switch {
                '"' => literal.Append(value: "\\\""),
                '\\' => literal.Append(value: "\\\\"),
                _ when char.IsControl(c: character) => literal.Append(value: $"\\u{((int)character):x4}"),
                _ => literal.Append(value: character),
            });
        }

        return literal.Append(value: '"').ToString();
    }
}
