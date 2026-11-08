using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions;

namespace Puck.World;

/// <summary>What a <see cref="WorldDocumentWalker"/> does at each value it reaches.</summary>
public interface IWorldDocumentVisitor {
    /// <summary>Gets whether the visitor has what it came for: the walk reads this after each value it visits and
    /// unwinds, ending each value it entered with <see cref="Leave"/>, once it is <see langword="true"/>.</summary>
    bool Stopped => false;

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
/// <summary>What a <see cref="WorldDocumentWalker.Rewrite{T}"/> does at each value it reaches.</summary>
public interface IWorldDocumentRewriter {
    /// <summary>Gets whether the rewriter has refused: the walk reads this after each value it rewrites and unwinds
    /// once it is <see langword="true"/>, and the rewrite answers its root unchanged.</summary>
    bool Stopped => false;

    /// <summary>Returns whether the walk enters the members of a value that is not a surface.</summary>
    /// <param name="value">The value: never <see langword="null"/>, a string or a surface.</param>
    /// <param name="site">Where the value sits.</param>
    /// <returns><see langword="true"/> to rewrite what the value holds; <see langword="false"/> keeps it whole.</returns>
    bool Enters(object value, WorldDocumentSite site) => true;
    /// <summary>Ends a value <see cref="Enters"/> admitted, after the walk has rewritten its members.</summary>
    /// <param name="value">The value the walk entered.</param>
    /// <param name="rewritten">What the site holds afterwards: <paramref name="value"/> itself when nothing under it
    /// was replaced.</param>
    void Left(object value, object rewritten) {
    }
    /// <summary>Returns what the site holds in place of a surface value the walk reached.</summary>
    /// <param name="value">The surface value.</param>
    /// <param name="site">Where the value sits.</param>
    /// <returns>The value itself to keep it, or its replacement, which the member holding it must admit.</returns>
    object Replace(object value, WorldDocumentSite site);
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
/// them. A walker is built over the surface types a consumer reads; it follows the model's generated shape
/// (<see cref="WorldModelShape"/>) and enters only members whose type can reach a surface, so a section that gains a
/// surface is covered without a new walker and literal-only sections are never read. Each consumer passes its own
/// <see cref="IWorldDocumentVisitor"/>, which decides what a reached value records and whether the walk continues
/// into its members. A value the model does not describe (a projection's or a mutation's wrapper, a caller's own
/// graph, a list at the root) is followed through its public properties and element type instead, so any object
/// graph walks the same way.
/// <para>
/// Everything the walk derives from a type (whether it reaches a surface, which of its members to read, their readers,
/// declared shapes and fields) is computed once per type, walker and <see cref="CodeRevision.Current"/>, and derived
/// again once an applied code update moves the revision; a walk reads values through the model's generated readers
/// and formats a path only when a visitor asks for one. The visitor sees every value it reaches, a shared one each
/// time it is reached; the walk enters a reference value's members unless that value is already being walked above
/// it, which is a cycle, so a value shared by two owners is walked under each.
/// </para>
/// </summary>
public sealed class WorldDocumentWalker {
    private readonly Func<Type, bool> m_isSurface;
    private Tables m_tables;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo? Key, PropertyInfo? Value)> PairCache = new();

    /// <summary>Initializes a new instance of the <see cref="WorldDocumentWalker"/> class over the types a consumer
    /// reads.</summary>
    /// <param name="surfaces">The surface types; a walk reaches every value of these types the document holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surfaces"/> is <see langword="null"/>.</exception>
    public WorldDocumentWalker(IEnumerable<Type> surfaces) : this(isSurface: Contains(surfaces: surfaces)) {
    }
    /// <summary>Initializes a new instance of the <see cref="WorldDocumentWalker"/> class over every type a predicate
    /// admits, for a consumer whose surface is an interface or a family of types.</summary>
    /// <param name="isSurface">Whether a type is a surface.</param>
    /// <exception cref="ArgumentNullException"><paramref name="isSurface"/> is <see langword="null"/>.</exception>
    public WorldDocumentWalker(Func<Type, bool> isSurface) {
        ArgumentNullException.ThrowIfNull(argument: isSurface);

        m_isSurface = isSurface;
        m_tables = new Tables(
            revision: CodeRevision.Current,
            walker: this
        );
    }

