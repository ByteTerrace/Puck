using Puck.Abstractions.Memory;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The digests of the sections a slot's body keeps behind no holder (its pose, motion, clocks, motion recency, tape,
/// program and contacts, and the entry's admission, designations, producer, steering, cadence, navigation and route)
/// together with a scoped fold's tail, cached over equal content. The step writes those sections' state directly, so
/// nothing says one is unchanged; instead each fold hands the memo the slot's image, and each section's holder
/// (<see cref="Versioned{T}"/>, <see cref="VersionedArray{T}"/>, <see cref="VersionedRecord{T}"/>) or content check
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
        (1U << ((int)WorldContinuationSectionKind.MotionRecency)) |
        (1U << ((int)WorldContinuationSectionKind.Tape)) |
        (1U << ((int)WorldContinuationSectionKind.Program)) |
        (1U << ((int)WorldContinuationSectionKind.Contacts)) |
        (1U << ((int)WorldContinuationSectionKind.Admission)) |
        (1U << ((int)WorldContinuationSectionKind.Designations)) |
        (1U << ((int)WorldContinuationSectionKind.Producer)) |
        (1U << ((int)WorldContinuationSectionKind.Steering)) |
        (1U << ((int)WorldContinuationSectionKind.Cadence)) |
        (1U << ((int)WorldContinuationSectionKind.Navigation)) |
        (1U << ((int)WorldContinuationSectionKind.Route));

    // A section with no padding between its fields compares by raw bytes; one whose bools, nullables or narrow fields
    // leave padding compares by its own equality, since a fold's view does not promise what its padding holds.
    private readonly Versioned<BodyPoseSection> m_pose;
    private readonly VersionedRecord<BodyMotionSection> m_motion;
    private readonly VersionedRecord<BodyClocksSection> m_clocks;
    private readonly VersionedArray<ulong> m_motionRecency;
    private readonly VersionedRecord<BodyProgramSection> m_program;
    private readonly VersionedArray<WorldRigidContactSlotImage> m_contacts;
    private readonly VersionedArray<WorldTargetDesignation> m_designations;
    private readonly VersionedRecord<EntryProducerSection> m_producer;
    private readonly VersionedRecord<EntrySteeringSection> m_steering;
    private readonly Versioned<EntryCadenceSection> m_cadence;
    private readonly VersionedRecord<EntryNavigationSection> m_navigation;
    private readonly VersionedArray<int> m_route;

    private WorldTapeSegmentImage[] m_tape = [];
    private int m_tapeCount = -1;
    private ulong m_tapeDigest;

    private EntryAdmissionSection m_admission;
    private WorldAdmissionGrant[] m_admissionTemplates = [];
    private int m_admissionTemplateCount;
    private (WorldCapability Capability, GrantSubject Subject)[] m_admissionRevoked = [];
    private int m_admissionRevokedCount;
    private bool m_hasAdmissionDigest;
    private ulong m_admissionDigest;

    private bool m_tailObservationCurrent;
    private WorldIdentityProjection? m_tailProfile;
    private long[] m_tailRegisters = [];
    private int m_tailRegisterCount = -1;
    private ulong m_tailDigest;

    /// <summary>Initializes a new instance of the <see cref="WorldContinuationMemo"/> class, holding no digest.</summary>
    internal WorldContinuationMemo() {
        var binding = new SlotBinding();

        m_pose = new(binding: binding);
        m_motion = new(binding: binding);
        m_clocks = new(binding: binding);
        m_motionRecency = new(binding: binding);
        m_program = new(binding: binding);
        m_contacts = new(binding: binding);
        m_designations = new(binding: binding);
        m_producer = new(binding: binding);
        m_steering = new(binding: binding);
        m_cadence = new(binding: binding);
        m_navigation = new(binding: binding);
        m_route = new(binding: binding);
    }

    private static void WriteIfChanged<T>(VersionedArray<T> holder, ReadOnlySpan<T> elements) where T : unmanaged, IEquatable<T> {
        if (!holder.Read.SequenceEqual(other: elements)) {
            elements.CopyTo(destination: holder.Resize(count: elements.Length));
        }
    }

    /// <summary>Digests the memo's sections of a slot's image into their places in <paramref name="digests"/>.</summary>
    /// <param name="image">The slot's image, as a fold's view built it.</param>
    /// <param name="writer">The scratch a moved section encodes into.</param>
    /// <param name="digests">The digests, by <see cref="WorldContinuationSectionKind"/>; only the memo's sections' are
    /// written.</param>
    /// <param name="work">What each digest read did, by <see cref="WorldContinuationSectionKind"/>.</param>
    /// <param name="checkedNotes">The notes to verify cached digests against when checked mode is on.</param>
    /// <returns><see cref="Sections"/>: the sections whose digests were written.</returns>
    internal uint Digest(ref WorldPopulationEntryCheckpoint image, WireWriter writer, Span<ulong> digests, Span<SectionDigestWork> work, SlotNotes? checkedNotes = null) {
        m_pose.Write = image.Body.Pose;
        m_motion.Write = image.Body.Motion;
        m_clocks.Write = image.Body.Clocks;
        WriteIfChanged(elements: image.Body.MotionRecency.Clocks.Span, holder: m_motionRecency);
        m_program.Write = image.Body.Program;
        WriteIfChanged(elements: image.Body.Contacts.Slots.Span, holder: m_contacts);
        WriteIfChanged(elements: image.Designations.Targets.Span, holder: m_designations);
        m_producer.Write = image.Producer;
        m_steering.Write = image.Steering;
        m_cadence.Write = image.Cadence;
        m_navigation.Write = image.Navigation;
        WriteIfChanged(elements: image.Route.Path.Span, holder: m_route);
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
        digests[((int)WorldContinuationSectionKind.MotionRecency)] = m_motionRecency.Digest<ContinuationTableCodec<BodyMotionRecencySection, ulong>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.MotionRecency)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Tape)] = DigestTape(
            checkedNotes: checkedNotes,
            slot: image.Index,
            tape: in image.Body.Tape,
            work: ref work[((int)WorldContinuationSectionKind.Tape)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Program)] = m_program.Digest<ContinuationSectionCodec<BodyProgramSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Program)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Contacts)] = m_contacts.Digest<ContinuationTableCodec<BodyContactsSection, WorldRigidContactSlotImage>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Contacts)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Admission)] = DigestAdmission(
            admission: in image.Admission,
            checkedNotes: checkedNotes,
            slot: image.Index,
            work: ref work[((int)WorldContinuationSectionKind.Admission)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.Designations)] = m_designations.Digest<ContinuationTableCodec<EntryDesignationsSection, WorldTargetDesignation>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Designations)],
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
        digests[((int)WorldContinuationSectionKind.Route)] = m_route.Digest<ContinuationTableCodec<EntryRouteSection, int>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Route)],
            writer: writer
        );
        return Sections;
    }

    internal ulong DigestTail(in WorldPopulationEntryTail tail, WireWriter writer, WorldContinuationFoldWork? work, SlotNotes? checkedNotes, int slot) {
        var registers = tail.Registers.Span;
        ulong digest;

        if ((m_tailRegisterCount == registers.Length) &&
            (m_tailObservationCurrent == tail.ContactFieldObservationCurrent) &&
            EqualityComparer<WorldIdentityProjection?>.Default.Equals(x: m_tailProfile, y: tail.Profile) &&
            registers.SequenceEqual(other: m_tailRegisters.AsSpan(length: m_tailRegisterCount, start: 0))) {
            digest = m_tailDigest;
        } else {
            digest = WorldAuthorityCheckpointCodec.ContinuationTailDigest(tail: in tail, work: work, writer: writer);
            ArrayCapacity.EnsureScratch(ref m_tailRegisters, registers.Length);
            registers.CopyTo(destination: m_tailRegisters);
            m_tailRegisterCount = registers.Length;
            m_tailObservationCurrent = tail.ContactFieldObservationCurrent;
            m_tailProfile = tail.Profile;
            m_tailDigest = digest;
        }
        if (checkedNotes is { Checked: true }) {
            var fresh = WorldAuthorityCheckpointCodec.ContinuationTailDigest(tail: in tail, work: null, writer: writer);

            if (digest != fresh) {
                checkedNotes.ReportStale(fault: $"slot {slot} tail: the cached digest {digest:X16} differs from its fresh encoding's {fresh:X16}");
                m_tailDigest = digest = fresh;
            }
        }
        return digest;
    }

    private ulong DigestTape(in BodyTapeSection tape, WireWriter writer, ref SectionDigestWork work, SlotNotes? checkedNotes, int slot) {
        var segments = tape.Segments.Span;
        ulong digest;

        if ((m_tapeCount == segments.Length) && segments.SequenceEqual(other: m_tape.AsSpan(length: m_tapeCount, start: 0))) {
            digest = m_tapeDigest;
        } else {
            digest = ContinuationSectionCodec<BodyTapeSection>.Digest(bytes: out var bytes, section: in tape, writer: writer);
            work.Encodes++;
            work.EncodedBytes += bytes;
            ArrayCapacity.EnsureScratch(ref m_tape, segments.Length);
            segments.CopyTo(destination: m_tape);
            m_tapeCount = segments.Length;
            m_tapeDigest = digest;
        }
        if (checkedNotes is { Checked: true }) {
            var fresh = ContinuationSectionCodec<BodyTapeSection>.Digest(bytes: out _, section: in tape, writer: writer);

            if (digest != fresh) {
                checkedNotes.ReportStale(fault: $"slot {slot} section {nameof(BodyTapeSection)}: the cached digest {digest:X16} differs from its fresh encoding's {fresh:X16}");
                m_tapeDigest = digest = fresh;
            }
        }
        return digest;
    }

    private ulong DigestAdmission(in EntryAdmissionSection admission, WireWriter writer, ref SectionDigestWork work, SlotNotes? checkedNotes, int slot) {
        ulong digest;

        if (m_hasAdmissionDigest && SameAdmission(current: in admission)) {
            digest = m_admissionDigest;
        } else {
            digest = ContinuationSectionCodec<EntryAdmissionSection>.Digest(bytes: out var bytes, section: in admission, writer: writer);
            work.Encodes++;
            work.EncodedBytes += bytes;
            var templates = (admission.InstalledGrantTemplates ?? []);
            var revoked = (admission.RevokedKeys ?? []);

            ArrayCapacity.EnsureScratch(ref m_admissionTemplates, templates.Count);
            for (var index = 0; (index < templates.Count); index++) { m_admissionTemplates[index] = templates[index]; }
            m_admissionTemplateCount = templates.Count;
            ArrayCapacity.EnsureScratch(ref m_admissionRevoked, revoked.Count);
            for (var index = 0; (index < revoked.Count); index++) { m_admissionRevoked[index] = revoked[index]; }
            m_admissionRevokedCount = revoked.Count;
            m_admission = (admission with { InstalledGrantTemplates = m_admissionTemplates, RevokedKeys = m_admissionRevoked });
            m_admissionDigest = digest;
            m_hasAdmissionDigest = true;
        }
        if (checkedNotes is { Checked: true }) {
            var fresh = ContinuationSectionCodec<EntryAdmissionSection>.Digest(bytes: out _, section: in admission, writer: writer);

            if (digest != fresh) {
                checkedNotes.ReportStale(fault: $"slot {slot} section {nameof(EntryAdmissionSection)}: the cached digest {digest:X16} differs from its fresh encoding's {fresh:X16}");
                m_admissionDigest = digest = fresh;
            }
        }
        return digest;
    }

    private bool SameAdmission(in EntryAdmissionSection current) {
        ref readonly var held = ref m_admission;

        if ((held.Generation != current.Generation) ||
            (held.IsAuthorityTransferred != current.IsAuthorityTransferred) ||
            (held.IsRemoteHuman != current.IsRemoteHuman) ||
            (held.KitIndex != current.KitIndex) ||
            (held.SelectedKit != current.SelectedKit) ||
            (held.Mobility != current.Mobility) ||
            (held.MobilityGeneration != current.MobilityGeneration) ||
            (held.Parked != current.Parked) ||
            (held.ParkedUntilTick != current.ParkedUntilTick) ||
            !string.Equals(a: held.PlacementId, b: current.PlacementId, comparisonType: StringComparison.Ordinal) ||
            (held.Titled != current.Titled) ||
            (held.SpawnPosition != current.SpawnPosition) ||
            (held.SpawnYaw != current.SpawnYaw) ||
            (held.TravelTurn != current.TravelTurn) ||
            !string.Equals(a: held.IdentityDomain, b: current.IdentityDomain, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: held.IdentitySubject, b: current.IdentitySubject, comparisonType: StringComparison.Ordinal) ||
            (held.AdmissionTrust != current.AdmissionTrust) ||
            (held.AdmissionTier != current.AdmissionTier)) {
            return false;
        }
        var templates = (current.InstalledGrantTemplates ?? []);
        var revoked = (current.RevokedKeys ?? []);

        if ((templates.Count != m_admissionTemplateCount) || (revoked.Count != m_admissionRevokedCount)) {
            return false;
        }
        for (var index = 0; (index < m_admissionTemplateCount); index++) {
            if (!EqualityComparer<WorldAdmissionGrant>.Default.Equals(x: m_admissionTemplates[index], y: templates[index])) { return false; }
        }
        for (var index = 0; (index < m_admissionRevokedCount); index++) {
            if (m_admissionRevoked[index] != revoked[index]) { return false; }
        }
        return true;
    }
}
