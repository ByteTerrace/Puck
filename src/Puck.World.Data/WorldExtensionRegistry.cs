namespace Puck.World;

/// <summary>
/// A keyed registry of one kind of host-supplied implementation (<typeparamref name="TExtension"/>) — the primitive
/// behind every document row that SELECTS an implementation by a string key instead of naming it in the schema. The
/// host owns the population (registering a new implementation is a composition-root edit), the document carries only
/// the key, and the schema therefore never grows a field or an enum case per implementation. Nothing here knows what
/// the extensions are or do: the key is whatever <c>keyOf</c> reads, so one type serves machine engines, renderers,
/// backends, content sources, or any later extension point equally.
/// </summary>
/// <remarks>The registered set is FIXED at construction, which is what lets a document-side vocabulary check and a
/// host-side resolution be answers to the same question — there is no window in which they can disagree. Building is
/// single-threaded; reading needs no synchronization.</remarks>
/// <typeparam name="TExtension">The registered implementation contract.</typeparam>
public sealed class WorldExtensionRegistry<TExtension> where TExtension : class {
    private readonly Dictionary<string, TExtension> m_byKey;

    /// <summary>Builds a registry from the host's collected extensions, keyed by <paramref name="keyOf"/>. Keys compare
    /// ORDINALLY — a registry key is a token, never prose. Every refusal here describes the COMPOSITION ROOT and never
    /// a document: a null element, a null or blank key, and one key claimed twice are all authoring errors in the
    /// registration list, so each throws at construction naming the offending implementation types rather than
    /// last-writer-wins into a set that no longer matches what the host believes it registered.</summary>
    /// <param name="extensions">The registered extension instances.</param>
    /// <param name="keyOf">Reads an extension's stable registry key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="extensions"/> or <paramref name="keyOf"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="extensions"/> holds a <see langword="null"/> element, an
    /// element whose key is null or blank, or two elements resolving to one key.</exception>
    public WorldExtensionRegistry(IEnumerable<TExtension> extensions, Func<TExtension, string> keyOf) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        ArgumentNullException.ThrowIfNull(argument: keyOf);

        m_byKey = new Dictionary<string, TExtension>(comparer: StringComparer.Ordinal);

        foreach (var extension in extensions) {
            if (extension is null) {
                throw new ArgumentException(message: $"A null {typeof(TExtension).Name} is registered.", paramName: nameof(extensions));
            }

            var key = keyOf(extension);

            if (string.IsNullOrWhiteSpace(value: key)) {
                throw new ArgumentException(message: $"{Describe(extension: extension)} registers a null or blank {typeof(TExtension).Name} key.", paramName: nameof(extensions));
            }

            if (!m_byKey.TryAdd(key: key, value: extension)) {
                throw new ArgumentException(message: $"{Describe(extension: m_byKey[key: key])} and {Describe(extension: extension)} both register the {typeof(TExtension).Name} key '{key}'.", paramName: nameof(extensions));
            }
        }
    }

    /// <summary>Gets the number of registered extensions.</summary>
    public int Count => m_byKey.Count;

    /// <summary>Gets every registered key, in the dictionary's enumeration order — in practice registration order, since
    /// entries are only ever added — what a caller's refusal echoes when a key resolves to nothing ("name one
    /// of: …").</summary>
    public IReadOnlyCollection<string> Keys => m_byKey.Keys;

    /// <summary>Gets every registered extension, in the dictionary's enumeration order — in practice registration order,
    /// since entries are only ever added — read by the sole-registration convenience a caller may offer when a key is
    /// omitted and exactly one extension is registered.</summary>
    public IReadOnlyCollection<TExtension> Values => m_byKey.Values;

    /// <summary>Determines whether a key names a registered extension — the deny-by-default vocabulary check a document-side
    /// validator runs before the key ever reaches the host.</summary>
    /// <param name="key">The candidate key.</param>
    /// <returns><see langword="true"/> when the key is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool IsRegistered(string key) => m_byKey.ContainsKey(key: key);

    /// <summary>Resolves a key to its registered extension.</summary>
    /// <param name="key">The candidate key.</param>
    /// <param name="extension">The registered extension; meaningful only when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the key is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool TryGet(string key, out TExtension extension) => m_byKey.TryGetValue(key: key, value: out extension!);

    private static string Describe(TExtension extension) => (extension.GetType().FullName ?? extension.GetType().Name);
}