    /// <summary>Walks a document, or any model value or object graph, in document order.</summary>
    /// <param name="root">The value the walk starts at.</param>
    /// <param name="visitor">What the walk does at each value it reaches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="visitor"/> is
    /// <see langword="null"/>.</exception>
    public void Walk(object root, IWorldDocumentVisitor visitor) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: visitor);

        var tables = m_tables;

        // Code the runtime replaced derived nothing a walk reads again: the revision moves, the tables are derived anew.
        if (tables.Revision != CodeRevision.Current) {
            tables = new Tables(
                revision: CodeRevision.Current,
                walker: this
            );
            m_tables = tables;
        }

        new Cursor(
            tables: tables,
            visitor: visitor
        ).Visit(
            declared: null,
            field: null,
            value: root
        );
    }
    /// <summary>Returns a document, or any model value or object graph, with each surface value the rewriter replaces
    /// put in its place: the values on the path from the root to a replacement are copied as a record's
    /// <c>with</c> copies them (a value type's box is copied, a list or dictionary rebuilt), and every value off those
    /// paths is the root's own, so a reference-keyed product derived from one stays valid for the other.</summary>
    /// <typeparam name="T">The root's type.</typeparam>
    /// <param name="root">The value the rewrite starts at; it and everything it holds are left as they are.</param>
    /// <param name="rewriter">What the rewrite does at each value it reaches.</param>
    /// <returns>The rewritten root, or <paramref name="root"/> itself when nothing was replaced or the rewriter
    /// stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="rewriter"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">A value on a path to a replacement cannot be copied: a class that is
    /// not a record, a member with no setter, or a collection a member's declared type cannot hold rebuilt. The
    /// message names its document path.</exception>
    public T Rewrite<T>(T root, IWorldDocumentRewriter rewriter) where T : class {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: rewriter);

        var tables = m_tables;

        if (tables.Revision != CodeRevision.Current) {
            tables = new Tables(
                revision: CodeRevision.Current,
                walker: this
            );
            m_tables = tables;
        }

        var rewritten = new Cursor(
            rewriter: rewriter,
            tables: tables
        ).Rewrite(
            declared: null,
            field: null,
            value: root
        );

        return (rewriter.Stopped ? root : (T)rewritten!);
    }

    private static Func<Type, bool> Contains(IEnumerable<Type> surfaces) {
        ArgumentNullException.ThrowIfNull(argument: surfaces);

        var types = new HashSet<Type>(collection: surfaces);

        return types.Contains;
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
    private static bool IsLeaf(Type type) =>
        (type.IsPrimitive ||
        type.IsEnum ||
        type.IsPointer ||
        type.IsByRefLike ||
        (type == typeof(string)) ||
        (type == typeof(decimal)) ||
        (type == typeof(DateTime)) ||
        (type == typeof(DateTimeOffset)) ||
        (type == typeof(Guid)) ||
        (type == typeof(JsonElement)));
    private static (PropertyInfo? Key, PropertyInfo? Value) PairOf(Type type) => PairCache.GetOrAdd(
        key: type,
        valueFactory: static type => ((type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)))
            ? (type.GetProperty(name: "Key"), type.GetProperty(name: "Value"))
            : (null, null))
    );
    private static Type Underlying(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);

    // One member a walk reads: its reader, the shape its declared type has, the field it declares, its JSON name, and the
    // property a rewrite sets on a copy of its owner.
    internal sealed record Member(Func<object, object?> Read, WorldModelType? Declared, WorldValueField? Field, string Name, PropertyInfo Property);
    // One step of the path to the value under visit: a member's JSON name, a list's index, or a dictionary's key.
    internal readonly record struct Segment(string? Name, int Index, object? Key);

    // Everything a walk derives from a type, for one code revision: a walk holds one generation from start to end, and
    // the next walk after an applied code update holds a new one.
    internal sealed class Tables(WorldDocumentWalker walker, int revision) {
        private readonly ConcurrentDictionary<Type, Member[]> m_members = new();
        private readonly Lazy<HashSet<Type>> m_reaching = new(valueFactory: () => ComputeReaching(isSurface: walker.m_isSurface));
        private readonly ConcurrentDictionary<Type, bool> m_reaches = new();
        private readonly ConcurrentDictionary<Type, WorldModelType?> m_shapes = new();

        public int Revision => revision;

        // The model's shape of a type, or the shape its public properties and element type give a type the model does
        // not describe; null for a value no member of which is document data.
        public WorldModelType? ShapeOf(Type type) => (WorldModelShape.Of(type: type) ?? m_shapes.GetOrAdd(
            key: type,
            valueFactory: Describe
        ));
        // Whether a surface is reachable from a value of the type: the fixed point over the generated table for a
        // model type, and for any other type its sealed structure, an open type reaching conservatively.
        public bool IsReaching(Type type) => m_reaches.GetOrAdd(
            key: Underlying(type: type),
            valueFactory: candidate => Reaches(
                path: [],
                type: candidate
            )
        );
        public Member[] MembersOf(WorldModelType shape) => m_members.GetOrAdd(
            factoryArgument: (Shape: shape, Tables: this),
            key: shape.Type,
            valueFactory: static (_, state) => [.. Walked(shape: state.Shape)
                .Where(predicate: member => (IsWalked(member: member) && state.Tables.IsReaching(type: member.Type)))
                .Select(selector: static member => (Model: member, Property: member.DeclaringType.GetProperty(
                    bindingAttr: BindingFlags.Instance | BindingFlags.Public,
                    name: member.Member
                )))
                .Where(predicate: static pair => ((pair.Property is not null) && IsDocumentData(property: pair.Property)))
                .Select(selector: pair => new Member(
                    Declared: state.Tables.ShapeOf(type: pair.Property!.PropertyType),
                    Field: WorldValueFields.Of(
                        member: pair.Property.Name,
                        owner: pair.Property.DeclaringType!
                    ),
                    Name: pair.Model.Name,
                    Property: pair.Property,
                    Read: (pair.Model.Get ?? pair.Property.GetValue)
                ))]
        );
        public bool IsSurface(Type type) => walker.m_isSurface(arg: type);
        // A copy of a value a rewrite changes a member of: a record's own clone (its copy constructor, as `with`
        // runs it), or a value type's box copied; null for any other class, whose copy would be a guess.
        public Func<object, object>? CopierOf(Type type) => m_copiers.GetOrAdd(key: type, valueFactory: static type => {
            if (type.IsValueType) {
                return BoxCopy;
            }
            if (type.GetMethod(name: "<Clone>$", bindingAttr: (BindingFlags.Instance | BindingFlags.Public), types: Type.EmptyTypes) is not { } clone) {
                return null;
            }

            var value = Expression.Parameter(
                name: "value",
                type: typeof(object)
            );

            return Expression.Lambda<Func<object, object>>(
                Expression.Convert(
                    expression: Expression.Call(
                        instance: Expression.Convert(expression: value, type: type),
                        method: clone
                    ),
                    type: typeof(object)
                ),
                value
            ).Compile();
        });

        private readonly ConcurrentDictionary<Type, Func<object, object>?> m_copiers = new();
        private static readonly Func<object, object> BoxCopy = (Func<object, object>)Delegate.CreateDelegate(
            method: typeof(object).GetMethod(name: "MemberwiseClone", bindingAttr: (BindingFlags.Instance | BindingFlags.NonPublic))!,
            type: typeof(Func<object, object>)
        );

        // Every model type from which a surface type is reachable through a readable member, an element type, or a
        // polymorphic arm: the fixed point over the generated shape table.
        private static HashSet<Type> ComputeReaching(Func<Type, bool> isSurface) {
            var reaching = new HashSet<Type>(collection: WorldModelShape.Types
                .Where(predicate: shape => isSurface(arg: shape.Type))
                .Select(selector: static shape => shape.Type));
            var grew = true;

            while (grew) {
                grew = false;

                foreach (var shape in WorldModelShape.Types) {
                    if (
                        !reaching.Contains(item: shape.Type) &&
                        ReachesFrom(
                        isSurface: isSurface,
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
        private static bool ReachesFrom(WorldModelType shape, HashSet<Type> reaching, Func<Type, bool> isSurface) {
            bool Holds(Type type) => (reaching.Contains(item: Underlying(type: type)) || isSurface(arg: Underlying(type: type)));

            if (
                (shape.ElementType is { } element) &&
                Holds(type: element)
            ) {
                return true;
            }

            foreach (var arm in shape.Arms) {
                if (Holds(type: arm.Type)) {
                    return true;
                }
            }
            foreach (var member in Walked(shape: shape)) {
                if (
                    IsWalked(member: member) &&
                    Holds(type: member.Type)
                ) {
                    return true;
                }
            }

            return false;
        }
        // Recursive generic properties can expand into a new closed type at every level, so type identity alone does
        // not bound discovery. Beyond this depth keep walking actual values conservatively.
        private const int MaxShapeDepth = 64;
        private bool Reaches(Type type, HashSet<Type> path) {
            if (
                walker.m_isSurface(arg: type) ||
                m_reaching.Value.Contains(item: type)
            ) {
                return true;
            }
            if (
                IsLeaf(type: type) ||
                (WorldModelShape.Of(type: type) is not null)
            ) {
                return false;
            }
            if (
                !type.IsSealed &&
                !type.IsValueType
            ) {
                return true;
            }
            if (
                (path.Count >= MaxShapeDepth) ||
                !path.Add(item: type)
            ) {
                return true;
            }

            try {
                if (type.IsArray) {
                    return Reaches(
                        path: path,
                        type: Underlying(type: type.GetElementType()!)
                    );
                }
                if (typeof(IEnumerable).IsAssignableFrom(c: type)) {
                    // These exact BCL implementations enumerate their declared element types through IEnumerable.
                    // An arbitrary enumerable may implement its non-generic enumeration differently.
                    if (
                        type.IsGenericType &&
                        ((type.GetGenericTypeDefinition() == typeof(List<>)) || (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                    ) {
                        return type.GenericTypeArguments.Any(predicate: argument => Reaches(
                            path: path,
                            type: Underlying(type: argument)
                        ));
                    }

                    return true;
                }

                return (ShapeOf(type: type) is { } shape) &&
                    Walked(shape: shape).Any(predicate: member => (IsWalked(member: member) && Reaches(
                        path: path,
                        type: Underlying(type: member.Type)
                    )));
            } finally {
                _ = path.Remove(item: type);
            }
        }
        // What the public properties and element type of a type the model does not describe give it.
        private static WorldModelType? Describe(Type type) {
            if (
                IsLeaf(type: type) ||
                type.IsGenericTypeDefinition
            ) {
                return null;
            }
            if (typeof(IEnumerable).IsAssignableFrom(c: type)) {
                // Only an array and the exact BCL collections enumerate their declared element type; any other
                // enumerable may implement its non-generic enumeration differently, so its items are read as objects.
                var definition = (type.IsGenericType
                    ? type.GetGenericTypeDefinition()
                    : null);

                return new WorldModelType(
                    Arms: [],
                    Described: false,
                    ElementType: (type.IsArray
                        ? type.GetElementType()
                        : ((definition == typeof(List<>))
                            ? type.GenericTypeArguments[0]
                            : ((definition == typeof(Dictionary<,>))
                                ? typeof(KeyValuePair<,>).MakeGenericType(typeArguments: type.GenericTypeArguments)
                                : typeof(object)))),
                    Kind: JsonTypeInfoKind.Enumerable,
                    Members: [],
                    Properties: [],
                    Type: type
                );
            }

            return new WorldModelType(
                Arms: [],
                Described: false,
                ElementType: null,
                Kind: JsonTypeInfoKind.Object,
                Members: [.. type.GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
                    .Where(predicate: static property => (property.CanRead && !property.PropertyType.IsByRefLike && (property.GetIndexParameters().Length == 0) && IsDocumentData(property: property)))
                    .Select(selector: static property => new WorldModelMember(
                        Access: WorldModelAccess.Read,
                        DeclaringType: property.DeclaringType!,
                        Member: property.Name,
                        Name: (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(name: property.Name)),
                        Type: property.PropertyType
                    ) { Get = Getter(property: property) })],
                Properties: [],
                Type: type
            );
        }
        // A compiled reader: walking a value invokes a delegate, never PropertyInfo.GetValue.
        private static Func<object, object?> Getter(PropertyInfo property) {
            var value = Expression.Parameter(
                name: "value",
                type: typeof(object)
            );

            return Expression.Lambda<Func<object, object?>>(
                Expression.Convert(
                    expression: Expression.Property(
                        expression: Expression.Convert(
                            expression: value,
                            type: property.DeclaringType!
                        ),
                        property: property
                    ),
                    type: typeof(object)
                ),
                value
            ).Compile();
        }
    }

    // One walk in progress: a visit (Visit) or a rewrite (Rewrite), whichever its constructor was handed.
    internal sealed class Cursor(Tables tables, IWorldDocumentVisitor visitor, IWorldDocumentRewriter rewriter) {
        // The reference values being walked above the one under visit: a value reached again below itself is a cycle.
        private readonly HashSet<object> m_active = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly List<Segment> m_path = [];

        public Cursor(Tables tables, IWorldDocumentVisitor visitor) : this(rewriter: null!, tables: tables, visitor: visitor) {
        }
        public Cursor(Tables tables, IWorldDocumentRewriter rewriter) : this(rewriter: rewriter, tables: tables, visitor: null!) {
        }

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
                (value is string) ||
                visitor.Stopped
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
            var entered = (type.IsValueType || m_active.Add(item: value));

            if (
                entered &&
                !visitor.Stopped &&
                ((tables.ShapeOf(type: type) ?? declared) is { } shape)
            ) {
                VisitMembers(
                    shape: shape,
                    value: value
                );
            }
            if (
                entered &&
                !type.IsValueType
            ) {
                _ = m_active.Remove(item: value);
            }

            visitor.Leave(value: value);
        }

        private void VisitMembers(object value, WorldModelType shape) {
            switch (shape.Kind) {
                case JsonTypeInfoKind.Enumerable:
                case JsonTypeInfoKind.Dictionary:
                    if (
                        (shape.ElementType is { } element) &&
                        tables.IsReaching(type: element) &&
                        (value is IEnumerable items)
                    ) {
                        var elementShape = tables.ShapeOf(type: element);
                        var index = 0;

                        foreach (var item in items) {
                            var (keyProperty, valueProperty) = (((item is null) || (shape.Kind != JsonTypeInfoKind.Dictionary))
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

                            if (visitor.Stopped) {
                                return;
                            }

                            index++;
                        }
                    }

                    break;
                case JsonTypeInfoKind.Object:
                case JsonTypeInfoKind.None:
                    foreach (var member in tables.MembersOf(shape: shape)) {
                        m_path.Add(item: new Segment(Index: 0, Key: null, Name: member.Name));
                        Visit(
                            declared: member.Declared,
                            field: member.Field,
                            value: member.Read(arg: value)
                        );
                        m_path.RemoveAt(index: (m_path.Count - 1));

                        if (visitor.Stopped) {
                            return;
                        }
                    }

                    break;
            }
        }

        // The rewrite twin of Visit: answers the value the site holds afterwards, the same instance when nothing under
        // it was replaced, so a caller copies its own owner only when an answer differs from what it read.
        public object? Rewrite(object? value, WorldModelType? declared, WorldValueField? field) {
            if (
                (value is null) ||
                (value is string) ||
                rewriter.Stopped
            ) {
                return value;
            }

            var type = value.GetType();
            var site = new WorldDocumentSite(
                field: field,
                walk: this
            );

            if (tables.IsSurface(type: type)) {
                return rewriter.Replace(
                    site: site,
                    value: value
                );
            }
            if (!rewriter.Enters(site: site, value: value)) {
                return value;
            }
            if (
                !type.IsValueType &&
                !m_active.Add(item: value)
            ) {
                return value;
            }

            try {
                var rewritten = (((tables.ShapeOf(type: type) ?? declared) is { } shape)
                    ? RewriteMembers(
                        shape: shape,
                        value: value
                    )
                    : value);

                if (!rewriter.Stopped) {
                    rewriter.Left(rewritten: rewritten, value: value);
                }

                return rewritten;
            } finally {
                if (!type.IsValueType) {
                    _ = m_active.Remove(item: value);
                }
            }
        }

        private object RewriteMembers(object value, WorldModelType shape) {
            switch (shape.Kind) {
                case JsonTypeInfoKind.Enumerable:
                case JsonTypeInfoKind.Dictionary:
                    return ((
                        (shape.ElementType is { } element) &&
                        tables.IsReaching(type: element) &&
                        (value is IEnumerable items)
                    )
                        ? RewriteItems(
                            dictionary: (shape.Kind == JsonTypeInfoKind.Dictionary),
                            elementShape: tables.ShapeOf(type: element),
                            items: items,
                            value: value
                        )
                        : value);
                case JsonTypeInfoKind.Object:
                case JsonTypeInfoKind.None:
                    if (PairOf(type: value.GetType()) is ({ } keyOf, { } valueOf)) {
                        return RewritePair(
                            key: keyOf.GetValue(obj: value),
                            pair: value,
                            shape: shape,
                            value: valueOf.GetValue(obj: value)
                        );
                    }

                    object? copy = null;

                    foreach (var member in tables.MembersOf(shape: shape)) {
                        var held = member.Read(arg: value);

                        m_path.Add(item: new Segment(Index: 0, Key: null, Name: member.Name));
                        var next = Rewrite(
                            declared: member.Declared,
                            field: member.Field,
                            value: held
                        );

                        if (rewriter.Stopped) {
                            m_path.RemoveAt(index: (m_path.Count - 1));

                            return value;
                        }
                        if (!ReferenceEquals(objA: held, objB: next)) {
                            copy ??= Copy(value: value);
                            Set(copy: copy, member: member, value: next);
                        }
                        m_path.RemoveAt(index: (m_path.Count - 1));
                    }

                    return (copy ?? value);
                default:
                    return value;
            }
        }
        // A key/value pair an enumerated dictionary yields, which has no setter: a changed one is built anew.
        private object RewritePair(object pair, object? key, object? value, WorldModelType shape) {
            var nextKey = key;
            var nextValue = value;

            foreach (var member in tables.MembersOf(shape: shape)) {
                var isKey = string.Equals(a: member.Property.Name, b: "Key", comparisonType: StringComparison.Ordinal);
                var held = (isKey ? key : value);

                m_path.Add(item: new Segment(Index: 0, Key: null, Name: member.Name));
                var next = Rewrite(
                    declared: member.Declared,
                    field: member.Field,
                    value: held
                );

                m_path.RemoveAt(index: (m_path.Count - 1));

                if (rewriter.Stopped) {
                    return pair;
                }
                if (isKey) {
                    nextKey = next;
                } else {
                    nextValue = next;
                }
            }

            return ((ReferenceEquals(objA: nextKey, objB: key) && ReferenceEquals(objA: nextValue, objB: value))
                ? pair
                : Activator.CreateInstance(type: pair.GetType(), args: [nextKey, nextValue])!);
        }
        // A list's or a dictionary's items rewritten: the same collection when no item changed, else a new one of the
        // kind the original was (an array as an array, a dictionary over its own comparer, any other list as a list).
        private object RewriteItems(object value, IEnumerable items, WorldModelType? elementShape, bool dictionary) {
            List<object?>? keys = null;
            List<object?>? rebuilt = null;
            var index = 0;

            foreach (var item in items) {
                var (keyProperty, valueProperty) = ((dictionary && (item is not null))
                    ? PairOf(type: item.GetType())
                    : (null, null));
                var key = keyProperty?.GetValue(obj: item);
                var held = ((valueProperty is null)
                    ? item
                    : valueProperty.GetValue(obj: item));

                m_path.Add(item: ((valueProperty is null)
                    ? new Segment(Index: index, Key: null, Name: null)
                    : new Segment(Index: index, Key: (key ?? string.Empty), Name: null)));
                var next = Rewrite(
                    declared: elementShape,
                    field: null,
                    value: held
                );

                m_path.RemoveAt(index: (m_path.Count - 1));

                if (rewriter.Stopped) {
                    return value;
                }
                if ((rebuilt is null) && !ReferenceEquals(objA: held, objB: next)) {
                    rebuilt = [];
                    keys = [];

                    var replay = 0;

                    foreach (var earlier in items) {
                        if (replay++ == index) {
                            break;
                        }

                        var (earlierKey, earlierValue) = ((dictionary && (earlier is not null))
                            ? PairOf(type: earlier.GetType())
                            : (null, null));

                        keys.Add(item: earlierKey?.GetValue(obj: earlier));
                        rebuilt.Add(item: ((earlierValue is null) ? earlier : earlierValue.GetValue(obj: earlier)));
                    }
                }

                keys?.Add(item: key);
                rebuilt?.Add(item: next);
                index++;
            }

            return ((rebuilt is null)
                ? value
                : Rebuild(
                    dictionary: dictionary,
                    keys: keys!,
                    original: value,
                    values: rebuilt
                ));
        }
        private object Rebuild(object original, List<object?> keys, List<object?> values, bool dictionary) {
            var type = original.GetType();

            if (type.IsArray) {
                var array = Array.CreateInstance(elementType: type.GetElementType()!, length: values.Count);

                for (var index = 0; (index < values.Count); index++) {
                    array.SetValue(index: index, value: values[index]);
                }

                return array;
            }
            // A dictionary is rebuilt as one whether the walk read it as pairs (a model dictionary) or as an enumeration
            // of key/value pair values (a dictionary the model does not describe).
            if ((Enumerated(type: type, definition: typeof(IReadOnlyDictionary<,>)) ?? Enumerated(type: type, definition: typeof(IDictionary<,>))) is { } pair) {
                var target = typeof(Dictionary<,>).MakeGenericType(typeArguments: pair.GenericTypeArguments);
                var comparer = (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    ? type.GetProperty(name: "Comparer")!.GetValue(obj: original)
                    : null);
                var rebuilt = (IDictionary)Activator.CreateInstance(type: target, args: [comparer])!;

                for (var index = 0; (index < values.Count); index++) {
                    if (dictionary) {
                        rebuilt.Add(key: keys[index]!, value: values[index]);
                    } else if ((values[index] is { } entry) && (PairOf(type: entry.GetType()) is ({ } keyOf, { } valueOf))) {
                        rebuilt.Add(key: keyOf.GetValue(obj: entry)!, value: valueOf.GetValue(obj: entry));
                    } else {
                        throw Unsupported(what: $"dictionary type {type}, whose item {index} is no key/value pair");
                    }
                }

                return rebuilt;
            }

            var element = (Enumerated(type: type, definition: typeof(IEnumerable<>)) ?? throw Unsupported(what: $"collection type {type}")).GenericTypeArguments[0];
            var list = (IList)Activator.CreateInstance(type: typeof(List<>).MakeGenericType(typeArguments: element), args: [values.Count])!;

            foreach (var item in values) {
                _ = list.Add(value: item);
            }

            return list;
        }
        private static Type? Enumerated(Type type, Type definition) {
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == definition)) {
                return type;
            }

            foreach (var candidate in type.GetInterfaces()) {
                if (candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == definition)) {
                    return candidate;
                }
            }

            return null;
        }
        private object Copy(object value) => ((tables.CopierOf(type: value.GetType()) is { } copier)
            ? copier(arg: value)
            : throw Unsupported(what: $"{value.GetType()}, which is neither a record nor a value type"));
        private void Set(object copy, WorldDocumentWalker.Member member, object? value) {
            if (member.Property.GetSetMethod(nonPublic: true) is null) {
                throw Unsupported(what: $"member {member.Property.DeclaringType}.{member.Property.Name}, which has no setter");
            }

            try {
                member.Property.SetValue(obj: copy, value: value);
            } catch (ArgumentException exception) {
                throw new NotSupportedException(message: $"the document walk cannot copy the path to {FormatPath()}: member {member.Property.DeclaringType}.{member.Property.Name} cannot hold its rewritten {value?.GetType()}.", innerException: exception);
            }
        }
        private NotSupportedException Unsupported(string what) => new(message: $"the document walk cannot copy the path to {FormatPath()}: {what}.");
    }
}
