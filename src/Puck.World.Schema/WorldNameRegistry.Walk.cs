using Puck.Abstractions.Documents;
using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>One member of the document model that carries no module-scoped name, with the reason.</summary>
/// <param name="Owner">The type declaring the member.</param>
/// <param name="Member">The member's C# name.</param>
/// <param name="Reason">Why the member is outside every <see cref="WorldNameKind"/>.</param>
public sealed record WorldNameExclusion(Type Owner, string Member, string Reason);
/// <summary>One JSON path a registered member reaches in a <c>puck.world.definition.v1</c> document.</summary>
/// <param name="Path">The path from the root: <c>rules[].effects[setState].state</c>, where <c>[]</c> is a list
/// element and <c>[name]</c> a <c>$type</c> arm.</param>
/// <param name="Field">The registration the path carries.</param>
public sealed record WorldNameSite(string Path, WorldNameField Field);
public static partial class WorldNameRegistry {
    // The expression converter names each flattened payload by its operation's PayloadShape,
    // and each vector operand by its camel-cased discriminator.
    internal static readonly FrozenDictionary<PayloadShape, Type> PayloadTypes = typeof(InstructionPayload).GetNestedTypes()
        .ToFrozenDictionary(type => Enum.Parse<PayloadShape>(type.Name));
    internal static readonly FrozenDictionary<string, Type> VectorTypes = typeof(VectorOperand).GetNestedTypes()
        .ToFrozenDictionary(type => JsonNamingPolicy.CamelCase.ConvertName(type.Name), StringComparer.Ordinal);

    internal static WorldModelMember KeyClock => KeyClockMember.Value;
    private static readonly Lazy<WorldModelMember> KeyClockMember = new(() => ClockOf(shapeOf: WorldModelShape.Of));

    private static WorldModelMember ClockOf(Func<Type, WorldModelType?> shapeOf) =>
        shapeOf(typeof(IWorldKeyTrack))!.Properties.Single(property => property.Member == nameof(IWorldKeyTrack.Clock));

    private static readonly Lazy<Report> Walked = new(valueFactory: static () => Over(shapeOf: WorldModelShape.Of));

    /// <summary>Gets every JSON path a registered member reaches, in document order.</summary>
    public static IReadOnlyList<WorldNameSite> Sites => Walked.Value.Sites;
    /// <summary>Gets the registry of the compiled <see cref="WorldModelShape"/>.</summary>
    public static Report Live => Walked.Value;
    /// <summary>Gets every unclassified text or name member, each as
    /// <c>path (Owner.Member)</c>; empty when the registry covers the model.</summary>
    public static IReadOnlyList<string> Uncovered => Walked.Value.Uncovered;

    /// <summary>Walks a description of the document model from <see cref="WorldDefinition"/>: the compiled
    /// <see cref="WorldModelShape"/> for the live registry, or the table <c>puck schema</c> has just reflected from
    /// the model before it writes the compiled one, so a model edit is registered without a rebuild in between.</summary>
    /// <param name="shapeOf">Answers what the model declares about a type, as <see cref="WorldModelShape.Of"/> does.</param>
    /// <returns>The registered sites, the literal-text exclusions and the unclassified members.</returns>
    public static Report Over(Func<Type, WorldModelType?> shapeOf) {
        ArgumentNullException.ThrowIfNull(argument: shapeOf);

        return Report.Run(root: typeof(WorldDefinition), shapeOf: shapeOf);
    }

    /// <summary>Gets every path a registered member reaches in a value of one model type, rooted at that type: the
    /// positions a value a writer submits on its own (a placement, a section's defaults) carries, which the write
    /// door's laws enumerate.</summary>
    /// <param name="root">The model type the paths start at.</param>
    /// <returns>The sites, in model order; a path ending in <c>…</c> re-enters a type above it.</returns>
    public static IReadOnlyList<WorldNameSite> SitesUnder(Type root) {
        ArgumentNullException.ThrowIfNull(argument: root);

        return Report.Run(root: root, shapeOf: WorldModelShape.Of).Sites;
    }
    /// <summary>Renders the registry as the Markdown document <c>docs/world-name-registry.md</c> holds.</summary>
    /// <returns>The document text, LF-terminated.</returns>
    public static string Render() => Walked.Value.Render();

