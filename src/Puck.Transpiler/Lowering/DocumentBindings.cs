namespace Puck.Transpiler.Lowering;

/// <summary>The names a lowering scope binds (its constants, its template arguments' scopes, its locals), held as a
/// chain of frames so that a derived scope adds a frame over the one it derives from instead of copying every
/// binding.</summary>
/// <remarks>A lookup reads the newest frame first, so a name bound in a derived scope hides the same name further
/// up, as a name written into a copy replaced the copied one. A frame another scope has derived from is frozen: a later
/// write to the scope that owns it starts a new frame over it, so a derived scope keeps seeing exactly the bindings it
/// derived from. A chain that grows deeper than <c>FlattenDepth</c> frames is flattened into one, so a lookup's cost
/// stays bounded however many derivations a lowering makes.</remarks>
/// <typeparam name="TValue">The value a name binds.</typeparam>
public sealed class DocumentBindings<TValue> {
    // Past this depth a new frame copies the chain into itself instead of linking to it.
    private const int FlattenDepth = 16;

    private Frame? m_top;

    /// <summary>Initializes a new instance of the <see cref="DocumentBindings{TValue}"/> class, binding nothing.</summary>
    public DocumentBindings() { }
    private DocumentBindings(Frame? top) => m_top = top;

    /// <summary>Gets bindings of nothing, for a scope with no locals. Writing to it is an error.</summary>
    public static DocumentBindings<TValue> Empty { get; } = new();

    /// <summary>Gets whether no name is bound.</summary>
    public bool IsEmpty => (m_top is null);

    /// <summary>Sets the value a name binds, replacing a binding of the same name for these bindings and every one
    /// derived from them afterwards; bindings derived before keep the value they saw.</summary>
    /// <param name="name">The name.</param>
    /// <exception cref="InvalidOperationException">The bindings are <see cref="Empty"/>.</exception>
    public TValue this[string name] {
        set => Set(name: name, value: value);
    }

    /// <summary>Reads the value a name binds, the newest binding first.</summary>
    /// <param name="key">The name.</param>
    /// <param name="value">The value, or the default when the name is unbound.</param>
    /// <returns><see langword="true"/> when the name is bound.</returns>
    public bool TryGetValue(string key, out TValue value) {
        for (var frame = m_top; (frame is not null); frame = frame.Parent) {
            if (frame.TryGetValue(name: key, value: out value)) {
                return true;
            }
        }
        value = default!;
        return false;
    }
    /// <summary>Returns whether a name is bound.</summary>
    /// <param name="key">The name.</param>
    /// <returns><see langword="true"/> when the name is bound.</returns>
    public bool ContainsKey(string key) => TryGetValue(key: key, value: out _);
    /// <summary>Reads the value a name binds, or the default.</summary>
    /// <param name="key">The name.</param>
    /// <returns>The value, or the default when the name is unbound.</returns>
    public TValue? GetValueOrDefault(string key) => (TryGetValue(key: key, value: out var value) ? value : default);
    /// <summary>Binds a name unless it is already bound.</summary>
    /// <param name="key">The name.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the name was unbound and is now bound.</returns>
    /// <exception cref="InvalidOperationException">The bindings are <see cref="Empty"/>.</exception>
    public bool TryAdd(string key, TValue value) {
        if (ContainsKey(key: key)) {
            return false;
        }
        Set(name: key, value: value);
        return true;
    }
    /// <summary>Returns bindings that start as these and change apart from them: a write to either is never seen by
    /// the other.</summary>
    /// <returns>The derived bindings, sharing every frame these hold.</returns>
    public DocumentBindings<TValue> Derive() {
        if (m_top is null) {
            return new();
        }
        m_top.Frozen = true;
        return new(top: m_top);
    }
    /// <summary>Returns bindings that add one name over these, which stay as they are.</summary>
    /// <param name="name">The name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The derived bindings.</returns>
    public DocumentBindings<TValue> With(string name, TValue value) {
        var derived = Derive();

        derived.Set(name: name, value: value);
        return derived;
    }
    /// <summary>Returns bindings that add two names over these, which stay as they are; the second wins when the
    /// names are the same.</summary>
    /// <param name="first">The first name.</param>
    /// <param name="firstValue">The first value.</param>
    /// <param name="second">The second name.</param>
    /// <param name="secondValue">The second value.</param>
    /// <returns>The derived bindings.</returns>
    public DocumentBindings<TValue> With(string first, TValue firstValue, string second, TValue secondValue) {
        var derived = Derive();

        derived.Set(name: first, value: firstValue);
        derived.Set(name: second, value: secondValue);
        return derived;
    }
    /// <summary>Returns bindings that add names over these, which stay as they are; a later pair wins over an earlier
    /// one of the same name.</summary>
    /// <param name="bindings">The names and values, in order.</param>
    /// <returns>The derived bindings, or these when <paramref name="bindings"/> is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public DocumentBindings<TValue> With(IEnumerable<KeyValuePair<string, TValue>> bindings) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        DocumentBindings<TValue>? derived = null;

