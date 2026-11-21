using Puck.Abstractions.Memory;
using Puck.Assets;
using Puck.World.Protocol;
using Puck.Maths;

namespace Puck.World;

/// <summary>A replacement computed from the current view and this stream's last delivered image.</summary>
public readonly record struct WorldRowReplacement(int Row, WorldRecipientImage Image);

/// <summary>One delivery stream's history. Definition and row delivery seed the same hashes; reads cannot move
/// them. Versions count observable deliveries in this stream, never authority installs.</summary>
/// <remarks><para>The cursor is the one place that knows what its stream was delivered, so it is the one place that
/// decides a cut and the one writer of the stream's generation (<see cref="Stamp"/>). A cut is the loss of something the
/// stream was delivered from a part of the world that can withhold from it, decided from two things the recipient itself
/// sees: what the stream last delivered and what its view discloses now. It happens when a cell held in a row that can
/// withhold (by its own policy, a cell policy or a containing zone) is missing from the row's image now (a row the stream
/// read whole and that begins to withhold among them, which cuts only when a cell it read is unreadable now), when the
/// stream stops observing (<see cref="Release"/>), and when the body policy changes to one that redacts
/// (<see cref="ObserveBodies"/>). A deletion in a row the recipient reads whole, a body leaving the recipient's projection
/// under an unchanged body policy, and a changed value in a cell it still reads are not cuts.</para>
/// <para>Every row that can withhold is imaged at every ceiling (<see cref="WorldRecipientView.ReadsWhole(int)"/>), so a
/// placement dealt from a row, a field region or a solid size leaves a recipient's projection only when a cell it reads
/// stops being readable, and the row rules decide those too. Because the rules read nothing but the delivered images and
/// the current disclosure, a change hidden throughout (a hidden install, a policy the recipient never passed added or
/// removed, a write to a row it never read) cannot move the generation.</para></remarks>
public sealed class WorldDeliveryCursor {
    // A row's image as the stream last held it, and whether the stream read the row whole then.
    private readonly record struct HeldRow(WorldRecipientImage Image, bool Whole);

    // Every row of the stream's document, by name, as it last held it.
    private readonly Dictionary<string, HeldRow> m_rows = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<int> m_metadataPrimers = [];
    // Advance's scratch: the rows a change can move, and the same in row order.
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_affected = new();
    private int[] m_ordered = [];
    private ContentPin? m_definition;
    private ContentPin? m_frame;
    private bool m_operator;
    // The stream's opaque identity, its span and the last record it stamped. The identity is random so that it says
    // nothing about the recipient, its policies or the table.
    private readonly ulong m_stream = SecureRandom.NextUInt<ulong>(maximum: ulong.MaxValue, minimum: 1UL);
    private long m_generation;
    private long m_ordinal;
    private bool m_seeded;
    private bool m_cutOwed;
    private WorldObserverDisclosureMode? m_bodyMode;
    private float? m_bodyRadius;

    /// <summary>The sequence this stream has delivered.</summary>
    public long Sequence { get; private set; }
    /// <summary>Gets the stream's current span: zero at admission, one more at each cut.</summary>
    public long Generation => m_generation;
    /// <summary>Stamps the stream's next record: the stream's identity, its current generation and the next ordinal.
    /// Every record a stream sends takes exactly one stamp, so the ordinals a reader receives rise by one.</summary>
    /// <returns>The record's stamp.</returns>
    public WorldStreamStamp Stamp() => new(Generation: m_generation, Ordinal: (m_ordinal = checked(m_ordinal + 1)), Stream: m_stream);
    /// <summary>Whether the replica's current disclosed document equals the one this stream delivered, including
    /// subsequent row snapshots. Used at a traveller's install boundary, never against the authority identity.</summary>
    public bool Holds(WorldRecipientView view) => m_definition is null || m_definition == view.Images.DefinitionIdentity(view);
    /// <summary>Whether a replica's disclosed document less its state rows (its placements among them) moved since
    /// this stream last delivered a document. A value-only change moves it when it changes what the recipient's
    /// placements read, a dealt cell turning readable or a zone move among them; a hidden-only change never does, so
    /// whether a value-only delivery carries the document is a function of the recipient's view alone.</summary>
    /// <param name="view">The stream's view.</param>
    /// <returns><see langword="true"/> when the stream owes a document for a value-only change.</returns>
    public bool FrameMoved(WorldRecipientView view) => view.Observes && (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica) &&
        (m_operator != view.IsAuthority || m_frame != view.Images.FrameIdentity(view));
    /// <summary>Delivers a definition only when its observable content differs, and seeds all its row images. The
    /// delivered document carries its audio rows' content inline (<see cref="WorldAssetRowLoader.EmbedAudio"/>), so a
    /// recipient in another process, or a routed view in this one, plays them without the authority's files.</summary>
    /// <param name="view">The stream's view.</param>
    /// <returns>The document owed, or <see langword="null"/> when the recipient already holds it.</returns>
    public WorldDefinition? DeliverDefinition(WorldRecipientView view) {
        if (!view.Observes || view.Ceiling != WorldDisclosureTier.Replica) { return null; }
        var identity = view.Images.DefinitionIdentity(view);
        m_frame = view.Images.FrameIdentity(view);
        if (identity == m_definition) { if (m_operator != view.IsAuthority) { Seed(view); } return null; }
        m_definition = identity;
        Seed(view);
        Delivered();
        return WorldAssetRowLoader.EmbedAudio(definition: view.Images.Definition(view));
    }

