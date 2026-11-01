using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>What a <see cref="WorldDocumentWalker"/> does at each value it reaches.</summary>
public interface IWorldDocumentVisitor {
    /// <summary>Reads a value the walk reached, before the walk enters its members.</summary>
    /// <param name="value">The value: never <see langword="null"/> or a string.</param>
    /// <param name="site">Where the value sits.</param>
    /// <returns>Whether the walk continues into the value's members; <see cref="Leave"/> follows when it does.</returns>
    bool Enter(object value, WorldDocumentSite site);
    /// <summary>Ends a value <see cref="Enter"/> admitted, after the walk has visited its members.</summary>
    /// <param name="value">The value.</param>
    void Leave(object value) {
    }
}
/// <summary>Where a <see cref="WorldDocumentWalker"/> reached a value: its document path, formatted only when asked, and
/// the scalar field the member holding it declares.</summary>
public readonly struct WorldDocumentSite {
    private readonly WorldDocumentWalker.Cursor? m_walk;

    internal WorldDocumentSite(WorldDocumentWalker.Cursor walk, WorldValueField? field) {
        m_walk = walk;
        Field = field;
    }

    /// <summary>Gets the field the member holding the value declares (<see cref="WorldValueFields"/>), or
    /// <see langword="null"/> for a value no member declares (the document itself, a list's or a dictionary's
    /// element).</summary>
    public WorldValueField? Field { get; }
    /// <summary>Gets the value's document path, in JSON member names (<c>render.sky.layers[3].softness</c>); empty
    /// for the document itself.</summary>
    public string Path => (m_walk?.FormatPath() ?? string.Empty);
}
/// <summary>
/// The one walk of the world document model that finds the values a consumer reads, wherever the document places
/// them. A walker is built over a set of surface types; it follows the model's generated shape
/// (<see cref="WorldModelShape"/>) and enters only members whose type can reach a surface, so a section that gains a
/// surface is covered without a new walker and literal-only sections are never read. Each consumer passes its own
/// <see cref="IWorldDocumentVisitor"/>, which decides what a reached value records and whether the walk continues
/// into its members.
/// <para>
/// Everything the walk derives from a type (whether it reaches a surface, which of its members to read, their readers,
/// declared shapes and fields) is computed once per type and walker; a walk reads values through the model's generated
/// readers and formats a path only when a visitor asks for one. The visitor sees every value it reaches, a shared one
/// each time; the walk enters a reference value's members once.
/// </para>
/// </summary>
public sealed class WorldDocumentWalker {
    private readonly ConcurrentDictionary<Type, Member[]> m_members = new();
    private readonly Lazy<HashSet<Type>> m_reaching;
    private readonly Type[] m_surfaces;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo? Key, PropertyInfo? Value)> PairCache = new();

    /// <summary>Initializes a new instance of the <see cref="WorldDocumentWalker"/> class over the types a consumer
    /// reads.</summary>
    /// <param name="surfaces">The surface types; a walk reaches every value of these types the document holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surfaces"/> is <see langword="null"/>.</exception>
    public WorldDocumentWalker(IEnumerable<Type> surfaces) {
        ArgumentNullException.ThrowIfNull(argument: surfaces);

        m_surfaces = [.. surfaces];
        m_reaching = new Lazy<HashSet<Type>>(valueFactory: ComputeReaching);
    }

    /// <summary>Walks a document, or any model value, in document order.</summary>
    /// <param name="root">The value the walk starts at.</param>
    /// <param name="visitor">What the walk does at each value it reaches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="visitor"/> is
    /// <see langword="null"/>.</exception>
    public void Walk(object root, IWorldDocumentVisitor visitor) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: visitor);

        new Cursor(visitor: visitor, walker: this).Visit(
            declared: null,
            field: null,
            value: root
        );
    }

    // Every model type from which a surface type is reachable through a readable member, an element type, or a
    // polymorphic arm: the fixed point over the generated shape table, computed once per walker.
    private HashSet<Type> ComputeReaching() {
        var reaching = new HashSet<Type>(collection: m_surfaces);
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
    private bool IsReaching(Type type) => m_reaching.Value.Contains(item: Underlying(type: type));
    private Member[] MembersOf(WorldModelType shape) => m_members.GetOrAdd(
        factoryArgument: (Shape: shape, Walker: this),
        key: shape.Type,
        valueFactory: static (_, state) => [.. Walked(shape: state.Shape)
            .Where(predicate: member => (IsWalked(member: member) && state.Walker.IsReaching(type: member.Type)))
            .Select(selector: static member => (Model: member, Property: member.DeclaringType.GetProperty(
                bindingAttr: BindingFlags.Instance | BindingFlags.Public,
                name: member.Member
            )))
            .Where(predicate: static pair => ((pair.Property is not null) && IsDocumentData(property: pair.Property)))
            .Select(selector: static pair => new Member(
                Declared: WorldModelShape.Of(type: pair.Property!.PropertyType),
                Field: WorldValueFields.Of(
                    member: pair.Property.Name,
                    owner: pair.Property.DeclaringType!
                ),
                Name: pair.Model.Name,
                Read: (pair.Model.Get ?? pair.Property.GetValue)
            ))]
    );
    private static (PropertyInfo? Key, PropertyInfo? Value) PairOf(Type type) => PairCache.GetOrAdd(
        key: type,
        valueFactory: static type => ((type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)))
            ? (type.GetProperty(name: "Key"), type.GetProperty(name: "Value"))
            : (null, null))
    );
    private static Type Underlying(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);

    // One member a walk reads: its reader, the shape its declared type has, the field it declares, its JSON name.
    private sealed record Member(Func<object, object?> Read, WorldModelType? Declared, WorldValueField? Field, string Name);
    // One step of the path to the value under visit: a member's JSON name, a list's index, or a dictionary's key.
    private readonly record struct Segment(string? Name, int Index, object? Key);

    internal sealed class Cursor(WorldDocumentWalker walker, IWorldDocumentVisitor visitor) {
        private readonly List<Segment> m_path = [];
        private readonly HashSet<object> m_visited = new(comparer: ReferenceEqualityComparer.Instance);

        public string FormatPath() {
            if (m_path.Count == 0) {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (var segment in m_path) {
                if (segment.Name is { } name) {
                    if (builder.Length > 0) {
                        _ = builder.Append(value: '.');
                    }

                    _ = builder.Append(value: name);
                } else if (segment.Key is { } key) {
                    _ = builder.Append(value: '.').Append(value: Convert.ToString(provider: CultureInfo.InvariantCulture, value: key));
                } else {
                    _ = builder.Append(value: '[').Append(value: segment.Index).Append(value: ']');
                }
            }

            return builder.ToString();
        }
        // The field is the one the member holding the value declares, or null for a value no member declares.
        public void Visit(object? value, WorldModelType? declared, WorldValueField? field) {
            if (
                (value is null) ||
                (value is string)
            ) {
                return;
            }
            if (!visitor.Enter(
                site: new WorldDocumentSite(
                    field: field,
                    walk: this
                ),
                value: value
            )) {
                return;
            }

            var type = value.GetType();

            if (
                (type.IsValueType || m_visited.Add(item: value)) &&
                ((WorldModelShape.Of(type: type) ?? declared) is { } shape)
            ) {
                VisitMembers(
                    shape: shape,
                    value: value
                );
            }

            visitor.Leave(value: value);
        }

        private void VisitMembers(object value, WorldModelType shape) {
            switch (shape.Kind) {
                case JsonTypeInfoKind.Enumerable:
                case JsonTypeInfoKind.Dictionary:
                    if (
                        (shape.ElementType is { } element) &&
                        walker.IsReaching(type: element) &&
                        (value is IEnumerable items)
                    ) {
                        var elementShape = WorldModelShape.Of(type: element);
                        var index = 0;

                        foreach (var item in items) {
                            var (keyProperty, valueProperty) = ((item is null)
                                ? (null, null)
                                : PairOf(type: item.GetType()));

                            m_path.Add(item: ((valueProperty is null)
                                ? new Segment(Index: index, Key: null, Name: null)
                                : new Segment(Index: index, Key: (keyProperty?.GetValue(obj: item) ?? string.Empty), Name: null)));
                            Visit(
                                declared: elementShape,
                                field: null,
                                value: ((valueProperty is null)
                                    ? item
                                    : valueProperty.GetValue(obj: item))
                            );
                            m_path.RemoveAt(index: (m_path.Count - 1));
                            index++;
                        }
                    }

                    break;
                case JsonTypeInfoKind.Object:
                case JsonTypeInfoKind.None:
                    foreach (var member in walker.MembersOf(shape: shape)) {
                        m_path.Add(item: new Segment(Index: 0, Key: null, Name: member.Name));
                        Visit(
                            declared: member.Declared,
                            field: member.Field,
                            value: member.Read(arg: value)
                        );
                        m_path.RemoveAt(index: (m_path.Count - 1));
                    }

                    break;
            }
        }
    }
}
