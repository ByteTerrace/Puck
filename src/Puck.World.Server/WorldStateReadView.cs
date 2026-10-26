using Puck.Commands;

namespace Puck.World.Server;

/// <summary>One principal's read of a live world's state: the document as <see cref="WorldStateDisclosure"/>
/// discloses it to that principal, and the refusal a read of anything withheld answers with.
/// <para>A console read-back verb reads state through this view rather than through <see cref="WorldServer.Definition"/>,
/// so a seat, an addon or a peer is shown what its visibility admits and nothing else. The operator — the
/// <see cref="PrincipalKind.Console"/> principal — reads the live document whole.</para></summary>
public sealed class WorldStateReadView {
    private WorldServer? m_server;
    private StateArena? m_arena;
    private readonly IReadOnlyDictionary<string, WorldWithheldRow> m_withheld;

    private WorldStateReadView(Principal reader, WorldStateDisclosed disclosed, ulong completedTick, ulong completedEngineTick, long arenaBytes) {
        Reader = reader;
        Definition = disclosed.Definition;
        m_withheld = disclosed.Withheld;
        CompletedTick = completedTick;
        CompletedEngineTick = completedEngineTick;
        ArenaBytes = arenaBytes;
    }

    /// <summary>Gets the live store's size, in bytes, as of the read.</summary>
    public long ArenaBytes { get; }
    /// <summary>Gets the last completed simulation tick the read answers as of.</summary>
    public ulong CompletedTick { get; }
    /// <summary>Gets the engine-tick coordinate the read answers as of.</summary>
    public ulong CompletedEngineTick { get; }
    /// <summary>Gets the document as the reader may read it: every withheld cell removed from its row.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Gets a value indicating whether the reader is the operator, who reads the live document whole.</summary>
    public bool IsOperator => (Reader.Kind == PrincipalKind.Console);
    /// <summary>Gets the principal the view discloses to.</summary>
    public Principal Reader { get; }

    /// <summary>Creates the view one principal reads a server's state through.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="reader">The acting principal, as its ingress stamped it.</param>
    /// <returns>The view; for <see cref="Principal.Console"/>, the live document whole.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session has no live disclosed read view.</exception>
    public static WorldStateReadView Of(WorldServer server, Principal reader) {
        var view = Create(server, reader); view.m_server = reader.Kind == PrincipalKind.Session ? null : server;
        view.m_arena = view.m_server?.Arena ?? WorldStateReader.Seed(view.Definition); return view;
    }
    private static WorldStateReadView Create(WorldServer server, Principal reader) {
        ArgumentNullException.ThrowIfNull(argument: server);

        if (reader.Kind == PrincipalKind.Session) {
            var definition = (server.ReadSessionDefinition(session: reader)
                ?? throw new InvalidOperationException(message: WorldSessionObservation.QueryRefusal));

            // A session reads the same definition its deliveries carry. Visibility-only redaction of the authority
            // document would expose unobserved rows and authoritative sections at Presentation fidelity.
            return new WorldStateReadView(
                arenaBytes: (ReferenceEquals(objA: definition, objB: server.Definition) ? server.Arena.Bytes : 0L),
                completedEngineTick: server.CompletedEngineTicks,
                completedTick: (server.NextInputTick - 1UL),
                disclosed: new WorldStateDisclosed(
                    Definition: definition,
                    Withheld: new Dictionary<string, WorldWithheldRow>(comparer: StringComparer.Ordinal)
                ),
                reader: reader
            );
        }

        return new WorldStateReadView(
            arenaBytes: server.Arena.Bytes,
            completedEngineTick: server.CompletedEngineTicks,
            completedTick: (server.NextInputTick - 1UL),
            disclosed: ((reader.Kind == PrincipalKind.Console)
                ? new WorldStateDisclosed(
                    Definition: server.ExportDefinition(),
                    Withheld: new Dictionary<string, WorldWithheldRow>(comparer: StringComparer.Ordinal)
                )
                : WorldStateDisclosure.Disclose(
                    arena: server.Arena,
                    definition: server.ExportDefinition(),
                    recipient: reader
                )),
            reader: reader
        );
    }
    /// <summary>Returns the refusal a read of a withheld row or cell answers with, worded the same for a cell that
    /// is withheld and one that is absent, so a refusal never tells a reader which of a restricted row's keys
    /// exist.</summary>
    /// <param name="verb">The reading verb, for the echo.</param>
    /// <param name="row">The row's name.</param>
    /// <param name="key">The cell's key, or <see langword="null"/> for the row as a whole.</param>
    /// <returns>The bracketed refusal echo.</returns>
    public string Refusal(string verb, string row, string? key) => ((key is null)
        ? $"[{verb}: '{row}' is not disclosed to {Reader.Describe()} — its visibility withholds it]"
        : $"[{verb}: '{row}'.'{key}' is not disclosed to {Reader.Describe()} — its visibility withholds it]"
    );
    /// <summary>Returns what the reader's disclosure withheld from one row.</summary>
    /// <param name="row">The row's name.</param>
    /// <returns>The withheld record, or <see langword="null"/> when the row is disclosed whole.</returns>
    public WorldWithheldRow? Withheld(string row) {
        var withheldRows = m_server is null || IsOperator ? m_withheld : WorldStateDisclosure.Disclose(m_server.Configuration, m_server.Arena, Reader).Withheld;
        return (withheldRows.TryGetValue(
        key: row,
        value: out var withheld
    )
        ? withheld
        : null
    );
    }

    /// <summary>Reads through the live arena and checks disclosure at this read.</summary>
    public bool TryReadValue(string rowName, string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldStateRow? row, out CellValue value, bool eased = false) {
        var arena = m_server?.Arena ?? m_arena!;
        if (!IsOperator && m_server is not null && !WorldStateDisclosure.CanReadCell(m_server.Configuration, arena, rowName, CellName.Parse(key ?? StateRow.SlotKey.Value), Reader)) {
            row = WorldDefinitionRows.FindStateRow(Definition.State, rowName); value = default; return row is not null;
        }
        return eased ? WorldStateReader.TryReadEasedValue(Definition, rowName, key, CompletedTick, CompletedEngineTick, out row, out value, arena) :
            WorldStateReader.TryReadValue(Definition, rowName, key, CompletedTick, CompletedEngineTick, out row, out value, arena);
    }
}
