using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>One persistent contact a rigid body owns: the partner it touches and the manifold whose slots carry the
/// pair's warm-start impulses from step to step.</summary>
internal sealed class WorldRigidContact {
    /// <summary>The partner's population index, or <see cref="WorldRigidContacts.Surface"/> for the static field.</summary>
    internal int Partner;
    /// <summary>Whether this step's contact generation produced candidates for the pair.</summary>
    internal bool Touched;
    /// <summary>The pair's manifold.</summary>
    internal readonly FixedRigidManifold Manifold = new();
}
/// <summary>The owner rule and the surface source ids of the rigid contacts, in one place.</summary>
internal static class WorldRigidContacts {
    /// <summary>The partner index naming the static contact field.</summary>
    internal const int Surface = -1;
    /// <summary>The source id of a body's walkable surface contact.</summary>
    internal const int GroundSource = 0;
    /// <summary>The source id of a body's non-walkable surface contact.</summary>
    internal const int ObstructionSource = 1;

    /// <summary>The owner rule, the one place it is stated: of a pair with a rigid side, the lower index owns a pair of
    /// two rigid bodies, and the rigid side owns a pair with a locomotion (kit) body. Every reader of a pair's contact
    /// (contact generation, the resting-contact test, support, checkpoint validation) asks here, so they can never
    /// disagree about which body holds the manifold.</summary>
    /// <param name="left">One side's population index.</param>
    /// <param name="leftRigid">Whether that side is rigid.</param>
    /// <param name="right">The other side's population index.</param>
    /// <param name="rightRigid">Whether that side is rigid.</param>
    /// <returns>The owning side and its partner.</returns>
    internal static (int Owner, int Partner) Owner(int left, bool leftRigid, int right, bool rightRigid) =>
        ((leftRigid && rightRigid)
            ? (Math.Min(val1: left, val2: right), Math.Max(val1: left, val2: right))
            : (leftRigid ? (left, right) : (right, left)));
    /// <summary>Whether the owner is its manifold's A side: it is against a rigid partner (the pair's lower index is A,
    /// and the normal runs from B toward A), and the B side against the static field or a kit body, which is A.</summary>
    /// <param name="partnerRigid">Whether the partner is a rigid body.</param>
    /// <returns><see langword="true"/> when the owner is the A side.</returns>
    internal static bool OwnerIsA(bool partnerRigid) => partnerRigid;
}
/// <summary>One occupied manifold slot of a rigid body's owned contact, as its checkpoint entry carries it: the partner,
/// the slot's index and the slot's persistent state.</summary>
/// <param name="Partner">The partner's population index, or -1 for the static field.</param>
/// <param name="Slot">The slot's index within the pair's manifold.</param>
/// <param name="SourceId">The slot's source identity.</param>
/// <param name="FeatureId">The slot's feature identity.</param>
/// <param name="AnchorA">The contact point relative to the pair's A body's centre of mass, world axes.</param>
/// <param name="AnchorB">The contact point relative to the pair's B body's centre of mass, world axes.</param>
/// <param name="Normal">The unit contact normal, from A toward B.</param>
/// <param name="NormalImpulseRaw">The accumulated normal impulse raw, at Q48.16.</param>
/// <param name="FrictionImpulse">The accumulated friction impulse, world axes.</param>
/// <param name="IdleSteps">The associations the slot has missed since it was last claimed.</param>
public readonly record struct WorldRigidContactSlotImage(int Partner, int Slot, int SourceId, int FeatureId, FixedVector3 AnchorA,
    FixedVector3 AnchorB, FixedVector3 Normal, long NormalImpulseRaw, FixedVector3 FrictionImpulse, int IdleSteps);
public sealed partial class WorldBody {
    // The contacts this body owns, ascending by partner (the surface first): every surface contact, every contact with a
    // locomotion body, and every contact with a rigid body of higher index. Entries are reused once released.
    private readonly List<WorldRigidContact> m_rigidContacts = [];
    private readonly List<WorldRigidContact> m_releasedRigidContacts = [];

    /// <summary>Gets the contacts this body owns, ascending by partner.</summary>
    internal IReadOnlyList<WorldRigidContact> RigidContacts => m_rigidContacts;

    /// <summary>Finds the contact this body owns with a partner, creating an empty one in partner order when none
    /// exists.</summary>
    /// <param name="partner">The partner's population index, or <see cref="WorldRigidContacts.Surface"/>.</param>
    /// <param name="created">Whether the contact was created by this call.</param>
    /// <returns>The contact.</returns>
    internal WorldRigidContact RigidContact(int partner, out bool created) {
        var position = 0;

        while (
            (position < m_rigidContacts.Count) &&
            (m_rigidContacts[position].Partner < partner)
        ) {
            position++;
        }

        if (
            (position < m_rigidContacts.Count) &&
            (m_rigidContacts[position].Partner == partner)
        ) {
            created = false;

            return m_rigidContacts[position];
        }

        WorldRigidContact contact;

        if (m_releasedRigidContacts.Count > 0) {
            contact = m_releasedRigidContacts[^1];
            m_releasedRigidContacts.RemoveAt(index: (m_releasedRigidContacts.Count - 1));
        } else {
            contact = new WorldRigidContact();
        }

        contact.Partner = partner;
        contact.Touched = false;
        contact.Manifold.Slots.Clear();
        m_rigidContacts.Insert(index: position, item: contact);
        created = true;

        return contact;
    }
    /// <summary>Releases the owned contact at a position in <see cref="RigidContacts"/>.</summary>
    /// <param name="position">The contact's position.</param>
    internal void ReleaseRigidContactAt(int position) {
        var contact = m_rigidContacts[position];

        m_rigidContacts.RemoveAt(index: position);
        contact.Manifold.Slots.Clear();
        contact.Manifold.BodyA = null;
        contact.Manifold.BodyB = null;
        m_releasedRigidContacts.Add(item: contact);
    }
    /// <summary>Appends every occupied slot of the owned contacts to a list, in contact then slot order.</summary>
    /// <param name="destination">The list the images are appended to.</param>
    internal void CopyRigidContactImages(List<WorldRigidContactSlotImage> destination) {
        foreach (var contact in m_rigidContacts) {
            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); index++) {
                ref readonly var slot = ref contact.Manifold.Slots[index];

                if (!slot.Occupied) {
                    continue;
                }

                destination.Add(item: new WorldRigidContactSlotImage(
                    AnchorA: slot.AnchorA,
                    AnchorB: slot.AnchorB,
                    FeatureId: slot.FeatureId,
                    FrictionImpulse: slot.FrictionImpulse,
                    IdleSteps: slot.IdleSteps,
                    Normal: slot.Normal,
                    NormalImpulseRaw: slot.NormalImpulseRaw,
                    Partner: contact.Partner,
                    Slot: index,
                    SourceId: slot.SourceId
                ));
            }
        }
    }
}
