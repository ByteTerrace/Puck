using System.Collections.Concurrent;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>
/// The silo's per-grain console routing table: which admitted row's <see cref="TextCommandSession"/> a tagged
/// <c>@&lt;key&gt; line</c> enqueues into, which row an untagged line defaults to (<c>silo.use</c>), and the
/// slot-&gt;world-id map <see cref="SiloConsoleAuthority"/> resolves a dispatched command's row through. Rows are
/// registered and unregistered on the tick thread (the silo's activation mailbox, where <see cref="WorldSiloHost"/>
/// admits/retires a row); <see cref="SiloStdinRouter"/> reads it from its own dedicated reader thread, so the row
/// table is a <see cref="ConcurrentDictionary{TKey,TValue}"/> and the <c>silo.use</c> default is stored behind
/// <see cref="Volatile"/>.
/// </summary>
public sealed class SiloConsoleRouting {
    // Restores the prior ambient scope on Dispose rather than always clearing to null, so a verb whose own handler
    // synchronously reaches another row (a co-silo peer call under Register's session) nests correctly.
    private readonly struct RowNarrationScope : IDisposable {
        private readonly string? m_previous;

        public RowNarrationScope(string worldId) {
            m_previous = WorldNarrationScope.Current;
            WorldNarrationScope.Current = worldId;
        }

        public void Dispose() => WorldNarrationScope.Current = m_previous;
    }
    private sealed record RowRoute(int Slot, TextCommandSession Session);

    private readonly ConcurrentDictionary<int, string> m_bySlot = new();
    private readonly ConcurrentDictionary<string, RowRoute> m_byWorldId = new(comparer: StringComparer.Ordinal);

    private readonly Func<TextCommandSource> m_source;
    private readonly SiloConsoleTagging m_tagging;

    private string? m_defaultWorldId;
    private int m_nextSlot;

    /// <summary>Initializes the routing table over the silo's shared command source. Slot 0 is reserved for the
    /// administrative <c>silo.*</c> session, so per-row slots start at 1.</summary>
    /// <param name="source">Resolves the shared text command source every session (administrative and per-row) is
    /// created against — resolved at the first <see cref="Register"/>, never at construction: the source's command
    /// registry holds modules that resolve the silo host, which holds this routing table, so an eager dependency
    /// here is a container cycle.</param>
    /// <param name="tagging">Where every session's result — administrative and per-row alike — is written.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SiloConsoleRouting(Func<TextCommandSource> source, SiloConsoleTagging tagging) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: tagging);

        m_source = source;
        m_tagging = tagging;
    }

    /// <summary>Gets the world id <c>silo.use</c> last selected for untagged lines, or <see langword="null"/> when
    /// none has been selected.</summary>
    public string? DefaultWorldId => Volatile.Read(location: ref m_defaultWorldId);

    /// <summary>Registers a freshly admitted row's own tagged console session, tagging every result it produces
    /// with <c>[&lt;worldId&gt;] </c> ahead of the shared output writer's own <c>Console.Out</c>/<c>Console.Error</c>
    /// routing.</summary>
    /// <param name="worldId">The row's registry name.</param>
    /// <param name="hold">The row's own <c>world.wait</c> hold predicate.</param>
    /// <returns>The row's own text session — the caller enqueues nothing through it directly; it exists so the
    /// registry submits lines under this row's stamped identity and slot.</returns>
    public TextCommandSession Register(string worldId, Func<bool> hold) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: worldId);
        ArgumentNullException.ThrowIfNull(argument: hold);

        var slot = Interlocked.Increment(location: ref m_nextSlot);
        var session = m_source().CreateSession(
            hold: hold,
            onResult: (_, result) => m_tagging.WriteTagged(
                result: result,
                tag: worldId
            ),
            principal: CommandPrincipal.Console,
            scope: () => new RowNarrationScope(worldId: worldId),
            slot: slot
        );

        m_byWorldId[worldId] = new RowRoute(
            Session: session,
            Slot: slot
        );
        m_bySlot[slot] = worldId;

        return session;
    }
    /// <summary>Sets the world id untagged lines route to, or clears it. <c>silo.use</c>'s one write.</summary>
    /// <param name="worldId">The world id to select, or <see langword="null"/> to clear the selection.</param>
    public void SetDefault(string? worldId) => Volatile.Write(
        location: ref m_defaultWorldId,
        value: worldId
    );
    /// <summary>Resolves the world id bound to a dispatched command's slot — <see cref="SiloConsoleAuthority"/>'s
    /// one read.</summary>
    /// <param name="slot">The invocation's <c>CommandContext.Slot</c>.</param>
    /// <param name="worldId">The bound world id, on success.</param>
    /// <returns><see langword="true"/> when the slot names a currently registered row.</returns>
    public bool TryResolveWorldId(int slot, out string worldId) => m_bySlot.TryGetValue(
        key: slot,
        value: out worldId!
    );
    /// <summary>Resolves a currently registered row's own session by world id — <see cref="SiloStdinRouter"/>'s
    /// tagged-input lookup.</summary>
    /// <param name="worldId">The row's registry name.</param>
    /// <param name="session">The row's own session, on success.</param>
    /// <returns><see langword="true"/> when the world id is currently registered (its row is admitted).</returns>
    public bool TryGetSession(string worldId, out TextCommandSession session) {
        if (m_byWorldId.TryGetValue(
            key: worldId,
            value: out var route
        )) {
            session = route.Session;

            return true;
        }

        session = null!;

        return false;
    }
    /// <summary>Retires a row's console session — called from the same tick-thread mailbox action that removes the
    /// row itself, so a session's lifetime never outlives its row.</summary>
    /// <param name="worldId">The row's registry name.</param>
    public void Unregister(string worldId) {
        if (m_byWorldId.TryRemove(
            key: worldId,
            value: out var route
        )) {
            m_bySlot.TryRemove(
                key: route.Slot,
                value: out _
            );
        }

        if (string.Equals(
            a: DefaultWorldId,
            b: worldId,
            comparisonType: StringComparison.Ordinal
        )) {
            SetDefault(worldId: null);
        }
    }
}
