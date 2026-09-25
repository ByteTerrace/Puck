using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>One state read the presentation manifest records: a parsed binding and how its reader converts the cell.
/// It is the key a <see cref="WorldStateMirror"/> slot is found by.</summary>
/// <param name="Binding">The parsed binding.</param>
/// <param name="Conversion">How the reader converts the cell.</param>
public readonly record struct WorldPresentationBinding(StateBinding Binding, WorldStateConversion Conversion);
/// <summary>
/// The presentation manifest: every state read a document's presentation makes, compiled once from the document and
/// deduplicated. <see cref="Bindings"/> are the reads that last as long as the document, which a
/// <see cref="WorldStateMirror"/> registers when it installs the document: a HUD element's binding or template
/// placeholder, an overlay <c>state</c> predicate, a binding bar's layout and model cells, every bindable scalar and
/// color (camera program operands, markers, render lighting, sky and environment colors, the theme), and a render
/// cycle's position row. <see cref="BodyBindings"/> are templates a body reads through its own
/// <c>WorldStateLease</c>: the population's scale row, a look's pose references and lane operands, a creation
/// driver's state signal and gate tokens, and an effector's state target. A template keeps its
/// <see cref="StateBinding.BodyKey"/> key as authored; a body's lease resolves that key to the body's index when it
/// acquires the template, so every body reads its own slot of one table, and the slot is released when the body leaves.
/// <para>
/// A surface is found by the type that carries it, wherever the document places that type, so a section that gains a
/// bindable member is covered without a new walker. The walk follows the world document model's generated shape
/// (<see cref="WorldModelShape"/>) and visits only members whose type can reach a surface, so a document's state rows,
/// rules and literal-only sections are never read.
/// </para>
/// </summary>
public sealed class WorldPresentationManifest {
    /// <summary>The manifest of a document that binds nothing.</summary>
    public static readonly WorldPresentationManifest Empty = new(
        bindings: [],
        bodyBindings: []
    );

    private static readonly ConditionalWeakTable<WorldDefinition, WorldPresentationManifest> Compiled = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> MemberCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> PairValueCache = new();
    private static readonly Lazy<HashSet<Type>> Reaching = new(valueFactory: ComputeReaching);
    private static readonly Type[] Surfaces = [
        typeof(BindableColor),
        typeof(BindableScalar),
        typeof(CreationDriverDocument),
        typeof(CreationEffectorTargetDocument),
        typeof(OverlayPredicate.State),
        typeof(WorldBindingBarAuthoring),
        typeof(WorldHudElement),
        typeof(WorldLookMotion),
        typeof(WorldRenderCycle),
    ];

    private readonly WorldPresentationBinding[] m_bindings;
    private readonly WorldPresentationBinding[] m_bodyBindings;

    private WorldPresentationManifest(WorldPresentationBinding[] bindings, WorldPresentationBinding[] bodyBindings) {
        m_bindings = bindings;
        m_bodyBindings = bodyBindings;
    }

    /// <summary>Gets the reads that last as long as the document, each once, in document order.</summary>
    public ReadOnlySpan<WorldPresentationBinding> Bindings => m_bindings;
    /// <summary>Gets the templates a body reads through its lease, each once, in document order; a template's key may
    /// be <see cref="StateBinding.BodyKey"/>.</summary>
    public ReadOnlySpan<WorldPresentationBinding> BodyBindings => m_bodyBindings;