    private static string TypeName(Type type) =>
        ((type.DeclaringType is { } outer)
            ? $"{TypeName(type: outer)}.{type.Name}"
            : type.Name
        );

    /// <summary>Returns the C# member a serializer property reads: the metadata's own attribute provider when it
    /// carries one, else the public property whose camel-cased name is the JSON name.</summary>
    /// <param name="property">The serializer's property.</param>
    /// <returns>The type declaring the member and the member's C# name.</returns>
    public static (Type DeclaringType, string Member) ResolveMember(JsonPropertyInfo property) {
        ArgumentNullException.ThrowIfNull(argument: property);

        if (property.AttributeProvider is MemberInfo member) {
            return ((member.DeclaringType ?? property.DeclaringType), member.Name);
        }

        foreach (var candidate in property.DeclaringType.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
            if (string.Equals(
                a: JsonNamingPolicy.CamelCase.ConvertName(name: candidate.Name),
                b: property.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                return ((candidate.DeclaringType ?? property.DeclaringType), candidate.Name);
            }
        }

        return (property.DeclaringType, property.Name);
    }

    private static WorldNameField? Implicit(Type declaringType, string member, Type propertyType) {
        var leaf = ValueType(propertyType);
        return leaf == typeof(BindableScalar) || leaf == typeof(BindableColor) || leaf == typeof(BindableAngle) || typeof(Puck.Assets.Documents.IDocumentStateValue).IsAssignableFrom(leaf)
            ? new(declaringType, member, WorldNameKind.State, WorldNameRole.Binding, WorldExportFacet.Binding)
            : null;
    }

    internal static Type Unwrap(Type type) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (
            type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        ) {
            return Unwrap(type: type.GetGenericArguments()[0]);
        }

        return type;
    }

    private static readonly Puck.Abstractions.CodeDerivedTable<WorldModelType, WorldModelMember[]> DocumentMembers = new();

    internal static WorldModelMember[] MembersOf(WorldModelType shape) => DocumentMembers.GetValue(shape, static model => {
        // The row converter reads its public writable properties. Ordinary objects follow the serializer.
        var members = typeof(StateRow).IsAssignableFrom(model.Type) ? model.Properties : model.Members;
        const WorldModelAccess Mask = WorldModelAccess.Read | WorldModelAccess.Write | WorldModelAccess.Ignored | WorldModelAccess.ExtensionData;
        return [.. members.Where(property => (property.Access & Mask) == (WorldModelAccess.Read | WorldModelAccess.Write))];
    });

    /// <summary>What a walk of the document model registers: every name-bearing site, every literal-text member with
    /// its purpose, and every member still unclassified.</summary>
    public sealed class Report {
        private readonly Func<Type, WorldModelType?> m_shapeOf;
        private readonly List<Type> m_stack = [];

        private Report(Func<Type, WorldModelType?> shapeOf) {
            m_shapeOf = shapeOf;
        }

        private List<(string Path, Type Owner, string Member, string Reason)> Excluded { get; } = [];
        private List<WorldNameSite> SiteList { get; } = [];
        private List<string> UncoveredList { get; } = [];

        /// <summary>Gets every JSON path a registered member reaches, in document order.</summary>
        public IReadOnlyList<WorldNameSite> Sites => SiteList;
        /// <summary>Gets every unclassified text or name member, each as <c>path (Owner.Member: Type)</c>.</summary>
        public IReadOnlyList<string> Uncovered => UncoveredList;

