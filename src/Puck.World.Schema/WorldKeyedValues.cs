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
/// <summary>One bindable a document binds to a state cell: where it sits, the cell it reads, and, for a presentation
/// scalar in a field the document model declares (<see cref="WorldValueFields"/>), its scalar form and that field.</summary>
/// <param name="Path">The value's document path, in JSON member names (<c>render.sky.layers[3].softness</c>).</param>
/// <param name="Binding">The state cell the value reads.</param>
/// <param name="Value">The bound scalar, a <see cref="BindableAngle"/> as its scalar form, when the value is a scalar in
/// a declared field; otherwise <see langword="null"/>.</param>
/// <param name="Field">The field the value fills, or <see langword="null"/> for a value that fills no declared scalar
/// field.</param>
public readonly record struct WorldBoundValue(string Path, StateBinding Binding, BindableScalar? Value, WorldValueField? Field);
/// <summary>The keyed values and the bound values one document authors, both found by one walk.</summary>
/// <param name="Keyed">The keyed values, in document order (<see cref="WorldKeyedValues.Of"/>).</param>
/// <param name="Bound">The bound values, in document order (<see cref="WorldKeyedValues.BoundOf"/>).</param>
public readonly record struct WorldAuthoredValues(IReadOnlyList<WorldKeyedValue> Keyed, IReadOnlyList<WorldBoundValue> Bound);
/// <summary>
/// Finds every keyed value a document authors, wherever the document places a bindable that can carry keys
/// (<see cref="BindableColor"/>, <see cref="BindableScalar"/>, <see cref="BindableAngle"/>,
/// <see cref="BindableDirection"/>, <see cref="BindableVector2"/>, <see cref="BindableVector3"/>), so the validator judges
/// each one's clock and times without a walker per section, and every bindable and text-screen placeholder a document
/// binds to a state cell. It is one consumer of the document walk (<see cref="WorldDocumentWalker"/>).
/// </summary>
public static class WorldKeyedValues {
    private static readonly Type[] Surfaces = [
        typeof(BindableAngle),
        typeof(BindableColor),
        typeof(BindableDirection),
        typeof(BindableScalar),
        typeof(BindableVector2),
        typeof(BindableVector3),
    ];
    private static readonly WorldDocumentWalker Walker = new(surfaces: [.. Surfaces, typeof(WorldScreenSource.Text)]);

    /// <summary>Returns every keyed value a document authors, in document order.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The keyed values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldKeyedValue> Of(WorldDefinition definition) => AuthoredBy(definition: definition).Keyed;
    /// <summary>Returns every bindable a document binds to a state cell, in document order: each a reading of that
    /// cell wherever it is presented, and, for a presentation scalar in a field the document model declares
    /// (<see cref="WorldValueFields"/>), its scalar form and that field.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The bound values, including text-screen placeholders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldBoundValue> BoundOf(WorldDefinition definition) => AuthoredBy(definition: definition).Bound;
    /// <summary>Returns a document's keyed values (<see cref="Of"/>) and bound values (<see cref="BoundOf"/>) from one
    /// walk of the document.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>Both lists, each in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldAuthoredValues AuthoredBy(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var visitor = new Visitor();

        Walker.Walk(
            root: definition,
            visitor: visitor
        );

        return new WorldAuthoredValues(
            Bound: visitor.Bound,
            Keyed: visitor.Found
        );
    }
    /// <summary>Returns whether a model type is a bindable that can carry keys on a clock.</summary>
    /// <param name="type">The type; a nullable value type answers for its underlying type.</param>
    /// <returns><see langword="true"/> for <see cref="BindableColor"/>, <see cref="BindableScalar"/>,
    /// <see cref="BindableAngle"/>, <see cref="BindableDirection"/>, <see cref="BindableVector2"/> and
    /// <see cref="BindableVector3"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static bool IsKeyable(Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        return Surfaces.Contains(value: (Nullable.GetUnderlyingType(nullableType: type) ?? type));
    }
    /// <summary>Returns the state cell a bindable reads, or <see langword="null"/> for a literal, keys or a value that
    /// is no bindable. A direction or a vector binds no state cell.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    public static StateBinding? BindingOf(object? value) => (value switch {
        BindableColor color => color.State,
        BindableScalar scalar => scalar.State,
        BindableAngle angle => angle.Value.State,
        _ => null,
    });
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

    // Records a bindable's keys or the cell it reads, and a text screen's placeholders; the walk enters neither.
    private sealed class Visitor : IWorldDocumentVisitor {
        public List<WorldBoundValue> Bound { get; } = [];
        public List<WorldKeyedValue> Found { get; } = [];

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is WorldScreenSource.Text text) {
                foreach (var (line, token, binding) in WorldScreenText.Placeholders(text: text)) {
                    Bound.Add(item: new WorldBoundValue(
                        Binding: binding,
                        Field: null,
                        Path: $"{site.Path}.lines[{line}] placeholder '{{{token}}}'",
                        Value: null
                    ));
                }

                return false;
            }
            if (!Surfaces.Contains(value: value.GetType())) {
                return true;
            }
            if (KeysOf(value: value) is { } track) {
                Found.Add(item: new WorldKeyedValue(
                    Path: site.Path,
                    Track: track,
                    Value: value
                ));
            } else if (BindingOf(value: value) is { } binding) {
                var scalar = (value switch {
                    BindableScalar bindable => bindable,
                    BindableAngle angle => angle.Value,
                    _ => ((BindableScalar?)null),
                });
                var declaredScalar = ((site.Field is not null) && scalar.HasValue);

                Bound.Add(item: new WorldBoundValue(
                    Binding: binding,
                    Field: (declaredScalar ? site.Field : null),
                    Path: site.Path,
                    Value: (declaredScalar ? scalar : null)
                ));
            }

            return false;
        }
    }
}
