using Puck.Commands;
using Puck.State.Rules;

namespace Puck.World;

/// <summary>The authority's view of the tick's pointing contacts on its screens: what a rule sweeping
/// <c>forEach: $contacts:&lt;screen&gt;</c> binds and what <c>contact(each, …)</c> reads.</summary>
public interface IWorldSurfaceContacts : IFacet {
    /// <summary>Appends the key of every contact event this tick whose ray lands on the sweep's screen and whose
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
/// <summary>A compiled <c>forEach: $contacts:&lt;screen&gt;</c> sweep: one binding of <c>$each</c> per pointing-contact
/// event this tick (a finger, a pen tip, or the mouse while its primary button is held) whose ray lands on one
/// <c>Simulation</c> screen. Mouse and touch arrive through this one door and differ only in their device, so a screen's
/// rules answer a tap the same way whichever made it. A binding's key is its participant's slot times
/// <see cref="SurfaceContact.MaximumPerTick"/> plus the event's position in that participant's tick.</summary>
/// <param name="ScreenIndex">The screen the rays map through, which a participant must hold <c>Control</c> over.</param>
/// <param name="Mapping">The screen row's source-normalized mapping (<see cref="WorldScreenMappings.Normalized"/>),
/// already validated, so the tick maps every ray in fixed point from document data.</param>
/// <param name="Participants">The participants one sweep can visit: the document's local seats plus its network
/// players.</param>
public sealed record WorldContactIteration(int ScreenIndex, SourceMapping Mapping, int Participants) : RuleIteration(Participants * SurfaceContact.MaximumPerTick) {
    /// <summary>The reserved channel the sweep's spelling <c>$contacts:&lt;screen&gt;</c> parses to.</summary>
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
}
/// <summary>One facet of the contact event a <c>$contacts:&lt;screen&gt;</c> sweep bound to <c>$each</c>.</summary>
public sealed class ContactOperand : WorldOperandFact {
    /// <summary>Initializes a new instance of the <see cref="ContactOperand"/> class.</summary>
    /// <param name="facet">The facet read.</param>
    /// <param name="sweep">The sweep whose binding is read, which names the screen the ray maps through.</param>
    public ContactOperand(ContactFacet facet, WorldContactIteration sweep) : base((facet is ContactFacet.X or ContactFacet.Y) ? CellKind.Fixed : CellKind.Int) {
        Facet = facet;
        Sweep = sweep;
    }

    /// <summary>Gets the facet read.</summary>
    public ContactFacet Facet { get; }
    /// <summary>Gets the sweep whose binding is read.</summary>
    public WorldContactIteration Sweep { get; }

    /// <summary>Returns the facet's raw value for a contact event its participant made.</summary>
    /// <param name="contact">The contact event.</param>
    /// <param name="seat">The participant's number, as <see cref="ContactFacet.Seat"/> reads it.</param>
    /// <returns>The raw value in <see cref="WorldOperandFact.ValueKind"/>'s encoding; zero for a coordinate of a ray
    /// that does not land on the screen.</returns>
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

        return (hit.IsOnSource
            ? ((Facet == ContactFacet.X) ? hit.Coordinate.X.Value : hit.Coordinate.Y.Value)
            : 0L);
    }
}
