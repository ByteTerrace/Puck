namespace Puck.Scripting;

/// <summary>
/// The frozen addon ABI contract: the single source of truth for the byte layout, export names, and pinned
/// budgets a WASM addon and its host agree on. Every multi-byte value is little-endian; every fixed-point value
/// is <see cref="Puck.Maths.FixedQ4816"/> raw <c>i64</c> bits; no floating point ever crosses the boundary. The
/// one version-shaped constant is <see cref="AbiVersion"/> itself — a shape-identity token pinned at <c>1</c>,
/// never a sequence: this host speaks exactly one addon ABI shape, and a breaking change re-keys the artifacts
/// (regenerate the module, move the hash pins) while the token stays <c>1</c>.
/// <para>
/// This is one of two independent re-key boundaries, never one key covering both. The other is the replay tape's
/// opaque magic (<c>Puck.World.WorldReplaySnapshot</c>). They are separately keyed but coupled in one direction: the
/// tape header pins recorded-at-mount receipts (name, module content hash, fuel, lane) rather than the definition's
/// declared rows, so a break here invalidates every existing tape through receipt mismatch even when the tape's own
/// byte layout is untouched. Re-key each boundary with the change that moves that boundary's shape; move only one and
/// a stale artifact passes its own door and fails at the other's.
/// </para>
/// </summary>
/// <remarks>
/// Two numbering families, never mixed. Discriminants — enumerated wire sets a leading byte reads as a closed
/// choice, where a zeroed cell must decode as invalid — are 1-based with <c>0</c> reserved-invalid: a cell's
/// <c>Kind</c>, <see cref="AddonChannelKind"/>, and <see cref="AddonSubjectKind"/>. The malformed-zero guard
/// lives at <c>Kind</c> and only <c>Kind</c>. Ordinals — dense indices with no reserved value — are 0-based:
/// channel-relative verbs (<see cref="RequestVerbs"/>), multi-part answer indices, channel table indices, and
/// batch ordinals.
/// </remarks>
public static class AddonAbi {
    /// <summary>The shape-identity token a guest must report from <c>puck_abi_version</c> (<c>1</c>, permanently —
    /// not a sequence). This host speaks exactly one addon ABI shape; any other reported value refuses loudly at
    /// handshake (<c>AbiMismatch</c>). Staleness detection does not rest on this number alone: a stale artifact
    /// fails the export pre-flight or the content-hash pin regardless of what it reports here.</summary>
    public const int AbiVersion = 1;
    /// <summary>The size in bytes of a single channel descriptor table entry (<c>16</c>).</summary>
    public const int ChannelDescriptorBytes = 16;
    /// <summary>The default per-tick fuel budget before a deterministic halt (<c>1_000_000</c>).</summary>
    public const long DefaultFuelPerTick = 1_000_000L;
    /// <summary>The size in bytes of a single host→guest input cell (<c>32</c>).</summary>
    public const int InCellBytes = 32;
    /// <summary>The number of low bits of an input-channel <c>Act</c>'s <c>Verb</c> that are reserved and must be
    /// zero (<c>2</c>). A channel act carries no phase: contribution semantics are per-tick declarative, so a
    /// nonzero reserved bit is a protocol fault rather than a discriminant to decode.</summary>
    public const int InputVerbReservedBits = 2;
    /// <summary>The mask isolating the reserved bits within an input-channel <c>Act</c>'s <c>Verb</c> (<c>3</c>);
    /// see <see cref="InputVerbReservedBits"/>.</summary>
    public const int InputVerbReservedMask = 3;
    /// <summary>The maximum length in UTF-8 bytes of one declared channel name (<c>64</c>).</summary>
    public const int MaxChannelNameBytes = 64;
    /// <summary>The maximum number of declared channel names an input channel accepts from a guest (<c>64</c>),
    /// and the width of the host's per-channel masks — must never exceed 64.</summary>
    public const int MaxChannelNames = 64;
    /// <summary>The maximum number of channel descriptors a guest may declare (<c>8</c>).</summary>
    public const int MaxChannels = 8;
    /// <summary>The maximum number of 32-byte cells the host writes to the input ring per tick (<c>64</c>).</summary>
    public const int MaxInCells = 64;
    /// <summary>The maximum total mutation-payload bytes EVERY mounted addon may dispatch, SUMMED, in ONE tick
    /// (<c>65536</c>, 64 KiB) — the global ceiling on host-side JSON decode work this seam admits per tick,
    /// regardless of how many guests are mounted or how their individual per-addon budgets are set. Excess is
    /// refused <see cref="AddonVerdict.QuotaExhausted"/>, attributed to the addon whose act pushed the running total
    /// over it.</summary>
    public const int MaxMutationBytesPerTickAllAddons = (64 * 1024);
    /// <summary>The maximum total mutation-payload bytes ONE addon may dispatch in ONE tick (<c>16384</c>, 16 KiB) —
    /// independent of, and tighter than, <see cref="MaxMutationPayloadBytes"/> times the per-tick act ceiling: a
    /// guest that spends its whole per-tick dispatch budget on maximum-size payloads still owes this second ceiling.
    /// Excess is refused <see cref="AddonVerdict.QuotaExhausted"/>, attributed to the offending addon.</summary>
    public const int MaxMutationBytesPerTickPerAddon = (16 * 1024);
    /// <summary>The maximum size in bytes of ONE <see cref="RequestVerbs.SubmitMutation"/> payload (<c>8192</c>, 8
    /// KiB) — the pointer-safety ceiling stage 5 of the addon mutation dispatch door enforces before any byte is
    /// copied out of guest linear memory. A guest naming a larger length is refused
    /// <see cref="AddonVerdict.PayloadTooLarge"/> without a single byte read.</summary>
    public const int MaxMutationPayloadBytes = (8 * 1024);
    /// <summary>The maximum number of 32-byte cells the output ring may hold (<c>63</c>) — one less than
    /// <see cref="MaxInCells"/> so every refusable act has a same-tick verdict slot in the guest's own declared
    /// input capacity; see the handshake relation in <c>AddonInstance</c>.</summary>
    public const int MaxOutCells = 63;
    /// <summary>The maximum length in UTF-8 bytes of a <see cref="AddonSubjectKind.Section"/> <c>Ask</c>'s
    /// NAME (<c>32</c>) — the name-keyed section-ask boundary's own pointer-safety ceiling, checked before a
    /// single byte is copied out of guest linear memory, exactly like <see cref="MaxMutationPayloadBytes"/> bounds
    /// a <see cref="RequestVerbs.SubmitMutation"/> payload. Comfortably above every declared
    /// <c>Puck.World.Protocol.WorldSection</c> member name (the longest today is ten ASCII characters); the ceiling
    /// exists to bound the guest-memory copy, not to fit the vocabulary exactly.</summary>
    public const int MaxSectionNameBytes = 32;
    /// <summary>The guest execution stack ceiling in bytes, guarding runaway recursion (<c>512 * 1024</c>).</summary>
    public const int MaxStackBytes = (512 * 1024);
    /// <summary>The <see cref="Puck.Maths.FixedQ4816"/> raw-bit value of <c>1.0</c> (<c>0x1_0000</c>).</summary>
    public const long One = 0x1_0000L;
    /// <summary>The size in bytes of a single guest→host output cell (<c>32</c>).</summary>
    public const int OutCellBytes = 32;