        private void Record(string path, WorldModelMember model) {
            if (model.NameField is { } field) {
                SiteList.Add(new WorldNameSite(path, field));
                if (model.IdentifierBinding is { } binding) { SiteList.Add(new WorldNameSite(path, binding)); }
            } else if (model.TextPurpose is { } purpose) {
                Excluded.Add((path, model.DeclaringType, model.Member, purpose));
            } else if (IsUncovered(model)) {
                UncoveredList.Add($"{path} ({TypeName(model.DeclaringType)}.{model.Member}: {model.Type.Name})");
            }
        }
        // A vector-operand member is itself a union whose arms carry names, so it expands one level further.
        private void RecordPayload(Type payload, string arm) {
            foreach (var model in m_shapeOf(payload)!.Properties) {
                if (Unwrap(type: model.Type) != typeof(VectorOperand)) {
                    Record($"{arm}.{model.Name}", model);
                    continue;
                }
                foreach (var (discriminator, operand) in VectorTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                    var side = $"{arm}.{model.Name}[{discriminator}]";
                    foreach (var property in m_shapeOf(operand)!.Properties) {
                        Record($"{side}.{property.Name}", property);
                    }
                }
            }
        }
        private void VisitType(Type type, string path) {
            type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

            // The IR flattens an instruction's payload onto the instruction object, so the walk enumerates the
            // payload union's own cases rather than resolving a JSON type info for a case it never serializes on
            // its own. Enumerating them, rather than naming the name-bearing ones by hand, is what lets the
            // coverage check reach a payload member nothing registered. A subprogram's instructions are the same
            // shape, so both lists are recorded.
            if (WorldKeyedValues.IsKeyable(type)) {
                Record($"{path}.clock", ClockOf(shapeOf: m_shapeOf));

                // A scalar's keys may join a second track, which names a clock of its own.
                if ((type == typeof(BindableScalar)) || (type == typeof(BindableAngle))) {
                    Record($"{path}.with.clock", ClockOf(shapeOf: m_shapeOf));
                }

                return;
            }
            if (type == typeof(ExpressionProgram)) {
                foreach (var list in new[] { $"{path}{{instructions}}", $"{path}{{subprograms}}{{instructions}}" }) {
                    foreach (var payload in PayloadTypes.OrderBy(pair => pair.Key).Select(pair => pair.Value)) {
                        RecordPayload(
                            arm: $"{list}[{JsonNamingPolicy.CamelCase.ConvertName(name: payload.Name)}]",
                            payload: payload
                        );
                    }
                }

                return;
            }

            // A creation document is read by hand (WorldModuleNamespace.VisitCreationBindings): every binding token in
            // it is one position.
            if (type == typeof(Puck.World.Authoring.CreationDocument)) {
                SiteList.Add(item: new WorldNameSite(Path: $"{path}{{state}}", Field: CreationBinding));

                return;
            }

            if (type == typeof(WorldLatticeScalar)) {
                Record($"{path}{{row}}", m_shapeOf(type)!.Properties.Single(property => property.Member == nameof(WorldLatticeScalar.Row)));

                return;
            }

            if (
                type.IsPrimitive ||
                type.IsEnum ||
                (type == typeof(string)) ||
                (type == typeof(decimal))
            ) {
                return;
            }

            var typeInfo = m_shapeOf(type);

            if (typeInfo is not { Described: true }) { return; }

            if (m_stack.Contains(item: type)) {
                SiteList.Add(item: new WorldNameSite(
                    Path: $"{path}…",
                    Field: new WorldNameField(
                        Owner: type,
                        Member: string.Empty,
                        Kind: WorldNameKind.State,
                        Role: WorldNameRole.Names
                    )
                ));

                return;
            }

            m_stack.Add(item: type);

            switch (typeInfo.Kind) {
                case JsonTypeInfoKind.None when typeof(StateRow).IsAssignableFrom(type):
                case JsonTypeInfoKind.Object:
                    foreach (var property in MembersOf(typeInfo)) {
                        var childPath = ((path.Length == 0)
                            ? property.Name
                            : $"{path}.{property.Name}"
                        );

                        Record(childPath, property);
                        VisitType(
                            type: property.Type,
                            path: childPath
                        );
                    }

                    foreach (var derived in typeInfo.Arms) {
                        VisitType(
                            type: derived.Type,
                            path: $"{path}[{derived.Discriminator}]"
                        );
                    }

                    break;
                case JsonTypeInfoKind.Enumerable:
                    VisitType(
                        type: typeInfo.ElementType!,
                        path: $"{path}[]"
                    );

                    break;
                case JsonTypeInfoKind.Dictionary:
                    VisitType(
                        type: typeInfo.ElementType!,
                        path: $"{path}{{*}}"
                    );

                    break;
            }

            m_stack.RemoveAt(index: (m_stack.Count - 1));
        }

