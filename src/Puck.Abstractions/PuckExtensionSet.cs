using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Puck.Abstractions;

/// <summary>
/// The immutable result of composing one host's extensions: every extension, ordered by name, and every contribution,
/// grouped by kind and ordered by key. Composition is deterministic — the same extensions give the same set whatever
/// order a host supplies or discovers them in — and refuses every conflict by name instead of letting an order decide.
/// </summary>
/// <remarks>The set owns the extension instances it composed: disposing it disposes each extension that implements
/// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, once, in reverse name order. It never owns what a
/// contribution's factory creates; the host that calls the factory owns that. Reading is thread-safe.</remarks>
public sealed class PuckExtensionSet : IAsyncDisposable {
    private readonly Dictionary<Type, Kind> m_contributions;
    private readonly IPuckExtension[] m_extensions;

    private int m_disposed;

    private PuckExtensionSet(IPuckExtension[] extensions, Dictionary<Type, Kind> contributions) {
        m_contributions = contributions;
        m_extensions = extensions;
    }

    /// <summary>Gets the composed extensions, ordered ordinally by <see cref="IPuckExtension.Name"/>.</summary>
    public IReadOnlyList<IPuckExtension> Extensions => m_extensions;

    private static string Describe(IPuckExtension extension) => $"'{extension.Name}' ({extension.GetType().FullName})";
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        condition: (Volatile.Read(location: ref m_disposed) != 0),
        instance: this
    );

    /// <summary>Composes extensions into a set, registering each in ordinal name order. The set owns every supplied
    /// extension whatever the outcome: when composition is refused, each disposable extension received so far is
    /// disposed, once, in reverse name order, before the refusal propagates.</summary>
    /// <param name="extensions">The host's built-in and discovered extensions, in any order. An exception the sequence
    /// itself raises is a refusal too; the extensions it yielded before it are disposed.</param>
    /// <returns>The composed set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="extensions"/> is <see langword="null"/>.</exception>
    /// <exception cref="PuckExtensionException">An extension is <see langword="null"/> or has a blank name, two extensions
    /// share a name, a registration is refused (see <see cref="IPuckExtensionRegistry.Add"/>), or an extension's
    /// <see cref="IPuckExtension.Register"/> fails, named by its extension with the failure as the inner exception.</exception>
    /// <exception cref="AggregateException">Composition was refused and disposing a received extension also failed; the
    /// refusal is the first inner exception.</exception>
    public static PuckExtensionSet Compose(IEnumerable<IPuckExtension> extensions) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        var ordered = new List<IPuckExtension>();

        try {
            foreach (var extension in extensions) {
                if (extension is null) { throw new PuckExtensionException(message: "A null extension was supplied."); }
                ordered.Add(item: extension);
                if (string.IsNullOrWhiteSpace(value: extension.Name)) { throw new PuckExtensionException(message: $"Extension {extension.GetType().FullName} has a blank name."); }
            }
            ordered.Sort(comparison: static (left, right) => string.CompareOrdinal(
                strA: left.Name,
                strB: right.Name
            ));
            for (var index = 1; (index < ordered.Count); index++) {
                if (string.Equals(
                    a: ordered[(index - 1)].Name,
                    b: ordered[index].Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new PuckExtensionException(message: $"Extensions {Describe(extension: ordered[(index - 1)])} and {Describe(extension: ordered[index])} share one name.");
                }
            }

            var kinds = new Dictionary<Type, Kind>();

            foreach (var extension in ordered) {
                var registry = new Registry(
                    extension: extension.Name,
                    kinds: kinds
                );

                try { extension.Register(registry: registry); } catch (Exception error) when ((error is not PuckExtensionException)) {
                    throw new PuckExtensionException(
                        inner: error,
                        message: $"Extension '{extension.Name}' failed to register: {error.Message}"
                    );
                } finally { registry.Close(); }
            }
            foreach (var kind in kinds.Values) { kind.Freeze(); }
            return new PuckExtensionSet(
                contributions: kinds,
                extensions: [.. ordered]
            );
        } catch (Exception refusal) {
            var failures = DisposeReceived(extensions: ordered);

            if (failures.Count == 0) { throw; }
            throw new AggregateException(innerExceptions: [refusal, .. failures]);
        }
    }

    // Disposes each distinct received extension once, in reverse name order, attempting every one and returning what
    // failed.
    private static List<Exception> DisposeReceived(List<IPuckExtension> extensions) {
        var failures = new List<Exception>();
        var seen = new HashSet<IPuckExtension>(comparer: ReferenceEqualityComparer.Instance);
        var order = extensions.OrderByDescending(
            comparer: StringComparer.Ordinal,
            keySelector: static extension => (extension.Name ?? "")
        );

        foreach (var extension in order) {
            if (!seen.Add(item: extension)) { continue; }
            try {
                switch (extension) {
                    case IAsyncDisposable asynchronous:
                        asynchronous.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                    case IDisposable synchronous:
                        synchronous.Dispose();
                        break;
                }
            } catch (Exception error) {
                failures.Add(item: error);
            }
        }
        return failures;
    }

    /// <summary>Returns every contribution of one kind, ordered ordinally by key.</summary>
    /// <typeparam name="TContribution">The contribution kind, matched exactly.</typeparam>
    /// <returns>The kind's contributions; empty when no extension registered one.</returns>
    /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
    public IReadOnlyList<PuckContribution<TContribution>> Contributions<TContribution>() where TContribution : class {
        ThrowIfDisposed();
        return (m_contributions.TryGetValue(
            key: typeof(TContribution),
            value: out var kind
        )
            ? ((Kind<TContribution>)kind).Frozen
            : []
        );
    }
    /// <summary>Returns one line per extension naming each of its contributions by kind and key — the read-back hosts
    /// print and compare.</summary>
    /// <returns>Lines of the form <c>Name: Kind key, Kind key</c>, in extension order; kinds in ordinal order of their
    /// type name, keys in ordinal order.</returns>
    /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
    public IReadOnlyList<string> Describe() {
        ThrowIfDisposed();
        var byExtension = m_extensions.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static _ => new List<string>(),
            keySelector: static extension => extension.Name
        );

        foreach (var (type, kind) in m_contributions.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static pair => pair.Key.Name
        )) {
            foreach (var (key, extension) in kind.Identities) { byExtension[extension].Add(item: $"{type.Name} {key}"); }
        }
        return [.. m_extensions.Select(selector: extension => ((byExtension[extension.Name].Count == 0)
            ? extension.Name
            : $"{extension.Name}: {string.Join(
                separator: ", ",
                values: byExtension[extension.Name]
            )}"
        ))];
    }
    /// <summary>Disposes each disposable extension once, in reverse name order. Later calls do nothing.</summary>
    /// <returns>A task that completes when every extension has been disposed.</returns>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }
        for (var index = (m_extensions.Length - 1); (index >= 0); index--) {
            switch (m_extensions[index]) {
                case IAsyncDisposable asynchronous:
                    await asynchronous.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
                    break;
                case IDisposable synchronous:
                    synchronous.Dispose();
                    break;
            }
        }
    }
    /// <summary>Selects the contribution a deployment names — the one way every host turns a configured selection key
    /// into a contribution. A named key must be installed; an absent key selects the kind's one installed
    /// contribution.</summary>
    /// <typeparam name="TContribution">The contribution kind, matched exactly.</typeparam>
    /// <param name="key">The deployment's selection key, or <see langword="null"/> when it names none.</param>
    /// <param name="purpose">The configuration member that made the selection, which opens every refusal.</param>
    /// <returns>The selected contribution.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> names no installed contribution of this kind (the
    /// refusal names the installed keys), or <paramref name="key"/> is <see langword="null"/> and none or more than one
    /// is installed (the refusal names each key and its extension).</exception>
    /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
    public TContribution Select<TContribution>(string? key, string purpose) where TContribution : class {
        ThrowIfDisposed();
        var installed = Contributions<TContribution>();
        var kind = typeof(TContribution).Name;

        if (key is null) {
            return installed.Count switch {
                1 => installed[0].Value,
                0 => throw new ArgumentException(message: $"{purpose}: no installed extension provides a {kind}."),
                _ => throw new ArgumentException(message: $"{purpose}: more than one installed extension provides a {kind} ({string.Join(
                    separator: ", ",
                    values: installed.Select(selector: static entry => $"'{entry.Key}' from {entry.Extension}")
                )}); name one."),
            };
        }
        if (TryGet<TContribution>(
            contribution: out var contribution,
            key: key
        )) {
            return contribution;
        }
        throw new ArgumentException(message: ((installed.Count == 0)
            ? $"{purpose} '{key}' is not installed; no installed extension provides a {kind}."
            : $"{purpose} '{key}' is not installed; name one of: {string.Join(
                separator: ", ",
                values: installed.Select(selector: static entry => entry.Key)
            )}."
        ));
    }
    /// <summary>Returns the contribution of one kind registered under a key.</summary>
    /// <typeparam name="TContribution">The contribution kind, matched exactly.</typeparam>
    /// <param name="key">The ordinal key.</param>
    /// <param name="contribution">The contribution; meaningful only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an extension registered the key for this kind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The set was disposed.</exception>
    public bool TryGet<TContribution>(string key, [MaybeNullWhen(returnValue: false)] out TContribution contribution) where TContribution : class {
        ArgumentNullException.ThrowIfNull(argument: key);
        ThrowIfDisposed();
        if (
            m_contributions.TryGetValue(
                key: typeof(TContribution),
                value: out var kind
            ) &&
            ((Kind<TContribution>)kind).ByKey.TryGetValue(
                key: key,
                value: out var entry
            )
        ) {
            contribution = entry.Value;
            return true;
        }
        contribution = null;
        return false;
    }

    private abstract class Kind {
        public abstract IEnumerable<(string Key, string Extension)> Identities { get; }

        public abstract void Freeze();
    }
    private sealed class Kind<TContribution> : Kind where TContribution : class {
        public FrozenDictionary<string, PuckContribution<TContribution>> ByKey { get; private set; } = FrozenDictionary<string, PuckContribution<TContribution>>.Empty;
        public SortedDictionary<string, PuckContribution<TContribution>> Entries { get; } = new(comparer: StringComparer.Ordinal);
        public PuckContribution<TContribution>[] Frozen { get; private set; } = [];

        public override IEnumerable<(string Key, string Extension)> Identities => Frozen.Select(selector: static entry => (entry.Key, entry.Extension));

        public override void Freeze() {
            Frozen = [.. Entries.Values];
            ByKey = Entries.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        }
    }
    private sealed class Registry(string extension, Dictionary<Type, Kind> kinds) : IPuckExtensionRegistry {
        private bool m_closed;

        public void Add<TContribution>(string key, TContribution contribution) where TContribution : class {
            if (m_closed) { throw new PuckExtensionException(message: $"Extension '{extension}' registered {typeof(TContribution).Name} '{key}' after its registration ended."); }
            if (string.IsNullOrWhiteSpace(value: key)) { throw new PuckExtensionException(message: $"Extension '{extension}' registered a {typeof(TContribution).Name} with a blank key."); }
            if (contribution is null) { throw new PuckExtensionException(message: $"Extension '{extension}' registered a null {typeof(TContribution).Name} '{key}'."); }
            if (!kinds.TryGetValue(
                key: typeof(TContribution),
                value: out var kind
            )) {
                kind = new Kind<TContribution>();
                kinds.Add(
                    key: typeof(TContribution),
                    value: kind
                );
            }

            var entries = ((Kind<TContribution>)kind).Entries;

            if (entries.TryGetValue(
                key: key,
                value: out var held
            )) {
                throw new PuckExtensionException(message: (string.Equals(
                    a: held.Extension,
                    b: extension,
                    comparisonType: StringComparison.Ordinal
                )
                    ? $"Extension '{extension}' registers {typeof(TContribution).Name} '{key}' twice."
                    : $"Extensions '{held.Extension}' and '{extension}' both register {typeof(TContribution).Name} '{key}'."
                ));
            }
            entries.Add(
                key: key,
                value: new PuckContribution<TContribution>(
                    Extension: extension,
                    Key: key,
                    Value: contribution
                )
            );
        }
        public void Close() => m_closed = true;
    }
}