    /// <summary>Advances an already primed replica over a presentation edit, provided its disclosed state and
    /// policy still describe the same stream. A stream that needs a primer uses the ordinary definition door.</summary>
    public bool TryDeliverPresentation(WorldRecipientView view, WorldPresentationRows rows, out bool moved) {
        moved = false;
        if (rows.ChangesProjectionDependencies || !view.Observes || view.Ceiling != WorldDisclosureTier.Replica || m_cutOwed || m_definition is null ||
            m_operator != view.IsAuthority || !view.Images.TryAdvanceAppearance(view, m_frame, m_definition, out var frame, out var identity)) {
            return false;
        }
        if (m_frame == frame) { return true; }
        m_frame = frame;
        m_definition = identity;
        moved = true;
        Delivered();
        return true;
    }
    /// <summary>Records the images a presentation delivery actually carried.</summary>
    public void Delivered(WorldRecipientView view) { Seed(view); Delivered(); }
    /// <summary>Records one observable delivery, preserving the sequence across a withheld span.</summary>
    public void Delivered() => Sequence = checked(Sequence + 1);
    /// <summary>Records the body policy a delivery is redacted under, and cuts when it changes to one that redacts: a
    /// body the stream was shown may be one it is no longer shown. A body leaving the projection under an unchanged
    /// policy is no cut.</summary>
    /// <param name="policy">The live body policy; its cadence never cuts.</param>
    public void ObserveBodies(WorldObserverDisclosure policy) {
        var (mode, radius) = (policy.Mode, policy.Radius);
        if (m_bodyMode is { } held && ((held != mode) || (m_bodyRadius != radius)) && (mode != WorldObserverDisclosureMode.All)) { Cut(); }
        m_bodyMode = mode;
        m_bodyRadius = radius;
    }
    /// <summary>Takes the cut this cursor decided since the last take, once: when it returns <see langword="true"/>, the
    /// stream owes its reader a cut record under the new generation and then the whole disclosed document and a primer
    /// snapshot, before anything newer (<see cref="ForgetDocument"/> makes the next <see cref="DeliverDefinition"/>
    /// compose it whole).</summary>
    /// <returns><see langword="true"/> when a cut happened since the last take.</returns>
    public bool TakeCut() {
        var cut = m_cutOwed;
        m_cutOwed = false;
        return cut;
    }
    /// <summary>Gets whether the stream owes its reader the observation headers of restricted rows, which a delivered
    /// document cannot carry. The next snapshot advanced through this cursor carries them, whatever cells it
    /// holds.</summary>
    public bool OwesRows => (m_metadataPrimers.Count != 0);
    /// <summary>Gets whether a cut was decided that <see cref="TakeCut"/> has not yet taken: the stream owes its reader
    /// the cut record and the new generation's primer now, whatever its cadence.</summary>
    public bool CutOwed => m_cutOwed;
    /// <summary>Forgets the document identity this stream last delivered, so the next <see cref="DeliverDefinition"/>
    /// delivers the whole disclosed document even when its content did not move: a new generation is primed
    /// alone.</summary>
    public void ForgetDocument() => m_definition = null;
    private void Cut() {
        m_generation = checked(m_generation + 1);
        m_cutOwed = true;
    }
    private void Seed(WorldRecipientView view) {
        m_operator = view.IsAuthority;
        var cut = false;
        var seeded = m_seeded;
        // The previous delivery's rows, consumed as each current row is matched: a held row that is gone now was deleted
        // by an install the recipient sees, which is a loss only when its row could withhold.
        Dictionary<string, HeldRow>? previous = (m_rows.Count == 0) ? null : new(m_rows, StringComparer.Ordinal);
        m_rows.Clear();
        m_metadataPrimers.Clear();
        for (var row = 0; row < view.Source.AuthoredState.Count; row++) {
            var name = view.Arena.Rows[row].Name.Value;
            var whole = view.HoldsWhole(row);
            var image = view.Image(row);
            // The definition carries no observation header, including when its hidden count is zero. Whether
            // the following primer is owed depends on the row's policy, never on its current hidden members.
            if (!whole && (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica) && (image.Observation is not null)) { m_metadataPrimers.Add(row); }
            m_rows[name] = new HeldRow(image, whole);
            if (!seeded) { continue; }
            if ((previous is not null) && previous.Remove(name, out var held) && !whole) { cut |= Loses(held.Image, image); }
        }
        if (previous is not null) {
            foreach (var held in previous.Values) { cut |= (!held.Whole && (held.Image.Cells.Count != 0)); }
        }
        m_seeded = true;
        if (cut) { Cut(); }
    }
    // Whether an image now lacks a cell the stream was delivered in its held image: a cell withheld or removed, which a
    // row that can withhold must not let the recipient tell apart. A row the stream read whole held every cell it had,
    // so the same test decides a row that turns withholding: it cuts only when a cell the stream read is unreadable now.
    private static bool Loses(WorldRecipientImage held, WorldRecipientImage now) {
        if (ReferenceEquals(held, now) || (held.Cells.Count == 0)) { return false; }
        var current = now.Cells;
        if (current.Count <= 16) {
            foreach (var cell in held.Cells) {
                var found = false;
                foreach (var candidate in current) { if (candidate.Key == cell.Key) { found = true; break; } }
                if (!found) { return true; }
            }
            return false;
        }
        var keys = new HashSet<Puck.State.CellName>(current.Count);
        foreach (var candidate in current) { keys.Add(candidate.Key); }
        foreach (var cell in held.Cells) { if (!keys.Contains(cell.Key)) { return true; } }
        return false;
    }
    internal IReadOnlyList<WorldRowReplacement> Advance(WorldRecipientView view, ReadOnlySpan<int> rows) {
        // Nothing changed and no primer is owed: no row can need a replacement, so the audience is not composed.
        var authorityChanged = m_operator != view.IsAuthority;
        if (!authorityChanged && rows.IsEmpty && (m_metadataPrimers.Count == 0)) { return []; }
        List<WorldRowReplacement>? result = null;
        string? audience = null;
        var cut = false;
        m_affected.Clear();
        view.Images.Affected(view, rows, m_affected);
        if (authorityChanged) { for (var row = 0; row < view.Source.AuthoredState.Count; row++) { m_affected.Add(row); } m_operator = view.IsAuthority; }
        foreach (var primer in m_metadataPrimers) { m_affected.Add(index: primer); }
        ArrayCapacity.EnsureScratch(ref m_ordered, m_affected.Count);
        var ordered = m_ordered.AsSpan(0, m_affected.Count);
        m_affected.Members.CopyTo(ordered);
        ordered.Sort();
        foreach (var row in ordered) {
            var name = view.Arena.Rows[row].Name.Value;
            var whole = view.HoldsWhole(row);
            var image = view.Images.Image(view, row, audience ??= view.Images.Audience(view));
            var holds = m_rows.TryGetValue(name, out var held);
            if (whole) {
                // A row the stream reads whole is held whole; one turning whole replaces its prior restricted image once.
                m_rows[name] = new HeldRow(image, Whole: true);
                if (holds && !held.Whole) { m_metadataPrimers.Remove(row); (result ??= []).Add(new(row, image)); }
                continue;
            }
            if (holds) {
                cut |= Loses(held.Image, image);
                if (!m_metadataPrimers.Remove(row) && !held.Whole && held.Image.Identity == image.Identity) { continue; }
            } else {
                m_metadataPrimers.Remove(row);
            }
            m_rows[name] = new HeldRow(image, Whole: false);
            (result ??= []).Add(new(row, image));
        }
        if (cut) { Cut(); }
        if (result is null) { return []; }
        DeliveredRows(view);
        return result;
    }
    /// <summary>Records a snapshot's delivered rows in the document identity too, so a later hidden-only install
    /// cannot resend public values already received through snapshots.</summary>
    public void DeliveredRows(WorldRecipientView view) {
        if (m_definition is not null) { m_definition = view.Images.DefinitionIdentity(view); }
        Delivered();
    }
    /// <summary>Releases content after withholding or detachment, and cuts: the stream stops observing, so everything
    /// it was delivered is lost to it. A resumed delivery seeds it again under the new generation, while the stream's
    /// sequence continues.</summary>
    public void Release() {
        if (m_seeded) { Cut(); }
        m_rows.Clear(); m_metadataPrimers.Clear(); m_definition = null; m_seeded = false;
    }
}
