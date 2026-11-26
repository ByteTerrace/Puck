using Puck.Commands;
using Puck.State.Rules;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The authority's view of the tick's pointing contacts on its pointer surfaces: what a rule sweeping
/// <c>forEach: contacts(&lt;surface&gt;)</c> binds and what <c>contact(each, …)</c> reads.</summary>
public interface IWorldSurfaceContacts : IFacet {
    /// <summary>Appends the key of every contact event this tick whose ray lands on the sweep's surface and whose
    /// participant holds <c>Control</c> over it: the local seats in order, then the live sessions by ordinal, each
    /// participant's events in the order they happened.</summary>
    /// <param name="sweep">The sweep.</param>
    /// <param name="into">The list the keys are appended to.</param>
    void CollectContacts(WorldContactIteration sweep, List<CellKey> into);
    /// <summary>Returns the identity of the contact event a key names this tick: a fresh value every tick, since a
    /// contact event lives for the one tick that admitted it.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The generation.</returns>
    long ContactGeneration(CellKey key);
}
/// <summary>A compiled <c>forEach: contacts(&lt;surface&gt;)</c> sweep (also spelled <c>$contacts:&lt;surface&gt;</c>):
/// one binding of <c>$each</c> per pointing-contact event this tick (a finger, a pen tip, or the mouse while its primary
/// button is held) whose ray lands on one pointer surface: a <c>Simulation</c> screen by its index, or a placement
/// carrying a <see cref="WorldPointerSurface"/> by its id, the surfaces a <c>pointer(…)</c> read names. Mouse and touch
/// arrive through this one door and differ only in their device, so a surface's rules answer a tap the same way
/// whichever made it. A binding's key is its participant's slot times <see cref="SurfaceContact.MaximumPerTick"/> plus
/// the event's position in that participant's tick.</summary>
/// <param name="Surface">The surface as authored: a screen's index or a placement's id.</param>
/// <param name="Subject">What a participant must hold <c>Control</c> over for its contacts to reach the sweep: the
/// screen, or the placement carrying the surface.</param>
/// <param name="Mapping">The surface's source-normalized mapping, already validated, so the tick maps every ray in
/// fixed point from document data.</param>
/// <param name="Regions">The surface's named regions a <c>contact(each, region|row, &lt;id&gt;)</c> read hit-tests, or
/// <see langword="null"/> for none.</param>
/// <param name="Participants">The participants one sweep can visit: the document's local seats plus its network
/// players.</param>
public sealed record WorldContactIteration(string Surface, GrantSubject Subject, SourceMapping Mapping, IReadOnlyList<WorldPointerRegion>? Regions, int Participants) : RuleIteration(Participants * SurfaceContact.MaximumPerTick) {
    /// <summary>The reserved channel the sweep's spelling <c>contacts(&lt;surface&gt;)</c> parses to.</summary>
    public const string Channel = "contacts";

    /// <summary>Returns the participant slot and event position a key's index encodes.</summary>
    /// <param name="index">The key's index.</param>
    /// <returns>The participant slot (a local seat, then the network players by session ordinal) and the event's
    /// position within its participant's tick.</returns>
    public static (int Participant, int Event) Decode(int index) => Math.DivRem(left: index, right: SurfaceContact.MaximumPerTick);
    /// <summary>Returns the key index of a participant's event.</summary>
    /// <param name="participant">The participant slot.</param>
    /// <param name="eventIndex">The event's position within its participant's tick.</param>
    /// <returns>The index.</returns>
    public static int Encode(int participant, int eventIndex) => ((participant * SurfaceContact.MaximumPerTick) + eventIndex);

