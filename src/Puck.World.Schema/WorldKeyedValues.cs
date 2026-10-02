using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>A keyed value's clock and key times, whatever its value's type: what the validator judges of every keyed
/// value in a document.</summary>
public interface IWorldKeyTrack {
    /// <summary>Gets the name of the clock the track reads.</summary>
    string Clock { get; }
    /// <summary>Gets the number of keys.</summary>
    int Count { get; }

    /// <summary>Returns where a key sits on the clock.</summary>
    /// <param name="index">The key's index.</param>
    /// <returns>The key's time, in the clock's span units.</returns>
    double AtOf(int index);
}
/// <summary>One keyed value a document authors: where it sits, the bindable that carries it, and its keys.</summary>
/// <param name="Path">The value's document path, in JSON member names (<c>theme.colors.ink</c>).</param>
/// <param name="Value">The bindable: a <see cref="BindableColor"/>, <see cref="BindableScalar"/>,
/// <see cref="BindableAngle"/>, <see cref="BindableDirection"/>, <see cref="BindableVector2"/> or
/// <see cref="BindableVector3"/>.</param>
/// <param name="Track">The value's keys.</param>
public readonly record struct WorldKeyedValue(string Path, object Value, IWorldKeyTrack Track);
/// <summary>One presentation scalar a document binds to a state row: where it sits, its scalar form, and the field it
/// fills (<see cref="WorldValueFields"/>).</summary>
/// <param name="Path">The value's document path, in JSON member names (<c>render.sky.layers[3].softness</c>).</param>
/// <param name="Value">The bound scalar; a <see cref="BindableAngle"/> appears as its scalar form.</param>
/// <param name="Field">The field the value fills.</param>
public readonly record struct WorldBoundValue(string Path, BindableScalar Value, WorldValueField Field);
/// <summary>
/// Finds every keyed value a document authors, wherever the document places a bindable that can carry keys
/// (<see cref="BindableColor"/>, <see cref="BindableScalar"/>, <see cref="BindableAngle"/>,
/// <see cref="BindableDirection"/>, <see cref="BindableVector2"/>, <see cref="BindableVector3"/>), so the validator judges each one's clock and times
/// without a walker per section. The walk follows the document model's generated shape
/// (<see cref="WorldModelShape"/>) and visits only members whose type can reach a bindable.
/// </summary>
public static class WorldKeyedValues {
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, string Name)[]> MemberCache = new();
    private static readonly Lazy<HashSet<Type>> Reaching = new(valueFactory: ComputeReaching);
    private static readonly Type[] Surfaces = [
        typeof(BindableAngle),
        typeof(BindableColor),
        typeof(BindableDirection),
        typeof(BindableScalar),
        typeof(BindableVector2),
        typeof(BindableVector3),
    ];

    /// <summary>Returns every keyed value a document authors, in document order.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The keyed values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldKeyedValue> Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var walk = new Walk();

        walk.Visit(
            declared: null,
            path: string.Empty,
            value: definition
        );

        return walk.Found;
    }
    /// <summary>Returns every presentation scalar a document binds to a state row in a field the document model
    /// declares (<see cref="WorldValueFields"/>), in document order.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The bound values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldBoundValue> BoundOf(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var walk = new Walk();

        walk.Visit(
            declared: null,
            path: string.Empty,
            value: definition
        );

        return walk.Bound;
    }
    /// <summary>Returns whether a model type is a bindable that can carry keys on a clock.</summary>
    /// <param name="type">The type; a nullable value type answers for its underlying type.</param>
    /// <returns><see langword="true"/> for <see cref="BindableColor"/>, <see cref="BindableScalar"/>,
    /// <see cref="BindableAngle"/>, <see cref="BindableDirection"/>, <see cref="BindableVector2"/> and
    /// <see cref="BindableVector3"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static bool IsKeyable(Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        return Surfaces.Contains(value: Underlying(type: type));
    }
    /// <summary>Returns the keys a bindable carries, or <see langword="null"/> for a literal, a binding or a value
    /// that is no bindable.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The keys, or <see langword="null"/>.</returns>
    public static IWorldKeyTrack? KeysOf(object? value) => (value switch {
        BindableColor color => color.Keys,
        BindableScalar scalar => scalar.Keys,
        BindableAngle angle => angle.Value.Keys,
        BindableDirection direction => direction.Keys,
        BindableVector2 vector => vector.Keys,
        BindableVector3 vector => vector.Keys,
        _ => null,
    });

    private static HashSet<Type> ComputeReaching() {
        var reaching = new HashSet<Type>(collection: Surfaces);
        var grew = true;

        while (grew) {
            grew = false;

            foreach (var shape in WorldModelShape.Types) {
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
        if ((shape.ElementType is { } element) && reaching.Contains(item: Underlying(type: element))) {
            return true;
        }

        foreach (var arm in shape.Arms) {
            if (reaching.Contains(item: arm.Type)) {
                return true;
            }
        }

        foreach (var member in Walked(shape: shape)) {
            if (IsWalked(member: member) && reaching.Contains(item: Underlying(type: member.Type))) {
                return true;
            }
        }

        return false;
    }
    private static IReadOnlyList<WorldModelMember> Walked(WorldModelType shape) => ((shape.Kind == JsonTypeInfoKind.Object)
        ? shape.Members
        : shape.Properties
    );
    private static bool IsWalked(WorldModelMember member) => (
        member.Access.HasFlag(flag: WorldModelAccess.Read) &&
        !member.Access.HasFlag(flag: WorldModelAccess.ExtensionData)
    );
    private static (PropertyInfo Property, string Name)[] MembersOf(WorldModelType shape) => MemberCache.GetOrAdd(
        factoryArgument: shape,
        key: shape.Type,
        valueFactory: static (_, shape) => [.. Walked(shape: shape)
            .Where(predicate: static member => (IsWalked(member: member) && Reaching.Value.Contains(item: Underlying(type: member.Type))))
            .Select(selector: static member => (Property: member.DeclaringType.GetProperty(
                bindingAttr: BindingFlags.Instance | BindingFlags.Public,
                name: member.Member
            ), member.Name))
            .Where(predicate: static pair => ((pair.Property is not null) && (pair.Property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })))
            .Select(selector: static pair => (pair.Property!, pair.Name))]
    );
    private static Type Underlying(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);

    private sealed class Walk {
        private readonly HashSet<object> m_visited = new(comparer: ReferenceEqualityComparer.Instance);

        public List<WorldBoundValue> Bound { get; } = [];
        public List<WorldKeyedValue> Found { get; } = [];

        // The field is the one the member holding the value declares, or null for a value no member declares (a
        // dictionary's or a list's element).
        public void Visit(object? value, string path, WorldModelType? declared, WorldValueField? field = null) {
            if ((value is null) || (value is string)) {
                return;
            }

            if (Surfaces.Contains(value: value.GetType())) {
                if (KeysOf(value: value) is { } track) {
                    Found.Add(item: new WorldKeyedValue(
                        Path: path,
                        Track: track,
                        Value: value
                    ));
                }

                var scalar = (value switch {
                    BindableScalar bindable => bindable,
                    BindableAngle angle => angle.Value,
                    _ => ((BindableScalar?)null),
                });

                if ((field is not null) && (scalar is { State: not null } bound)) {
                    Bound.Add(item: new WorldBoundValue(
                        Field: field,
                        Path: path,
                        Value: bound
                    ));
                }

                return;
            }

            var type = value.GetType();

            if (!type.IsValueType && !m_visited.Add(item: value)) {
                return;
            }

            if ((WorldModelShape.Of(type: type) ?? declared) is not { } shape) {
                return;
            }

            switch (shape.Kind) {
                case JsonTypeInfoKind.Enumerable:
                    if ((shape.ElementType is { } element) && Reaching.Value.Contains(item: Underlying(type: element)) && (value is IEnumerable items)) {
                        var elementShape = WorldModelShape.Of(type: element);
                        var index = 0;

                        foreach (var item in items) {
                            Visit(
                                declared: elementShape,
                                path: $"{path}[{index}]",
                                value: item
                            );
                            index++;
                        }
                    }

                    break;
                case JsonTypeInfoKind.Dictionary:
                    if ((shape.ElementType is { } valueType) && Reaching.Value.Contains(item: Underlying(type: valueType)) && (value is IEnumerable pairs)) {
                        var valueShape = WorldModelShape.Of(type: valueType);

                        foreach (var pair in pairs) {
                            if (pair is null) {
                                continue;
                            }

                            var pairType = pair.GetType();
                            var key = pairType.GetProperty(name: "Key")?.GetValue(obj: pair);

                            Visit(
                                declared: valueShape,
                                path: $"{path}.{key}",
                                value: pairType.GetProperty(name: "Value")?.GetValue(obj: pair)
                            );
                        }
                    }

                    break;
                case JsonTypeInfoKind.Object:
                case JsonTypeInfoKind.None:
                    foreach (var (property, name) in MembersOf(shape: shape)) {
                        Visit(
                            declared: WorldModelShape.Of(type: property.PropertyType),
                            field: WorldValueFields.Of(
                                member: property.Name,
                                owner: property.DeclaringType!
                            ),
                            path: ((path.Length == 0)
                                ? name
                                : $"{path}.{name}"),
                            value: property.GetValue(obj: value)
                        );
                    }

                    break;
            }
        }
    }
}