    /// <summary>Returns a document's manifest, compiling it on the first request for that document instance and
    /// answering later requests for the same instance from a table that does not keep the document alive.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The document's manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationManifest Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Compiled.GetValue(
            createValueCallback: static document => Compile(definition: document),
            key: definition
        );
    }
    /// <summary>Compiles a document's manifest: the deduplicated union of the state bindings its presentation
    /// sections author.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldPresentationManifest Compile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var builder = new Builder();

        if (definition.Population.ScaleRow is { } scaleRow) {
            builder.AddBody(binding: new StateBinding(
                Key: StateBinding.BodyKey,
                Row: scaleRow,
                Target: false
            ));
        }

        builder.Visit(value: definition);

        return (((builder.Bindings.Count == 0) && (builder.BodyBindings.Count == 0))
            ? Empty
            : new WorldPresentationManifest(
                bindings: [.. builder.Bindings],
                bodyBindings: [.. builder.BodyBindings]
            )
        );
    }

    // Every model type from which a surface type is reachable through a readable member, an element type, or a
    // polymorphic arm: the fixed point over the generated shape table, computed once.
    private static HashSet<Type> ComputeReaching() {
        var reaching = new HashSet<Type>(collection: Surfaces);
        var types = WorldModelShape.Types;
        var grew = true;

        while (grew) {
            grew = false;

            foreach (var shape in types) {
                if (
                    !reaching.Contains(item: shape.Type) &&
                    Reaches(
                    reaching: reaching,
                    shape: shape
                )
                ) {
                    _ = reaching.Add(item: shape.Type);
                    grew = true;
                }
            }
        }

        return reaching;
    }
    private static bool Reaches(WorldModelType shape, HashSet<Type> reaching) {
        if (
            (shape.ElementType is { } element) &&
            reaching.Contains(item: Underlying(type: element))
        ) {
            return true;
        }

        foreach (var arm in shape.Arms) {
            if (reaching.Contains(item: arm.Type)) {
                return true;
            }
        }
        foreach (var member in Walked(shape: shape)) {
            if (
                IsWalked(member: member) &&
                reaching.Contains(item: Underlying(type: member.Type))
            ) {
                return true;
            }
        }

        return false;
    }
    // A serializer-described object carries its members; a converter-backed one (a creation document) carries its
    // public properties instead, which hold the same document data.
    private static IReadOnlyList<WorldModelMember> Walked(WorldModelType shape) => ((shape.Kind == JsonTypeInfoKind.Object)
        ? shape.Members
        : shape.Properties
    );
    private static bool IsWalked(WorldModelMember member) => (
        member.Access.HasFlag(flag: WorldModelAccess.Read) &&
        !member.Access.HasFlag(flag: WorldModelAccess.ExtensionData)
    );
    // An unconditionally ignored property is derived from the document rather than part of it.
    private static bool IsDocumentData(PropertyInfo property) =>
        (property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always });
    private static PropertyInfo[] MembersOf(WorldModelType shape) => MemberCache.GetOrAdd(
        factoryArgument: shape,
        key: shape.Type,
        valueFactory: static (_, shape) => [.. Walked(shape: shape)
            .Where(predicate: member => (IsWalked(member: member) && Reaching.Value.Contains(item: Underlying(type: member.Type))))
            .Select(selector: static member => member.DeclaringType.GetProperty(
                bindingAttr: BindingFlags.Instance | BindingFlags.Public,
                name: member.Member
            ))
            .OfType<PropertyInfo>()
            .Where(predicate: IsDocumentData)]
    );
    private static Type Underlying(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);

    private sealed class Builder {
        private readonly HashSet<WorldPresentationBinding> m_bodySeen = [];
        private readonly HashSet<WorldPresentationBinding> m_seen = [];
        private readonly HashSet<object> m_visited = new(comparer: ReferenceEqualityComparer.Instance);

        public List<WorldPresentationBinding> Bindings { get; } = [];
        public List<WorldPresentationBinding> BodyBindings { get; } = [];

        public void Add(StateBinding? binding, WorldStateConversion conversion) {
            if (
                (binding is { } bound) &&
                m_seen.Add(item: new WorldPresentationBinding(
                    Binding: bound,
                    Conversion: conversion
                ))
            ) {
                Bindings.Add(item: new WorldPresentationBinding(
                    Binding: bound,
                    Conversion: conversion
                ));
            }
        }
        // A lease reads every template as a number.
        public void AddBody(StateBinding binding) {
            var entry = new WorldPresentationBinding(
                Binding: binding,
                Conversion: WorldStateConversion.Number
            );

            if (m_bodySeen.Add(item: entry)) {
                BodyBindings.Add(item: entry);
            }
        }
        // A lease reads a token with the truth when its reader asks for it, and whenever the token spells .$target.
        public void AddBodyToken(string? token, bool truth) {
            if (StateBinding.TryParse(
                binding: out var binding,
                token: token
            )) {
                AddBody(binding: (binding with { Target = (truth || binding.Target) }));
            }
        }
        public void Visit(object? value, WorldModelType? declared = null) {
            if (
                (value is null) ||
                (value is string)
            ) {
                return;
            }

            if (!Collect(value: value)) {
                return;
            }

            var type = value.GetType();

            if (
                !type.IsValueType &&
                !m_visited.Add(item: value)
            ) {
                return;
            }

            if ((WorldModelShape.Of(type: type) ?? declared) is not { } shape) {
                return;
            }

            switch (shape.Kind) {
                case JsonTypeInfoKind.Enumerable:
                case JsonTypeInfoKind.Dictionary:
                    if (
                        (shape.ElementType is { } element) &&
                        Reaching.Value.Contains(item: Underlying(type: element)) &&
                        (value is IEnumerable items)
                    ) {
                        var elementShape = WorldModelShape.Of(type: element);

                        foreach (var item in items) {
                            Visit(
                                declared: elementShape,
                                value: PairValue(item: item)
                            );
                        }
                    }

                    break;
                case JsonTypeInfoKind.Object:
                case JsonTypeInfoKind.None:
                    foreach (var property in MembersOf(shape: shape)) {
                        Visit(
                            declared: WorldModelShape.Of(type: property.PropertyType),
                            value: property.GetValue(obj: value)
                        );
                    }

                    break;
            }
        }

        private static object? PairValue(object? item) {
            if (item is null) {
                return null;
            }

            var type = item.GetType();

            if (
                !type.IsGenericType ||
                (type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
            ) {
                return item;
            }

            return PairValueCache.GetOrAdd(
                key: type,
                valueFactory: static type => type.GetProperty(name: "Value")
            )?.GetValue(obj: item);
        }
        // Records the bindings a surface carries; returns whether the walk continues into its members.
        private bool Collect(object value) {
            switch (value) {
                case BindableScalar scalar:
                    Add(
                        binding: scalar.State,
                        conversion: WorldStateConversion.Number
                    );

                    return false;
                case BindableColor color:
                    Add(
                        binding: color.State,
                        conversion: WorldStateConversion.Color
                    );

                    return false;
                case OverlayPredicate.State state:
                    Add(
                        binding: StateBinding.Parse(token: state.Binding),
                        conversion: WorldStateConversion.Number
                    );

                    return false;
                case WorldHudElement element:
                    Add(
                        binding: StateBinding.Parse(token: element.Binding),
                        conversion: WorldStateConversion.Number
                    );

                    if (
                        (element.Template is { Length: > 0 } template) &&
                        HudTemplate.TryEnumeratePlaceholders(
                        error: out _,
                        placeholders: out var placeholders,
                        template: template
                    )
                    ) {
                        foreach (var placeholder in placeholders) {
                            Add(
                                binding: StateBinding.Parse(token: placeholder),
                                conversion: WorldStateConversion.Number
                            );
                        }
                    }

                    return true;
                case WorldBindingBarAuthoring bar:
                    Add(
                        binding: StateBinding.Parse(token: bar.LayoutCell),
                        conversion: WorldStateConversion.Number
                    );
                    Add(
                        binding: StateBinding.Parse(token: bar.ModelCell),
                        conversion: WorldStateConversion.Number
                    );

                    return true;
                case WorldRenderCycle cycle:
                    // The cycle's position is its row's stored truth, read only once the cycle has two keys.
                    if (cycle.Keys is { Count: >= 2 }) {
                        Add(
                            binding: new StateBinding(
                                Key: null,
                                Row: cycle.State,
                                Target: true
                            ),
                            conversion: WorldStateConversion.Number
                        );
                    }

                    return true;
                case WorldLookMotion motion:
                    if (motion.Poses is { } poses) {
                        foreach (var reference in poses.Values) {
                            AddBodyToken(
                                token: reference,
                                truth: true
                            );
                        }
                    }
                    if (motion.Lanes is { } lanes) {
                        foreach (var lane in lanes) {
                            if (lane is null) {
                                continue;
                            }

                            foreach (var instruction in lane.Instructions) {
                                if (instruction.Payload is InstructionPayload.State operand) {
                                    AddBody(binding: new StateBinding(
                                        Key: operand.Key?.Spelling,
                                        Row: operand.Name.Spelling,
                                        Target: false
                                    ));
                                }
                            }
                        }
                    }

                    return true;
                case CreationDriverDocument driver:
                    if (CreationDriverDocument.IsStateSignal(signal: driver.Signal)) {
                        AddBodyToken(
                            token: driver.Signal,
                            truth: false
                        );
                    }

                    foreach (var token in (driver.When ?? [])) {
                        if (CreationDriverDocument.IsStateSignal(signal: token)) {
                            AddBodyToken(
                                token: token,
                                truth: true
                            );
                        }
                    }

                    return false;
                case CreationEffectorTargetDocument target:
                    if (string.Equals(
                        a: target.Kind,
                        b: CreationEffectorTargetDocument.KindState,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        AddBodyToken(
                            token: target.Reference,
                            truth: false
                        );
                    }

                    return false;
                default:
                    return true;
            }
        }
    }
}
