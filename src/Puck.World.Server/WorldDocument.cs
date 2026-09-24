using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The document facade of <see cref="WorldServer"/>: the live <see cref="WorldDefinition"/>, the journal base it is
/// an edit history over, the buffered live-edit ops, the mutation compose and apply pipeline, the generate arm, the
/// state-transform admission, the solid field, and the per-tick delivery decision.
/// </summary>
/// <remarks>Every durable change flows through here — a mutation composes a candidate, the whole document
/// revalidates, capacity is checked, the swap is atomic, the journal records it, and the changed derived state
/// rebuilds. Undo replays journal-minus-tail through the same gates.</remarks>
public sealed partial class WorldDocument {
    // The buffered live-edit ops (mutations, whole-document swaps, journal undo), drained FIFO at the step boundary
    // BEFORE intents. New allocation lives here, at the mutation boundary; an idle tick pays one empty-queue check.
    private readonly Queue<WorldPendingOp> m_pending = new();
    // The mutation journal — the undo engine. m_base is the loaded base definition (reset by a swap or world.save
    // compaction); m_journal is the append-only edit history over it. dirty == m_journal.Count.
    private readonly List<WorldJournalEntry> m_journal = new();
    // A human-readable description of m_base's origin — world.reset's read-back rule ("the completion echo names
    // what was reset to"). Set at construction (the boot document), replaced by Compact (world.save — "the last
    // world.save") and by ApplyRebuild's Load/Reload arm (a new base replaces the old one, exactly like a swap
    // always has). Reset itself never writes this: reset targets the base WITHOUT moving it.
    private string m_baseOrigin = "the boot document";

    private WorldDefinition m_base;
    private WorldDefinition m_definition;
    private WorldDocumentSubmissionReceipt? m_lastDocumentReceipt;
    // The solid-field revision — bumped each time m_solids is rebuilt (a solid-affecting edit under the field provider),
    // the world.collision.status read-back. Starts at 1 when the boot world uses the field provider, else 0.
    private int m_solidRevision;
    // The live SDF contact field under the FIELD provider (null under analytic) — the server OWNS this
    // provider's lifecycle: it is built ONCE at apply time (for its loud excluded-op rejection) and handed to the
    // population's rebuild, so a body's first step after a solid edit already solves against the new field. Adopted at
    // construction from the population's boot build so it is never compiled twice for one boundary.
    private WorldSolidField? m_solids;

    private readonly WorldServer m_host;

    /// <summary>Gets the loaded base definition the journal is an edit history over.</summary>
    internal WorldDefinition Base => m_base;
    /// <summary>Gets a human-readable description of the base's origin — the completion echo names what a reset
    /// targets.</summary>
    internal string BaseOrigin => m_baseOrigin;
    /// <summary>Gets the live world definition, swapped in place as buffered edits apply.</summary>
    internal WorldDefinition Definition => m_definition;

    /// <summary>Gets the server whose arena, entity table, grants and narration this pipeline runs against.</summary>
    private WorldServer Host => m_host;

    /// <summary>Gets the applied-mutation journal over the base.</summary>
    internal List<WorldJournalEntry> Journal => m_journal;
    /// <summary>Gets the journal length — the number of applied mutations over the base.</summary>
    internal int JournalLength => m_journal.Count;
    /// <summary>Gets or sets the most recent visiting-world durable-state verdict.</summary>
    internal WorldDocumentSubmissionReceipt? LastDocumentReceipt {
        get => m_lastDocumentReceipt;
        set => m_lastDocumentReceipt = value;
    }
    /// <summary>Gets the detail line the last refused mutation left, or <see langword="null"/> when the last one
    /// applied.</summary>
    internal string? LastMutationFailureDetail {
        get => m_lastMutationFailureDetail;
        set => m_lastMutationFailureDetail = value;
    }
    /// <summary>Gets or sets whether a definition delivery is pending for the next step.</summary>
    internal bool PendingDefinitionDelivery {
        get => m_pendingDefinitionDelivery;
        set => m_pendingDefinitionDelivery = value;
    }
    /// <summary>Gets the buffered live-edit ops, drained FIFO at the step boundary before intents.</summary>
    internal Queue<WorldPendingOp> Pending => m_pending;

    /// <summary>Gets the live SDF contact field under the field provider, or <see langword="null"/> under the
    /// analytic one.</summary>
    public WorldSolidField? SolidField => m_solids;

    /// <summary>Gets the solid-field revision, bumped each time the field is rebuilt.</summary>
    internal int SolidRevision => m_solidRevision;

    /// <summary>Initializes the facade over the server it runs against and the document it boots on.</summary>
    /// <param name="host">The owning server.</param>
    /// <param name="definition">The boot document — the initial live definition and journal base.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal WorldDocument(WorldServer host, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: host);

        m_base = definition;
        m_definition = definition;
        m_host = host;
    }

    /// <summary>Replaces the journal base and names its new origin, without touching the live definition.</summary>
    /// <param name="definition">The new base.</param>
    /// <param name="origin">A human-readable description of where it came from.</param>
    internal void AdoptBase(WorldDefinition definition, string origin) {
        m_base = definition;
        m_baseOrigin = origin;
    }
    /// <summary>Swaps the live definition.</summary>
    /// <param name="definition">The new live definition.</param>
    internal void AdoptDefinition(WorldDefinition definition) => m_definition = definition;

    /// <summary>Adopts a rebuilt solid field and bumps its revision.</summary>
    /// <param name="solids">The new field, or <see langword="null"/> under the analytic provider.</param>
    private void AdoptSolidField(WorldSolidField? solids) {
        m_solidRevision++;
        m_solids = solids;
    }

    /// <summary>Compacts the journal: the live definition becomes the new base and the edit history is cleared.</summary>
    internal void Compact() {
        m_base = m_definition;
        m_baseOrigin = "the last world.save";
        m_journal.Clear();
    }
    /// <summary>Restores the solid field and its revision verbatim from a checkpoint.</summary>
    /// <param name="solids">The captured field.</param>
    /// <param name="revision">The captured revision.</param>
    internal void RestoreSolidField(WorldSolidField? solids, int revision) {
        m_solidRevision = revision;
        m_solids = solids;
    }
}