    /// <inheritdoc/>
    public override void Collect(IEffectHost host, List<CellKey> into) => ((IWorldSurfaceContacts)host).CollectContacts(sweep: this, into: into);
    /// <inheritdoc/>
    public override long Generation(IEffectHost host, CellKey key) => ((IWorldSurfaceContacts)host).ContactGeneration(key: key);
}
/// <summary>What <c>contact(each, …)</c> reads of the contact event a <see cref="WorldContactIteration"/> bound.</summary>
public enum ContactFacet : byte {
    /// <summary>The participant: a local seat's 1-based number, or a session's network slot after the local seats.</summary>
    Seat,
    /// <summary>The device: mouse 1, touch 2, pen 3 (<see cref="ContactDevice"/>).</summary>
    Device,
    /// <summary>The contact's number: 0 for the mouse, from 1 for a finger or pen tip, stable while it is down.</summary>
    Id,
    /// <summary>The phase: down 1, move 2, up 3, cancel 4 (<see cref="ContactPhase"/>).</summary>
    Phase,
    /// <summary>The source-normalized horizontal fraction where the ray lands, <c>x</c> right, in <c>[0, 1)</c>.</summary>
    X,
    /// <summary>The source-normalized vertical fraction where the ray lands, <c>y</c> down, in <c>[0, 1)</c>.</summary>
    Y,
    /// <summary><c>1</c> while the ray lands inside one named region of the surface, <c>0</c> otherwise.</summary>
    Region,
    /// <summary>The row of one named region the ray lands in, counted from the top (<see cref="WorldPointerRegion.RowOf"/>),
    /// or <c>-1</c> while it lands outside the region.</summary>
    Row,
}
/// <summary>One facet of the contact event a <c>contacts(&lt;surface&gt;)</c> sweep bound to <c>$each</c>.</summary>
public sealed class ContactOperand : WorldOperandFact {
    /// <summary>Initializes a new instance of the <see cref="ContactOperand"/> class.</summary>
    /// <param name="facet">The facet read.</param>
    /// <param name="sweep">The sweep whose binding is read, which names the surface the ray maps through.</param>
    /// <param name="region">The region a <see cref="ContactFacet.Region"/> or <see cref="ContactFacet.Row"/> read
    /// hit-tests, or <see langword="null"/> for any other facet.</param>
    /// <exception cref="ArgumentException"><paramref name="facet"/> is <see cref="ContactFacet.Region"/> or
    /// <see cref="ContactFacet.Row"/> and <paramref name="region"/> is <see langword="null"/>.</exception>
    public ContactOperand(ContactFacet facet, WorldContactIteration sweep, WorldPointerRegion? region = null) : base((facet is ContactFacet.X or ContactFacet.Y) ? CellKind.Fixed : CellKind.Int) {
        if ((facet is ContactFacet.Region or ContactFacet.Row) && (region is null)) {
            throw new ArgumentException(message: $"A {facet} read names the region it hit-tests.", paramName: nameof(region));
        }

        Facet = facet;
        Region = region;
        Sweep = sweep;
    }

    /// <summary>Gets the facet read.</summary>
    public ContactFacet Facet { get; }
    /// <summary>Gets the region a <see cref="ContactFacet.Region"/> or <see cref="ContactFacet.Row"/> read hit-tests,
    /// or <see langword="null"/>.</summary>
    public WorldPointerRegion? Region { get; }
    /// <summary>Gets the sweep whose binding is read.</summary>
    public WorldContactIteration Sweep { get; }

    /// <summary>Returns the facet's raw value for a contact event its participant made.</summary>
    /// <param name="contact">The contact event.</param>
    /// <param name="seat">The participant's number, as <see cref="ContactFacet.Seat"/> reads it.</param>
    /// <returns>The raw value in <see cref="WorldOperandFact.ValueKind"/>'s encoding; for a ray that does not land on
    /// the surface, <c>-1</c> for a row and zero for every other place facet.</returns>
    public long Read(in SurfaceContact contact, int seat) {
        switch (Facet) {
            case ContactFacet.Seat:
                return seat;
            case ContactFacet.Device:
                return ((long)contact.Device);
            case ContactFacet.Id:
                return contact.Id;
            case ContactFacet.Phase:
                return ((long)contact.Phase);
        }

        var hit = Sweep.Mapping.MapRay(ray: contact.Ray);

        if (!hit.IsOnSource) {
            return ((Facet == ContactFacet.Row) ? -1L : 0L);
        }

        return Facet switch {
            ContactFacet.X => hit.Coordinate.X.Value,
            ContactFacet.Y => hit.Coordinate.Y.Value,
            ContactFacet.Region => ((Region!.RowOf(point: hit.Coordinate) >= 0L) ? 1L : 0L),
            _ => Region!.RowOf(point: hit.Coordinate),
        };
    }
}