        internal static Report Run(Type root, Func<Type, WorldModelType?> shapeOf) {
            var walk = new Report(shapeOf: shapeOf);

            walk.VisitType(
                path: string.Empty,
                type: root
            );

            return walk;
        }

        /// <summary>Describes the unclassified members and the annotation that classifies each.</summary>
        /// <returns>One line per member, then the fix; <see langword="null"/> when every member is classified.</returns>
        public string? DescribeUncovered() => (UncoveredList.Count == 0)
            ? null
            : $"{UncoveredList.Count} document member(s) have no declared meaning, each shown as path (Owner.Member: Type):\n  {string.Join(separator: "\n  ", values: UncoveredList)}\n" +
                "Annotate each member in its source file: [DocumentName(WorldNameKind.<kind>, WorldNameRole.<role>)] when its text spells a name the document resolves, " +
                "or [DocumentText(\"what the text is\")] when it is literal or local text.";

        /// <summary>Renders the registry as the Markdown document <c>docs/world-name-registry.md</c> holds.</summary>
        /// <returns>The document text, LF-terminated.</returns>
        public string Render() {
            var output = new StringBuilder();

            _ = output.Append(value: "# World name registry\n\n");
            _ = output.Append(value: "Generated by `puck schema` from `WorldNameRegistry` over `WorldJsonContext`; do not hand-edit.\n");
            _ = output.Append(value: "`puck schema --check` and `puck registry --check` check every text position for a declared meaning and compare this file with the model.\n\n");
            _ = output.Append(value: "Every document field that carries a declared name, and the kind of name it carries. `[]` is a list\n");
            _ = output.Append(value: "element; `[name]` is a `$type` arm; `{tokens}` is an expression's postfix\n");
            _ = output.Append(value: "spelling; `{row}` is a reaction scalar's row form. A path ending in `…` re-enters a shape listed above it.\n");
            _ = output.Append(value: "An aliased import (`imports[].as`) qualifies every `Declares` site as `<alias>$<name>` and rewrites every\n");
            _ = output.Append(value: "other role's names of the same namespace to match; a placement and a prototype each have their own. Facet\n");
            _ = output.Append(value: "is the export list (`exports.reads`/`actions`/`bindings`) that admits a host\n");
            _ = output.Append(value: "reference through the field to an imported module's name; see `src/Puck.World.Schema/README.md`.\n\n");
            _ = output.Append(value: "## Registered fields\n\n| Path | Kind | Role | Facet | Member |\n|---|---|---|---|---|\n");

            foreach (var site in Sites) {
                _ = output.Append(value: ((site.Field.Member.Length == 0)
                    ? $"| `{site.Path}` | | | | re-enters `{TypeName(type: site.Field.Owner)}` |\n"
                    : $"| `{site.Path}` | {site.Field.Kind} | {site.Field.Role} | {((site.Field.Role == WorldNameRole.Declares)
                        ? ""
                        : site.Field.Facet.ToString())} | `{TypeName(type: site.Field.Owner)}.{site.Field.Member}` |\n"));
            }

            _ = output.Append(value: "\n## Literal text and names outside module qualification\n\n| Path | Member | Reason |\n|---|---|---|\n");

            foreach (var (path, owner, member, reason) in Excluded) {
                _ = output.Append(value: $"| `{path}` | `{TypeName(type: owner)}.{member}` | {reason} |\n");
            }

            return output.ToString();
        }
    }
}
