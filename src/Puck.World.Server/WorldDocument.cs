using System.Collections.Immutable;
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
    // The edit journal — the undo engine. m_base is the loaded base definition (reset by a swap or world.save
    // compaction); m_journal is the edit history over it. Every change replaces the list (SetJournal, AppendJournal), so
    // one instance names one journal, and a history keyframe proves the live journal is its own by reference.
    private ImmutableList<WorldJournalEntry> m_journal = [];
    // A human-readable description of m_base's origin — world.reset's read-back rule ("the completion echo names
    // what was reset to"). Set at construction (the boot document), replaced by Compact (world.save — "the last
    // world.save") and by ApplyRebuild's Load/Reload arm (a new base replaces the old one, exactly like a swap
    // always has). Reset itself never writes this: reset targets the base WITHOUT moving it.
    private WorldBaseOrigin m_baseOrigin = WorldBaseOrigin.Boot;

    private WorldDefinition m_base;
    private WorldDefinition m_definition;
    // The install ordinal of m_definition within this activation (WorldDocumentVersion.Sequence): bumped by
    // AdoptDefinition, the one door every live-definition install goes through.
    private long m_sequence;
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
    // The source revision a reload's delta is cut from: the boot document, then each loaded or reloaded source.
    private WorldSourceRevision m_revision;

    /// <summary>Gets the loaded base definition the journal is an edit history over.</summary>
    internal WorldDefinition Base => m_base;

    /// <summary>Gets where the journal base came from — what a reset lands on, as its completion echo names it.</summary>
    public WorldBaseOrigin BaseOrigin => m_baseOrigin;

    /// <summary>Gets the live world definition, swapped in place as buffered edits apply.</summary>
    internal WorldDefinition Definition => m_definition;
    /// <summary>Gets the install ordinal of the live definition within this activation: zero for the boot document,
    /// one more at every install since.</summary>
    internal long Sequence => m_sequence;

    /// <summary>Gets the revision of the world's source this authority last took.</summary>
    internal WorldSourceRevision Revision => m_revision;

    /// <summary>Gets the server whose arena, entity table, grants and narration this pipeline runs against.</summary>
    private WorldServer Host => m_host;

    /// <summary>Gets the edit journal over the base: an immutable list each change replaces.</summary>
    internal ImmutableList<WorldJournalEntry> Journal => m_journal;
    /// <summary>Gets the journal length — the number of entries over the base, undone ones included.</summary>
    internal int JournalLength => m_journal.Count;

    // How many of a principal's own edits the journal holds for world.undo to reach, and how many of its undone edits
    // for world.redo: the entries it authored (WorldJournalEntry.Authored and Author), split by their Undone mark.
    internal (int Undoable, int Redoable) JournalDepths(Puck.Commands.Principal principal) {
        var undoable = 0;
        var redoable = 0;

        foreach (var entry in m_journal) {
            if (!entry.Authored || (entry.Author != principal)) { continue; }
            if (entry.Undone) { redoable++; } else { undoable++; }
        }

        return (undoable, redoable);
    }

    /// <summary>Replaces the edit journal.</summary>
    /// <param name="journal">The journal the base now carries.</param>
    internal void SetJournal(ImmutableList<WorldJournalEntry> journal) => m_journal = journal;

    // Appends an entry. An entry its author made clears that author's undone entries: a principal's next edit ends what
    // that principal could redo.
    private void AppendJournal(WorldJournalEntry entry) {
        var journal = m_journal;

        if (entry.Authored) {
            var author = entry.Author;
            journal = journal.RemoveAll(match: held => (held.Undone && (held.Author == author)));
        }
        m_journal = journal.Add(value: entry);
    }
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
        m_revision = new WorldSourceRevision(ContentHash: string.Empty, Definition: definition);
        WorldContentIndex.Register(definition: definition);
    }

    /// <summary>Takes a revision of the world's source as the one later reloads are cut from.</summary>
    /// <param name="revision">The revision.</param>
    internal void AdoptRevision(WorldSourceRevision revision) => m_revision = revision;

    /// <summary>Replaces the journal base and names its new origin, without touching the live definition.</summary>
    /// <param name="definition">The new base.</param>
    /// <param name="origin">A human-readable description of where it came from.</param>
    internal void AdoptBase(WorldDefinition definition, WorldBaseOrigin origin) {
        m_base = definition;
        m_baseOrigin = origin;
    }
    /// <summary>Swaps the live definition and moves its install ordinal forward. It is the one door every live
    /// definition passes through, so it is also where a replaced <c>admission</c> section is announced: an install, an
    /// undo, a seek and a checkpoint restore all reach <see cref="WorldServer.AdmissionChanged"/> without each naming
    /// it. It is where the content the definition names by pin is indexed (<see cref="WorldContentIndex"/>) for the same
    /// reason: every world this process runs has its content findable by pin before anything reads it.</summary>
    /// <param name="definition">The new live definition.</param>
    internal void AdoptDefinition(WorldDefinition definition) {
        var previous = m_definition;

        m_definition = definition;
        WorldContentIndex.Register(definition: definition, previous: previous);
        if (Host.Arena is { } arena) { WorldStateReader.Bind(definition, arena); }
        m_sequence++;
        if (!ReferenceEquals(objA: previous.Admission, objB: definition.Admission)) {
            Host.NotifyAdmissionChanged();
        }
    }

    /// <summary>Adopts a rebuilt solid field and bumps its revision.</summary>
    /// <param name="solids">The new field, or <see langword="null"/> under the analytic provider.</param>
    private void AdoptSolidField(WorldSolidField? solids) {
        m_solidRevision++;
        SetSolids(solids: solids);
    }

    /// <summary>Compacts the journal: the live definition becomes the new base and the edit history is cleared.</summary>
    internal void Compact() {
        m_base = Host.MaterializeArena();
        m_baseOrigin = new WorldBaseOrigin(Kind: WorldBaseOriginKind.Save);
        m_journal = [];
    }
    /// <summary>Restores the solid field and its revision verbatim from a checkpoint.</summary>
    /// <param name="solids">The captured field.</param>
    /// <param name="revision">The captured revision.</param>
    internal void RestoreSolidField(WorldSolidField? solids, int revision) {
        m_solidRevision = revision;
        SetSolids(solids: solids);
    }
}