        foreach (var (name, value) in bindings) {
            (derived ??= Derive()).Set(name: name, value: value);
        }
        return (derived ?? this);
    }

    private void Set(string name, TValue value) {
        if (ReferenceEquals(objA: this, objB: Empty)) {
            throw new InvalidOperationException(message: "The empty bindings are shared and bind nothing.");
        }
        if ((m_top is null) || m_top.Frozen) {
            m_top = new Frame(parent: (((m_top is not null) && (m_top.Depth >= FlattenDepth)) ? m_top.Flattened() : m_top));
        }
        m_top.Set(name: name, value: value);
    }

    // One frame: the first two bindings in fields, since most frames (a lambda's parameters, a loop's item and index)
    // hold one or two, and any further ones in a dictionary.
    private sealed class Frame(Frame? parent) {
        private string? m_firstName;
        private TValue m_firstValue = default!;
        private string? m_secondName;
        private TValue m_secondValue = default!;
        private Dictionary<string, TValue>? m_more;
        private Frame? m_flattened;

        public Frame? Parent { get; } = parent;
        public int Depth { get; } = ((parent?.Depth ?? 0) + 1);
        public bool Frozen { get; set; }

        // The chain ending at this frozen frame as one frozen frame, built once and shared by every scope that
        // writes over it.
        public Frame Flattened() {
            if (m_flattened is null) {
                var frames = new Stack<Frame>();

                for (var frame = this; (frame is not null); frame = frame.Parent) {
                    frames.Push(item: frame);
                }
                var flat = new Frame(parent: null);

                while (frames.TryPop(result: out var frame)) {
                    frame.CopyTo(into: flat);
                }
                flat.Frozen = true;
                m_flattened = flat;
            }
            return m_flattened;
        }
        public bool TryGetValue(string name, out TValue value) {
            if (string.Equals(a: m_firstName, b: name, comparisonType: StringComparison.Ordinal)) {
                value = m_firstValue;
                return true;
            }
            if (string.Equals(a: m_secondName, b: name, comparisonType: StringComparison.Ordinal)) {
                value = m_secondValue;
                return true;
            }
            if (m_more is not null) {
                return m_more.TryGetValue(key: name, value: out value!);
            }
            value = default!;
            return false;
        }
        public void Set(string name, TValue value) {
            if ((m_firstName is null) || string.Equals(a: m_firstName, b: name, comparisonType: StringComparison.Ordinal)) {
                (m_firstName, m_firstValue) = (name, value);
            } else if ((m_secondName is null) || string.Equals(a: m_secondName, b: name, comparisonType: StringComparison.Ordinal)) {
                (m_secondName, m_secondValue) = (name, value);
            } else {
                (m_more ??= new(comparer: StringComparer.Ordinal))[name] = value;
            }
        }
        private void CopyTo(Frame into) {
            if (m_firstName is not null) {
                into.Set(name: m_firstName, value: m_firstValue);
            }
            if (m_secondName is not null) {
                into.Set(name: m_secondName, value: m_secondValue);
            }
            if (m_more is not null) {
                foreach (var (name, value) in m_more) {
                    into.Set(name: name, value: value);
                }
            }
        }
    }
}
