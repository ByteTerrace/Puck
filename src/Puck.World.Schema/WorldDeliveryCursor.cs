using Puck.Assets;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A replacement computed from the current view and this stream's last delivered image.</summary>
public readonly record struct WorldRowReplacement(int Row, WorldRecipientImage Image);

/// <summary>One delivery stream's history. Definition and row delivery seed the same hashes; reads cannot move
/// them. Versions count observable deliveries in this stream, never authority installs.</summary>
/// <remarks><para>The cursor is the one place that knows what its stream was delivered, so it is the one place that
/// decides a cut and the one writer of the stream's generation (<see cref="Stamp"/>). A cut is the loss of something the
/// stream was delivered from a part of the world that can withhold from it, decided from two things the recipient itself
/// sees: what the stream last delivered and what its view discloses now. It happens when a cell held in a row that can
/// withhold (by its own policy, a cell policy or a containing zone) is missing from the row's image now, when a row the
/// stream read whole begins to withhold from it, when the stream stops observing (<see cref="Release"/>), and when the
/// body policy changes to one that redacts (<see cref="ObserveBodies"/>). A deletion in a row the recipient reads whole,
/// a body leaving the recipient's projection under an unchanged body policy, and a changed value in a cell it still
/// reads are not cuts. A placement, a field region or a solid size leaves a recipient's projection only when a row it
/// reads stops being readable, so the row rules decide those too. Because the rules read nothing but the delivered
/// images and the current disclosure, a change hidden throughout (a hidden install, a policy the recipient never passed
/// added or removed, a write to a row it never read) cannot move the generation.</para></remarks>
public sealed class WorldDeliveryCursor {
    // The rows that can withhold from this stream, by name, and the image each last delivered; and the rows it was
    // delivered whole, whose turning restricted is a cut.
    private readonly Dictionary<string, WorldRecipientImage> m_rows = [];
    private readonly HashSet<string> m_wholeRows = new(comparer: StringComparer.Ordinal);
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
    /// <summary>The number of restricted rows retained by this stream.</summary>
    public int RetainedRows => m_rows.Count;
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
    /// <summary>Delivers a definition only when its observable content differs, and seeds all its row images.</summary>
    public WorldDefinition? DeliverDefinition(WorldRecipientView view) {
        if (!view.Observes) { return null; }
        var replica = (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica);
        var presented = replica ? null : view.Definition;
        var identity = replica ? view.Images.DefinitionIdentity(view) : ContentPin.Compute(WorldDefinitionSerialization.SerializeCompact(presented!));
        if (replica) { m_frame = view.Images.FrameIdentity(view); }
        if (identity == m_definition) { if (m_operator != view.IsAuthority) { Seed(view); } return null; }
        m_definition = identity;
        Seed(view);
        Delivered();
        return presented ?? view.Images.Definition(view);
    }
    /// <summary>Records the images a presentation delivery actually carried.</summary>
    public void Delivered(WorldRecipientView view) { Seed(view); Delivered(); }
    /// <summary>Records one observable delivery, preserving the sequence across a withheld span.</summary>
    public void Delivered() => Sequence = checked(Sequence + 1);
    /// <summary>Records the body policy a delivery is redacted under, and cuts when it changes to one that redacts: a
    /// body the stream was shown may be one it is no longer shown. A body leaving the projection under an unchanged
    /// policy is no cut.</summary>
    /// <param name="mode">The policy's mode.</param>
    /// <param name="radius">The policy's radius, in metres, for <see cref="WorldObserverDisclosureMode.Radius"/>.</param>
    public void ObserveBodies(WorldObserverDisclosureMode mode, float? radius) {
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
        Dictionary<string, WorldRecipientImage>? previous = (m_rows.Count == 0) ? null : new(m_rows, StringComparer.Ordinal);
        HashSet<string>? previousWhole = (m_wholeRows.Count == 0) ? null : new(m_wholeRows, StringComparer.Ordinal);
        m_rows.Clear();
        m_wholeRows.Clear();
        m_metadataPrimers.Clear();
        for (var row = 0; row < view.Source.AuthoredState.Count; row++) {
            var name = view.Arena.Rows[row].Name.Value;
            if (view.ReadsWhole(row)) { m_wholeRows.Add(name); previous?.Remove(name); continue; }
            var image = view.Image(row);
            // The definition carries no observation header, including when its hidden count is zero. Whether
            // the following primer is owed depends on the row's policy, never on its current hidden members.
            if (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica && image.Observation is not null) { m_metadataPrimers.Add(row); }
            m_rows[name] = image;
            if (!seeded) { continue; }
            if (previous is not null && previous.Remove(name, out var held)) { cut |= Loses(held, image); }
            else if (previousWhole?.Contains(name) == true && view.Includes(row)) { cut = true; }
        }
        if (previous is not null) {
            foreach (var held in previous.Values) { cut |= (held.Cells.Count != 0); }
        }
        m_seeded = true;
        if (cut) { Cut(); }
    }
    // Whether an image now lacks a cell the stream was delivered in its held image: a cell withheld or removed, which a
    // row that can withhold must not let the recipient tell apart.
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
        if (m_ordered.Length < m_affected.Count) { m_ordered = new int[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)m_affected.Count)]; }
        var ordered = m_ordered.AsSpan(0, m_affected.Count);
        m_affected.Members.CopyTo(ordered);
        ordered.Sort();
        foreach (var row in ordered) {
            var name = view.Arena.Rows[row].Name.Value;
            var whole = view.ReadsWhole(row);
            if (whole && !m_rows.ContainsKey(name)) { m_wholeRows.Add(name); continue; }
            var image = view.Images.Image(view, row, audience ??= view.Images.Audience(view));
            if (whole) { m_rows.Remove(name); m_wholeRows.Add(name); (result ??= []).Add(new(row, image)); continue; }
            var heldWhole = m_wholeRows.Remove(name);
            if (m_rows.TryGetValue(name, out var held)) {
                cut |= Loses(held, image);
                if (!m_metadataPrimers.Remove(row) && held.Identity == image.Identity) { continue; }
            } else {
                cut |= (heldWhole && view.Includes(row));
                m_metadataPrimers.Remove(row);
            }
            m_rows[name] = image;
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
        m_rows.Clear(); m_wholeRows.Clear(); m_metadataPrimers.Clear(); m_definition = null; m_seeded = false;
    }
}
