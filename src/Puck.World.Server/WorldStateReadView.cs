using Puck.Commands;

namespace Puck.World.Server;

/// <summary>One principal's read of a live world's state: the document as <see cref="WorldRecipientView"/>
/// discloses it to that principal, and the refusal a read of anything withheld answers with.
/// <para>A console read-back verb reads state through this view rather than through <see cref="WorldServer.Definition"/>,
/// so a seat, an addon or a peer is shown what its visibility admits and nothing else. The operator — the
/// <see cref="PrincipalKind.Console"/> principal — reads the live document whole.</para></summary>
public sealed class WorldStateReadView {
    private readonly WorldRecipientView m_view;

    private WorldStateReadView(WorldServer server, Principal reader) {
        Reader = reader;
        m_view = server.ReadView(reader);
        CompletedTick = server.NextInputTick - 1UL;
        CompletedEngineTick = server.CompletedEngineTicks;
        ArenaBytes = IsOperator ? server.Arena.Bytes : 0L;
    }

    /// <summary>The live capability behind this console adapter.</summary>
    public WorldRecipientView Recipient => m_view;
    /// <summary>Gets the operator's live store size, or zero for a restricted reader: reservations reveal hidden membership.</summary>
    public long ArenaBytes { get; }
    /// <summary>Gets the last completed simulation tick the read answers as of.</summary>
    public ulong CompletedTick { get; }
    /// <summary>Gets the engine-tick coordinate the read answers as of.</summary>
    public ulong CompletedEngineTick { get; }
    /// <summary>Gets the document as the reader may read it: every withheld cell removed from its row.</summary>
    public WorldDefinition Definition => m_view.Definition;
    /// <summary>Gets a value indicating whether the reader is the operator, who reads the live document whole.</summary>
    public bool IsOperator => (Reader.Kind is PrincipalKind.Console or PrincipalKind.World);
    /// <summary>Gets the principal the view discloses to.</summary>
    public Principal Reader { get; }

    /// <summary>Creates the view one principal reads a server's state through.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="reader">The acting principal, as its ingress stamped it.</param>
    /// <returns>The view; for <see cref="Principal.Console"/>, the live document whole.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session has no live disclosed read view.</exception>
    public static WorldStateReadView Of(WorldServer server, Principal reader) {
        ArgumentNullException.ThrowIfNull(server);
        return new WorldStateReadView(server, reader);
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
    /// <summary>Returns what the reader's disclosure withholds from one row, read from the live store at this call.
    /// A row whose declared policy can withhold from the reader (<see cref="WorldRecipientView.ReadsWhole(int)"/>)
    /// answers a record whether zero or many of its cells are withheld now, so a door that refuses or reshapes a
    /// restricted row does so by policy, never by current membership.</summary>
    /// <param name="row">The row's name.</param>
    /// <returns>The withheld record, or <see langword="null"/> when the row reads whole for the reader.</returns>
    public WorldWithheldRow? Withheld(string row) => m_view.Withheld(row);

    /// <summary>Reads the disclosed document at its ceiling, including authored clock evaluation.</summary>
    public bool TryReadValue(string rowName, string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldStateRow? row, out CellValue value, bool eased = false) {
        var definition = Definition;
        return eased ? WorldStateReader.TryReadEasedValue(definition, rowName, key, CompletedTick, CompletedEngineTick, out row, out value) :
            WorldStateReader.TryReadValue(definition, rowName, key, CompletedTick, CompletedEngineTick, out row, out value);
    }
}