    /// <summary>The by-name guest exports the host binds at instantiation.</summary>
    public static class Exports {
        /// <summary>The <c>() -&gt; i32</c> export returning the guest's ABI version.</summary>
        public const string AbiVersion = "puck_abi_version";
        /// <summary>The <c>() -&gt; i32</c> export returning the declared channel count at <see cref="ChannelsPtr"/>, <c>1..=MaxChannels</c>.</summary>
        public const string ChannelsCount = "puck_channels_count";
        /// <summary>The <c>() -&gt; i32</c> export returning the byte offset of the channel descriptor table.</summary>
        public const string ChannelsPtr = "puck_channels_ptr";
        /// <summary>The <c>() -&gt; i32</c> export returning the input ring capacity in cells, <c>1..=MaxInCells</c>.</summary>
        public const string InCap = "puck_in_cap";
        /// <summary>The <c>() -&gt; i32</c> export returning the byte offset of the host→guest input ring.</summary>
        public const string InPtr = "puck_in_ptr";
        /// <summary>The optional <c>() -&gt; ()</c> export called once after instantiation, before the first tick.</summary>
        public const string Init = "puck_init";
        /// <summary>The exported guest linear memory the host reads and writes.</summary>
        public const string Memory = "memory";
        /// <summary>The <c>(i32) -&gt; i32</c> export the host drives once per sim tick: the argument is the input cell count the host wrote, the result is the output cell count the guest wrote.</summary>
        public const string OnTick = "puck_on_tick";
        /// <summary>The <c>() -&gt; i32</c> export returning the output ring capacity in cells, <c>0..=MaxOutCells</c>.</summary>
        public const string OutCap = "puck_out_cap";
        /// <summary>The <c>() -&gt; i32</c> export returning the byte offset of the guest→host output ring.</summary>
        public const string OutPtr = "puck_out_ptr";
    }
    /// <summary>The little-endian field offsets within a 16-byte channel descriptor table entry.</summary>
    public static class ChannelDescriptorOffsets {
        /// <summary>The <c>u8</c> <see cref="AddonChannelKind"/> wire value at byte <c>0</c>. <c>0</c> is invalid.</summary>
        public const int Kind = 0;
        /// <summary>The <c>u8</c> reserved-must-be-zero byte at <c>1</c>.</summary>
        public const int Reserved0 = 1;
        /// <summary>The <c>u64</c> reserved-must-be-zero field at byte <c>8</c>.</summary>
        public const int Reserved1 = 8;
        /// <summary>The <c>u16</c> per-kind verb or source count at byte <c>2</c>.</summary>
        public const int VerbCount = 2;
        /// <summary>The <c>u32</c> byte offset of the channel's verb table at byte <c>4</c>; <c>0</c> when the kind carries none.</summary>
        public const int VerbTablePtr = 4;
    }
    /// <summary>The little-endian field offsets within a 32-byte host→guest input cell.</summary>
    public static class InCellOffsets {
        /// <summary>The <c>i64</c> primary payload lane at byte <c>16</c>.</summary>
        public const int A = 16;
        /// <summary>The <c>i64</c> secondary payload lane at byte <c>24</c>.</summary>
        public const int B = 24;
        /// <summary>The <c>u8</c> channel index at byte <c>1</c>.</summary>
        public const int Channel = 1;
        /// <summary>The <c>u16</c> handle generation paired with <see cref="HandleIndex"/> at byte <c>6</c>.</summary>
        public const int HandleGeneration = 6;
        /// <summary>The <c>u16</c> granted handle index on an <c>Answer</c>, or the observed subject on an <c>Observation</c>, at byte <c>4</c>.</summary>
        public const int HandleIndex = 4;
        /// <summary>The <c>u8</c> <see cref="AddonInCellKind"/> wire value at byte <c>0</c>. <c>0</c> is invalid.</summary>
        public const int Kind = 0;
        /// <summary>The <c>u16</c> index, on an <c>Answer</c>, of which output cell of the guest's previous batch it answers, at byte <c>2</c>.</summary>
        public const int Ordinal = 2;
        /// <summary>The <c>u16</c> reserved-must-be-zero field at byte <c>10</c>.</summary>
        public const int Reserved0 = 10;
        /// <summary>The <c>u32</c> reserved-must-be-zero field at byte <c>12</c>.</summary>
        public const int Reserved1 = 12;
        /// <summary>The <c>u8</c> 0-based <see cref="ObservationVerbs"/> ordinal, or a multi-part answer's
        /// 0-based part index, at byte <c>9</c>.</summary>
        public const int Verb = 9;
        /// <summary>The <c>u8</c> <see cref="AddonVerdict"/> wire value at byte <c>8</c>; zero on kinds that carry none.</summary>
        public const int Verdict = 8;
    }
    /// <summary>The little-endian field offsets within a 32-byte guest→host output cell. No reserved padding —
    /// every byte is load-bearing.</summary>
    public static class OutCellOffsets {
        /// <summary>The <c>i64</c> primary payload lane at byte <c>8</c>.</summary>
        public const int A = 8;
        /// <summary>The <c>i64</c> secondary payload lane at byte <c>16</c>.</summary>
        public const int B = 16;
        /// <summary>The <c>i64</c> tertiary payload lane at byte <c>24</c>.</summary>
        public const int C = 24;
        /// <summary>The <c>u8</c> descriptor index into the guest's declared channel table at byte <c>1</c>.</summary>
        public const int Channel = 1;
        /// <summary>The <c>u16</c> handle generation paired with <see cref="HandleIndex"/> at byte <c>4</c>.</summary>
        public const int HandleGeneration = 4;
        /// <summary>The <c>u16</c> subject handle at byte <c>2</c>; reserved-must-be-zero on an <c>Ask</c>.</summary>
        public const int HandleIndex = 2;
        /// <summary>The <c>u8</c> <see cref="AddonOutCellKind"/> wire value at byte <c>0</c>. <c>0</c> is invalid.</summary>
        public const int Kind = 0;
        /// <summary>The <c>u16</c> 0-based channel-relative operation ordinal on an <c>Act</c>, or the
        /// <see cref="AddonSubjectKind"/> discriminant on an <c>Ask</c>, at byte <c>6</c>.</summary>
        public const int Verb = 6;
    }
    /// <summary>The closed numeric vocabulary a <c>Request</c> channel's <c>Act</c>/<c>Ask</c> cells speak. Verbs
    /// are 0-based ordinals, not discriminants: a channel's declared <c>VerbCount</c> is the exclusive upper
    /// bound of the range a guest may write (<c>0 &lt;= Verb &lt; VerbCount</c>).</summary>
    public static class RequestVerbs {
        /// <summary>The body-pose query verb: a subject's position and orientation, no arguments (<c>0</c>).</summary>
        public const int BodyPose = 0;
        /// <summary>The number of <c>Answer</c> cells a <see cref="BodyPose"/> query produces (<c>4</c>).</summary>
        public const int BodyPoseAnswerParts = 4;
        /// <summary>The size of the pinned vocabulary — the ceiling on a guest's declared <c>VerbCount</c>, which
        /// may be any non-empty prefix of it: growing this vocabulary must never refuse a guest built against
        /// fewer verbs (<c>3</c>).</summary>
        public const int Count = 3;
        /// <summary>The target-designation verb (<c>2</c>): a guest acts through a Drive handle over the source
        /// body; <c>A</c> is the target body index, <c>B</c> is the target-register index, and <c>C</c> is zero.</summary>
        public const int Designate = 2;
        /// <summary>The number of <c>Answer</c> cells a <see cref="Designate"/> act produces (<c>1</c>).</summary>
        public const int DesignateAnswerParts = 1;
        /// <summary>The mutation-submission verb (<c>1</c>): a guest holding a Mutate handle over a document
        /// section acts through it with a JSON payload rather than a query — the request cell's <c>A</c>/<c>B</c>/
        /// <c>C</c> lanes carry the declared mutation-kind ordinal, an unsigned guest-memory pointer, and an
        /// unsigned byte length (bounded by <see cref="MaxMutationPayloadBytes"/>) rather than the all-zero shape
        /// <see cref="BodyPose"/> requires. This is prefix growth over <see cref="AbiVersion"/> <c>1</c>: a guest
        /// built before this verb existed declares a smaller <c>VerbCount</c> and is unaffected — its hash pin does
        /// not move.</summary>
        public const int SubmitMutation = 1;
        /// <summary>The number of <c>Answer</c> cells a <see cref="SubmitMutation"/> act produces (<c>1</c>) — one
        /// reserved cell per act, carrying <see cref="AddonVerdict.Applied"/> or a refusal; see the addon mutation
        /// seam's timing contract for when it is reserved versus when it is staged.</summary>
        public const int SubmitMutationAnswerParts = 1;
    }
    /// <summary>The host-written, 0-based disclosure verb vocabulary an <c>Observation</c> cell carries. Verbs
    /// <c>1..9</c> are world events — edges delivered in pinned sim iteration order, gated by an Observe grant
    /// carrying an event budget (see <c>Puck.World.Protocol.WorldGrant.EventBudget</c>); they mint no handle
    /// (<c>HandleIndex</c>/<c>HandleGeneration</c> are always zero on an event cell — events are data, never
    /// authority). The closed event-family vocabulary lives host-side (<c>Server.WorldEventFeed</c>); this ABI only
    /// pins the wire shape. This is prefix growth over <see cref="AbiVersion"/> <c>1</c>: a guest built before an
    /// event verb existed simply never receives it (it declares no interest by holding no event-budgeted grant), so
    /// growing this set never breaks an existing module.</summary>
    public static class ObservationVerbs {
        /// <summary>Two bodies began overlapping (a PROXIMITY edge — see <c>Server.WorldEventFeed</c>'s own remarks
        /// for the exact test; this is not the physical contact resolver). <c>A</c>/<c>B</c> = the two bodies'
        /// 0-based entity indices, ascending.</summary>
        public const int EventCollisionBegin = 5;
        /// <summary>Two bodies stopped overlapping. Same payload shape as <see cref="EventCollisionBegin"/>.</summary>
        public const int EventCollisionEnd = 6;
        /// <summary>The per-mount event gap summary cell — the overflow doctrine's resync signal. <c>A</c> = the
        /// addon's lifetime dropped-event count (saturating); <c>B</c> is always zero. Emitted at most once per
        /// batch, after every edge that fit, whenever the count is nonzero or moved since the last batch that
        /// carried it. A nonzero count means "resync by polling the level state you already observe" — the dropped
        /// edges are gone, never replayed.</summary>
        public const int EventGap = 10;
        /// <summary>A watched machine-memory byte range changed value. <c>A</c> = <c>(screenIndex &lt;&lt; 32) |
        /// (uint)address</c>; <c>B</c> = the new byte value, zero-extended. Published only when the host composes
        /// presentation (a headless host peeks no machine and publishes nothing on this verb — see
        /// <c>Server.WorldEventFeed</c>'s own remarks).</summary>
        public const int EventMachineMemoryChanged = 9;
        /// <summary>A body entered a named region. <c>A</c> = the body's 0-based entity index; <c>B</c> = the
        /// region's 0-based ordinal (document order among placements carrying a region facet).</summary>
        public const int EventRegionEnter = 1;
        /// <summary>A body left a named region. Same payload shape as <see cref="EventRegionEnter"/>.</summary>
        public const int EventRegionExit = 2;
        /// <summary>A route was dissolved (ordinary disengage or admin repair). Same payload shape as
        /// <see cref="EventRouteEngaged"/>.</summary>
        public const int EventRouteDisengaged = 8;
        /// <summary>A route (possession/mirror/machine engagement) was established. <c>A</c> = the source body's
        /// 0-based entity index; <c>B</c> = the target, encoded as the screen index when <c>B &gt;= 0</c> or
        /// <c>-(bodyIndex + 1)</c> when the target is a body.</summary>
        public const int EventRouteEngaged = 7;
        /// <summary>A seat became human-occupied. <c>A</c> = the 0-based seat index (also its body index); <c>B</c>
        /// is always zero.</summary>
        public const int EventSeatJoin = 3;
        /// <summary>A seat stopped being human-occupied. Same payload shape as <see cref="EventSeatJoin"/>.</summary>
        public const int EventSeatLeave = 4;
        /// <summary>The disclosure of a minted handle over a body the addon's principal was granted.</summary>
        public const int GrantedBody = 0;
    }

    /// <summary>Encodes an input channel <c>Act</c> cell's <c>Verb</c> from the declared channel-name ordinal it
    /// addresses: the low <see cref="InputVerbReservedBits"/> bits are required-zero, the remaining bits are the
    /// declared ordinal. Contribution semantics are per-tick declarative, so there is no phase to pack
    /// alongside it.</summary>
    /// <param name="declaredOrdinal">The 0-based index into the channel's declared name table.</param>
    /// <returns>The encoded <c>Verb</c> value.</returns>
    public static ushort EncodeChannelVerb(int declaredOrdinal) {
        return ((ushort)(declaredOrdinal << InputVerbReservedBits));
    }
}
