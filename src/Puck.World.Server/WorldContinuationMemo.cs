using Puck.Networking;

namespace Puck.World.Server;

/// <summary>
/// The digests of the sections a slot's body keeps behind no holder (its pose, motion, clocks and contacts, and the
/// entry's producer, steering, cadence and navigation), cached over equal content. The step writes those sections'
/// state directly, so nothing says one is unchanged; instead each fold hands the memo the slot's image, and each
/// section's holder (<see cref="Versioned{T}"/>, <see cref="VersionedArray{T}"/>, <see cref="VersionedRecord{T}"/>)
/// answers the digest it took from equal content, encoding only a section whose content moved.
/// </summary>
/// <remarks>A digest is a static function of the section's content, and a holder hands out a cached digest only
/// over content equal to what it was taken from (raw bytes for a section of unmanaged fields, the section's own
/// equality for one holding immutable references), so the memo answers what a fresh encoding answers whichever slot
/// or occupant its content came from. Its holders are bound to no notes: feeding the memo is not a write to the
/// slot.</remarks>
internal sealed class WorldContinuationMemo {
    /// <summary>The sections the memo digests, one bit per <see cref="WorldContinuationSectionKind"/>.</summary>
    internal const uint Sections =
        (1U << ((int)WorldContinuationSectionKind.Pose)) |
        (1U << ((int)WorldContinuationSectionKind.Motion)) |
        (1U << ((int)WorldContinuationSectionKind.Clocks)) |
        (1U << ((int)WorldContinuationSectionKind.Contacts)) |
        (1U << ((int)WorldContinuationSectionKind.Producer)) |
        (1U << ((int)WorldContinuationSectionKind.Steering)) |
        (1U << ((int)WorldContinuationSectionKind.Cadence)) |
        (1U << ((int)WorldContinuationSectionKind.Navigation));

    // A section with no padding between its fields compares by raw bytes; one whose bools, nullables or narrow fields
    // leave padding compares by its own equality, since a fold's view does not promise what its padding holds.
    private readonly Versioned<BodyPoseSection> m_pose;
    private readonly VersionedRecord<BodyMotionSection> m_motion;
    private readonly VersionedRecord<BodyClocksSection> m_clocks;
    private readonly VersionedArray<WorldRigidContactSlotImage> m_contacts;
    private readonly VersionedRecord<EntryProducerSection> m_producer;
    private readonly VersionedRecord<EntrySteeringSection> m_steering;
    private readonly Versioned<EntryCadenceSection> m_cadence;
    private readonly VersionedRecord<EntryNavigationSection> m_navigation;

    /// <summary>Initializes a new instance of the <see cref="WorldContinuationMemo"/> class, holding no digest.</summary>
    internal WorldContinuationMemo() {
        var binding = new SlotBinding();

        m_pose = new(binding: binding);
        m_motion = new(binding: binding);
        m_clocks = new(binding: binding);
        m_contacts = new(binding: binding);
        m_producer = new(binding: binding);
        m_steering = new(binding: binding);
        m_cadence = new(binding: binding);
        m_navigation = new(binding: binding);
    }

    /// <summary>Digests the memo's sections of a slot's image into their places in <paramref name="digests"/>.</summary>
    /// <param name="image">The slot's image, as a fold's view built it.</param>
    /// <param name="writer">The scratch a moved section encodes into.</param>
    /// <param name="digests">The digests, by <see cref="WorldContinuationSectionKind"/>; only the memo's sections' are
    /// written.</param>
    /// <param name="work">What each digest read did, by <see cref="WorldContinuationSectionKind"/>.</param>
    /// <returns><see cref="Sections"/>: the sections whose digests were written.</returns>
    internal uint Digest(ref WorldPopulationEntryCheckpoint image, WireWriter writer, Span<ulong> digests, Span<SectionDigestWork> work) {
        m_pose.Write = image.Body.Pose;
        m_motion.Write = image.Body.Motion;
        m_clocks.Write = image.Body.Clocks;
        image.Body.Contacts.Slots.Span.CopyTo(destination: m_contacts.Resize(count: image.Body.Contacts.Slots.Length));
        m_producer.Write = image.Producer;
        m_steering.Write = image.Steering;
        m_cadence.Write = image.Cadence;
        m_navigation.Write = image.Navigation;
        digests[((int)WorldContinuationSectionKind.Pose)] = m_pose.Digest<ContinuationSectionCodec<BodyPoseSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Pose)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Motion)] = m_motion.Digest<ContinuationSectionCodec<BodyMotionSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Motion)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Clocks)] = m_clocks.Digest<ContinuationSectionCodec<BodyClocksSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Clocks)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Contacts)] = m_contacts.Digest<ContinuationTableCodec<BodyContactsSection, WorldRigidContactSlotImage>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Contacts)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Producer)] = m_producer.Digest<ContinuationSectionCodec<EntryProducerSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Producer)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Steering)] = m_steering.Digest<ContinuationSectionCodec<EntrySteeringSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Steering)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Cadence)] = m_cadence.Digest<ContinuationSectionCodec<EntryCadenceSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Cadence)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Navigation)] = m_navigation.Digest<ContinuationSectionCodec<EntryNavigationSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Navigation)],
            writer: writer
        );
        return Sections;
    }
}
