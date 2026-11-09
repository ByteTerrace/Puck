using Puck.Assets;

namespace Puck.World;

/// <summary>A replacement computed from the current view and this stream's last delivered image.</summary>
public readonly record struct WorldRowReplacement(int Row, WorldRecipientImage Image);

/// <summary>One delivery stream's history. Definition and row delivery seed the same hashes; reads cannot move
/// them. Versions count observable deliveries in this stream, never authority installs.</summary>
public sealed class WorldDeliveryCursor {
    private readonly Dictionary<string, ContentPin> m_rows = [];
    private readonly HashSet<int> m_metadataPrimers = [];
    // Advance's scratch: the rows a change can move, and the same in row order.
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_affected = new();
    private int[] m_ordered = [];
    private ContentPin? m_definition;
    private ContentPin? m_frame;

    /// <summary>The sequence this stream has delivered.</summary>
    public long Sequence { get; private set; }
    /// <summary>The number of restricted rows retained by this stream.</summary>
    public int RetainedRows => m_rows.Count;
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
        (m_frame != view.Images.FrameIdentity(view));
    /// <summary>Delivers a definition only when its observable content differs, and seeds all its row images.</summary>
    public WorldDefinition? DeliverDefinition(WorldRecipientView view) {
        if (!view.Observes) { return null; }
        var replica = (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica);
        var presented = replica ? null : view.Definition;
        var identity = replica ? view.Images.DefinitionIdentity(view) : ContentPin.Compute(WorldDefinitionSerialization.SerializeCompact(presented!));
        if (replica) { m_frame = view.Images.FrameIdentity(view); }
        if (identity == m_definition) { return null; }
        m_definition = identity;
        Seed(view);
        Delivered();
        return presented ?? view.Images.Definition(view);
    }
    /// <summary>Records the images a presentation delivery actually carried.</summary>
    public void Delivered(WorldRecipientView view) { Seed(view); Delivered(); }
    /// <summary>Records one observable delivery, preserving the sequence across a withheld span.</summary>
    public void Delivered() => Sequence = checked(Sequence + 1);
    private void Seed(WorldRecipientView view) {
        m_rows.Clear();
        m_metadataPrimers.Clear();
        for (var row = 0; row < view.Source.AuthoredState.Count; row++) {
            if (!view.ReadsWhole(row)) {
                var image = view.Image(row);
                // The definition carries no observation header, including when its hidden count is zero. Whether
                // the following primer is owed depends on the row's policy, never on its current hidden members.
                if (view.Ceiling == Puck.World.Protocol.WorldDisclosureTier.Replica && image.Observation is not null) { m_metadataPrimers.Add(row); }
                m_rows[view.Arena.Rows[row].Name.Value] = image.Identity;
            }
        }
    }
    internal IReadOnlyList<WorldRowReplacement> Advance(WorldRecipientView view, ReadOnlySpan<int> rows) {
        // Nothing changed and no primer is owed: no row can need a replacement, so the audience is not composed.
        if (rows.IsEmpty && (m_metadataPrimers.Count == 0)) { return []; }
        List<WorldRowReplacement>? result = null;
        string? audience = null;
        m_affected.Clear();
        view.Images.Affected(view, rows, m_affected);
        foreach (var primer in m_metadataPrimers) { m_affected.Add(index: primer); }
        if (m_ordered.Length < m_affected.Count) { m_ordered = new int[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)m_affected.Count)]; }
        var ordered = m_ordered.AsSpan(0, m_affected.Count);
        m_affected.Members.CopyTo(ordered);
        ordered.Sort();
        foreach (var row in ordered) {
            var name = view.Arena.Rows[row].Name.Value;
            if (view.ReadsWhole(row) && !m_rows.ContainsKey(name)) { continue; }
            var image = view.Images.Image(view, row, audience ??= view.Images.Audience(view));
            if (view.ReadsWhole(row)) { m_rows.Remove(name); (result ??= []).Add(new(row, image)); continue; }
            if (!m_metadataPrimers.Remove(row) && m_rows.TryGetValue(name, out var held) && held == image.Identity) { continue; }
            m_rows[name] = image.Identity;
            (result ??= []).Add(new(row, image));
        }
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
    /// <summary>Releases content after withholding or detachment. A resumed delivery seeds it again, while the
    /// stream's sequence continues.</summary>
    public void Release() { m_rows.Clear(); m_metadataPrimers.Clear(); m_definition = null; }
}
