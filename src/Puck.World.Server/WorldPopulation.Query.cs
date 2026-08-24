using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private int AvailableCensusSlots() {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (
                (entry.PlacementId is null) &&
                !entry.IsRemoteHuman &&
                !entry.IsAuthorityTransferred
            ) {
                count++;
            }
        }
        return count;
    }
    private static void ClearDesignations(Entry entry) {
        Array.Fill(
            array: entry.Designations,
            value: WorldTargetDesignation.None
        );
        entry.DesignationRefusal = string.Empty;
    }
    private int CountActiveCensus() {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (
                m_entries[index].Active &&
                (m_entries[index].PlacementId is null) &&
                !m_entries[index].IsRemoteHuman &&
                !m_entries[index].IsAuthorityTransferred
            ) {
                count++;
            }
        }

        return count;
    }
    private int CountInhabitants(string placementId) {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(
                a: m_entries[index].PlacementId,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )) {
                count++;
            }
        }

        return count;
    }
    private static WorldPlacement? FindInhabited(WorldDefinition definition, string placementId) {
        foreach (var placement in definition.Placements) {
            if (
                (placement.Inhabit is not null) &&
                string.Equals(
                a: placement.Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return placement;
            }
        }

        return null;
    }
    // The highest slot (127 downward) not currently claimed by an active seat/census peer or an inhabited peer — where a
    // new inhabited body lands, so inhabitants cluster at the top and never renumber an existing peer. A free slot is one
    // that holds no placement back-reference and no active census body.
    private int HighestFreeSlot() {
        for (var index = (Capacity - 1); (index >= LocalSeatCount); index--) {
            var entry = m_entries[index];

            if (
                (entry.PlacementId is null) &&
                !entry.Active
            ) {
                return index;
            }
        }

        return -1;
    }
    private int LowestInhabitant(string placementId) {
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(
                a: m_entries[index].PlacementId,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }
    private WorldTargetDesignation[] NewDesignations() {
        var values = new WorldTargetDesignation[m_targets.Count];

        Array.Fill(
            array: values,
            value: WorldTargetDesignation.None
        );
        return values;
    }

    /// <summary>The count of active entries this tick — a read-only aggregate over <see cref="IsActive"/>, computed
    /// on demand (never cached) since a world rule's <c>"$population"</c> reserved channel reads it at most once per
    /// tick. Each <c>WorldServer</c> — the boot instance's and every spawned <c>Puck.World.WorldInstance</c>'s alike —
    /// owns its own <see cref="WorldPopulation"/>, so this is already per-instance scoped under multi-world: reading it
    /// off one instance's population never observes another's occupancy. <c>WorldInstanceHost</c>'s reap-on-empty rule
    /// reads exactly this.</summary>
    /// <returns>The active-entry count.</returns>
    public int ActiveCount() {
        var count = 0;

        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index].Active) {
                count++;
            }
        }

        return count;
    }
    /// <summary>Counts the active entities per kit row for console diagnostics (one slot per definition row).</summary>
    public int[] ActiveKitCounts() {
        var counts = new int[m_kits.Length];

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index].Active) {
                counts[m_entries[index].KitIndex]++;
            }
        }

        return counts;
    }
    /// <summary>Counts the active entities per look row for the <c>world.looks</c> census (one slot per look row,
    /// mirroring <see cref="ActiveKitCounts"/>).</summary>
    public int[] ActiveLookCounts() {
        var counts = new int[m_lookRows.Count];

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index].Active) {
                counts[m_entries[index].LookIndex]++;
            }
        }

        return counts;
    }
    /// <summary>The entry's body color (the avatar's material albedo). A seat's is its assigned profile color; the
    /// client folds the pending-gray desaturation in on its side.</summary>
    /// <param name="index">The population index (0-based).</param>
    public Vector3 BodyColor(int index) => m_entries[index].BodyColor;
    /// <summary>The dynamic-body contact mode authored by the kit currently governing an entity.</summary>
    public WorldBodyContactMode BodyContact(int index) =>
        ((((uint)index) < Capacity)
            ? m_kits[ResolveKitIndex(index: index)].BodyContact
            : WorldBodyContactMode.Overlap
        );
    /// <summary>Reads a live seat's own <see cref="Entry.Designations"/> register — a defensive copy, safe to hold
    /// past the register's own future mutation. The one moment an abort-preparing caller can read it, mirroring
    /// <see cref="WorldBody.CaptureTransferState"/>'s own "read live, right now, never cached" contract: call this
    /// before <see cref="TryDetachSeatForTransfer"/>, which clears the live register unconditionally regardless of
    /// whether the transfer that follows ever aborts (see that method's own remarks) — pass the result to
    /// <see cref="RestoreDetachedSeat"/> on an abort so the seat's designations survive the round trip.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <returns>A defensive copy of the slot's current designation register, or an empty array for an out-of-range
    /// slot.</returns>
    public WorldTargetDesignation[] CaptureDesignations(int slot) => ((((uint)slot) < m_entries.Length)
        ? [.. m_entries[slot].Designations]
        : []
    );
    /// <summary>The entity-owned procedural appearance rig. Unlike a look row, this follows the occupant when
    /// authority transfer assigns it a different population slot.</summary>
    public byte CatalogRig(int index) => m_entries[index].CatalogRig;
    /// <summary>Clears designation outputs after the world authority has applied them.</summary>
    public void ClearDesignationOutputs() => m_designationOutputs.Clear();
    /// <summary>Clears staged generator invocations after the world authority has enqueued them.</summary>
    public void ClearGeneratorInvocationOutputs() => m_generatorInvocations.Clear();
    /// <summary>Clears staged judge invocations after the world authority has graded them.</summary>
    public void ClearJudgeInvocationOutputs() => m_judgeInvocations.Clear();
    /// <summary>Collects every currently-inhabited body slot bound to <paramref name="placementId"/> into
    /// <paramref name="into"/> (cleared first) — the despawn-ownership guard's read: which live bodies a
    /// <c>removePlacement</c> rule effect targeting this placement would strip their Inhabit binding from. Rule
    /// cadence only (at most once per firing rule per tick), never the per-tick pose path.</summary>
    /// <param name="placementId">The placement id to match.</param>
    /// <param name="into">The reusable destination list.</param>
    public void CollectInhabitants(string placementId, List<int> into) {
        into.Clear();

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(
                a: m_entries[index].PlacementId,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )) {
                into.Add(item: index);
            }
        }
    }
    /// <summary>Describes every target register and the most recent designation refusal for one body.</summary>
    public string DescribeTargets(int bodyIndex) {
        var entry = m_entries[bodyIndex];
        var rows = new string[m_targets.Count];

        for (var index = 0; (index < rows.Length); index++) {
            var register = m_targetRows[index];
            var target = entry.Designations[index];
            var status = (target.HasBody
                ? $"body:{target.Index}{(IsActive(index: target.Index)
                    ? string.Empty
                    : "(inactive)")}"
                : (target.IsPoint
                    ? string.Create(
                        provider: System.Globalization.CultureInfo.InvariantCulture,
                        handler: $"at:{((double)target.Point.X):0.###},{((double)target.Point.Y):0.###},{((double)target.Point.Z):0.###}"
                    )
                    : "none")
            );
            var effectiveRange = EffectiveTargetValue(
                body: entry.Body,
                stateName: register.RangeState,
                authoredMaximum: register.MaximumRange
            );
            var effectiveAngle = EffectiveTargetValue(
                body: entry.Body,
                stateName: register.HalfAngleState,
                authoredMaximum: register.MaximumHalfAngleDegrees
            );

            rows[index] = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"{register.Name}={status} envelope:range={effectiveRange:0.###}/{register.MaximumRange:0.###},halfAngle={effectiveAngle:0.###}/{register.MaximumHalfAngleDegrees:0.###},rangeState={(register.RangeState ?? "none")},halfAngleState={(register.HalfAngleState ?? "none")},los={register.RequiresLineOfSight.ToString().ToLowerInvariant()}"
            );
        }

        var refusal = ((entry.DesignationRefusal.Length == 0)
            ? "none"
            : entry.DesignationRefusal
        );

        return $"[body.targets: body:{bodyIndex} {((rows.Length == 0)
            ? "registers=none"
            : string.Join(
                separator: "; ",
                values: rows
            ))} lastRefusal={refusal}]";
    }
    /// <summary>Re-resolves a proposed body subject against one designation envelope.</summary>
    public bool DesignationWithinEnvelope(int sourceIndex, int targetIndex, WorldTargetRegister register, float rangeValue, float halfAngleDegrees, out string reason) {
        var target = m_entries[targetIndex].Body!;

        return DesignationWithinEnvelope(
            candidate: target.FixedPosition,
            candidateLabel: $"body:{targetIndex}",
            candidateOrientation: target.FixedOrientation,
            halfAngleDegrees: halfAngleDegrees,
            rangeValue: rangeValue,
            reason: out reason,
            register: register,
            sourceIndex: sourceIndex
        );
    }
    /// <summary>Re-resolves a proposed world-space point against one designation envelope.</summary>
    public bool DesignationWithinEnvelope(int sourceIndex, in FixedVector3 point, WorldTargetRegister register, float rangeValue, float halfAngleDegrees, out string reason) =>
        DesignationWithinEnvelope(
            candidate: point,
            candidateLabel: string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"at:{((double)point.X):0.###},{((double)point.Y):0.###},{((double)point.Z):0.###}"
            ),
            candidateOrientation: FixedQuaternion.Identity,
            halfAngleDegrees: halfAngleDegrees,
            rangeValue: rangeValue,
            reason: out reason,
            register: register,
            sourceIndex: sourceIndex
        );
    private bool DesignationWithinEnvelope(int sourceIndex, in FixedVector3 candidate, string candidateLabel, in FixedQuaternion candidateOrientation, WorldTargetRegister register, float rangeValue, float halfAngleDegrees, out string reason) {
        var source = m_entries[sourceIndex].Body!;
        var origin = source.FixedPosition;
        var forward = source.FixedOrientation.Rotate(vector: LocalForward);
        var range = FixedQ4816.FromDouble(value: rangeValue);
        var minimumDot = FixedQ4816.FromDouble(value: Math.Cos(d: (halfAngleDegrees * (Math.PI / 180.0))));

        if (!BodyTargetConeSense.Contains(
            candidate: in candidate,
            distanceSquared: out var distanceSquared,
            forward: in forward,
            minimumDot: minimumDot,
            origin: in origin,
            range: range
        )) {
            // The distance formats through double: FixedQ4816's own TryFormat admits only exact-expansion formats
            // and refuses '0.###'.
            var distance = ((double)FixedQ4816.Sqrt(value: distanceSquared));

            reason = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"{candidateLabel} is outside range/cone (distance={distance:0.###}, range={rangeValue:0.###}, halfAngle={halfAngleDegrees:0.###})"
            );
            return false;
        }
        if (
            register.RequiresLineOfSight &&
            !HasLineOfSight(
            from: origin,
            fromOrientation: source.FixedOrientation,
            to: candidate,
            toOrientation: candidateOrientation
        )
        ) {
            reason = $"solid geometry blocks line of sight to {candidateLabel}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
    /// <summary>Reads a visited-world effective slot and composes it with a register maximum by taking the tighter value.</summary>
    public static float EffectiveTargetValue(WorldBody? body, string? stateName, float authoredMaximum) {
        if (
            (body is null) ||
            string.IsNullOrWhiteSpace(value: stateName) ||
            !body.TryReadDurableCounter(
            name: stateName,
            value: out var requested
        )
        ) {
            return authoredMaximum;
        }
        return Math.Clamp(
            max: authoredMaximum,
            min: 0f,
            value: ((float)((double)requested))
        );
    }
    /// <summary>Reads or mints the stable mobility identity for one active occupant. A new local incarnation is
    /// derived from the complete authority/index/generation address; a transferred incarnation retains its origin.</summary>
    public WorldMobilityIdentity EnsureMobility(int index, string authority) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: authority);
        var entry = m_entries[index];

        if (
            !entry.Active ||
            (entry.Body is null)
        ) {
            throw new InvalidOperationException(message: $"body:{index} is not active");
        }
        if (
            (entry.Mobility is null) ||
            (entry.MobilityGeneration != entry.Generation)
        ) {
            entry.Mobility = new WorldMobilityIdentity(
                Incarnation: new WorldEntityAddress(
                    Authority: authority,
                    Index: index,
                    Generation: entry.Generation
                ),
                Epoch: 0UL
            );
            entry.MobilityGeneration = entry.Generation;
        }
        return entry.Mobility.Value;
    }
    /// <summary>Returns the <see cref="WorldBody"/> an entry owns while active, or <see langword="null"/> for an inactive
    /// entry. The <c>player.*</c> command wire resolves an index <c>1..128</c> to the entry's own body and produces
    /// intents on it (a warp/run/face/stop command), never a pose stream.</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="Capacity"/>).</param>
    public WorldBody? EntryBody(int index) => m_entries[index].Body;
    /// <summary>The entry's activation generation. Combined with authority and slot, this prevents stale entity
    /// addresses from aliasing a later occupant.</summary>
    public int Generation(int index) => m_entries[index].Generation;
    /// <summary>The placement id an inhabited peer slot holds — the frame source / anchor back-reference.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The held placement id, or <see langword="null"/> for a plain census peer or an empty slot.</returns>
    public string? InhabitantPlacementId(int index) => m_entries[index].PlacementId;
    /// <summary>Returns a value indicating whether the entry at <paramref name="index"/> is active (drawn this frame).</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="Capacity"/>).</param>
    /// <returns><see langword="true"/> when the entry is active.</returns>
    public bool IsActive(int index) => m_entries[index].Active;
    /// <summary>The deterministic kit row index assigned to a stable population slot.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The slot's assigned kit row index.</returns>
    public byte KitIndex(int index) => m_entries[index].KitIndex;
    /// <summary>The declared locomotion model of the kit assigned to a stable population slot — the runtime
    /// <c>body.motion</c> door's read of the same fact <see cref="WorldDefinitionValidator.TryValidateProgramCoherence"/>
    /// checks at boot, so a document-legal kit cannot runtime-switch into a program its model cannot back.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The slot's assigned kit's motion model.</returns>
    public WorldMotionModel KitMotion(int index) => m_kitRows[ResolveKitIndex(index: index)].Motion;
    /// <summary>The most recent timed <c>body.press</c> outcome for a body, or a zeroed/<see cref="PressHoldCapKind.None"/>
    /// outcome when none has been made (or the last attempt was refused — see <see cref="PressRefusal"/>).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public PressOutcome LastPressOutcome(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_entries.Length))
        ? m_entries[bodyIndex].PressOutcome
        : default
    );
    /// <summary>The most recent <c>body.stop</c> outcome for a body, or a zeroed outcome when none has been made
    /// (or the last attempt was refused — see <see cref="StopRefusal"/>).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public StopOutcome LastStopOutcome(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_entries.Length))
        ? m_entries[bodyIndex].StopOutcome
        : default
    );
    /// <summary>The resolved look row index for a stable population slot — carried out on the snapshot for the client's
    /// renderer (presentation-only).</summary>
    /// <param name="index">The 0-based population index.</param>
    public byte LookIndex(int index) => m_entries[index].LookIndex;
    /// <summary>The most recent <c>body.motion</c> switch refusal for a body, or <see cref="string.Empty"/> when its
    /// last attempt succeeded (or none has been made).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string MotionRefusal(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_entries.Length))
        ? m_entries[bodyIndex].MotionRefusal
        : string.Empty
    );
    /// <summary>Records the latest designation refusal for a live source body's read-back.</summary>
    public void NoteDesignationRefusal(int bodyIndex, string reason) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].DesignationRefusal = reason;
        }
    }
    /// <summary>Records the outcome of the latest <c>body.motion</c> switch attempt for a body — an empty
    /// <paramref name="reason"/> on success, the named refusal otherwise. <c>body.motion</c>'s handler reads this
    /// back through <see cref="MotionRefusal(int)"/> immediately after its synchronous submit (<c>WorldServer.Submit</c>
    /// drains inline) so its immediate echo reports the true outcome instead of assuming success.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason, or <see cref="string.Empty"/> on success.</param>
    public void NoteMotionRefusal(int bodyIndex, string reason) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].MotionRefusal = reason;
        }
    }
    /// <summary>Records the outcome of a successful timed <c>body.press</c> — the effective hold (post
    /// grant-ceiling and engine-backstop clamping) and which cap, if any, decided it — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so the handler can name a silent truncation instead of
    /// echoing the requested duration as if it were honored. Always clears any refusal note the body's press slot
    /// carried.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The outcome <see cref="WorldBody.PressChannel(int, FixedQ4816, float, FixedQ4816)"/> returned.</param>
    public void NotePressOutcome(int bodyIndex, PressOutcome outcome) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
            m_entries[bodyIndex].PressOutcome = outcome;
        }
    }
    /// <summary>Records a refused <c>body.press</c> attempt (timed or untimed alike — they share one refusal
    /// slot) for a body — <see cref="WorldServer.ApplyCommand"/> calls this from every early return a
    /// <see cref="WorldCommand.PressChannel"/> can take, so the slot is written on every single outcome the command
    /// can have. Also resets the timed-path's outcome to a neutral default, so a handler that reads it without
    /// checking the refusal first still sees nothing rather than a fabricated affirmative.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason.</param>
    public void NotePressRefusal(int bodyIndex, string reason) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].PressRefusal = reason;
            m_entries[bodyIndex].PressOutcome = default;
        }
    }
    /// <summary>Records a successful untimed <c>body.press</c> (the host-step tap, which carries no numeric
    /// outcome of its own) — clears any refusal note the body's press slot carried, the same way
    /// <see cref="NotePressOutcome"/> does for the timed path, so the one shared refusal slot both press paths read
    /// back through is always fresh regardless of which one last ran.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public void NotePressSuccess(int bodyIndex) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
        }
    }
    /// <summary>Records the outcome of a successful <c>body.stop</c> for a body — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so <c>body.stop</c>'s handler can quote the true
    /// released/cleared counts instead of a fixed template string. Always clears any refusal note the body's stop
    /// slot carried, so a denial from an earlier attempt can never bleed into a fresh success's echo.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The counts <see cref="WorldBody.Stop"/> computed.</param>
    public void NoteStopOutcome(int bodyIndex, StopOutcome outcome) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].StopRefusal = string.Empty;
            m_entries[bodyIndex].StopOutcome = outcome;
        }
    }
    /// <summary>Records a refused <c>body.stop</c> attempt for a body — <see cref="WorldServer.ApplyCommand"/>
    /// calls this from every early return a <see cref="WorldCommand.Stop"/> can take (the grant-table denial, the
    /// missing/inactive body) before it ever reaches <see cref="NoteStopOutcome"/>, so the slot is written on every
    /// single outcome a Stop command can have — never left holding a stale success from some earlier, unrelated
    /// attempt. Also resets the outcome counts to zero, so a handler that reads them without checking the refusal
    /// first still sees nothing rather than a fabricated affirmative.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason.</param>
    public void NoteStopRefusal(int bodyIndex, string reason) {
        if (((uint)bodyIndex) < ((uint)m_entries.Length)) {
            m_entries[bodyIndex].StopRefusal = reason;
            m_entries[bodyIndex].StopOutcome = default;
        }
    }
    /// <summary>The most recent <c>body.press</c> refusal for a body, or <see cref="string.Empty"/> when its last
    /// attempt succeeded (or none has been made). <c>body.press</c>'s handler checks this before
    /// <see cref="LastPressOutcome"/> — a non-empty refusal means no press was applied.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string PressRefusal(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_entries.Length))
        ? m_entries[bodyIndex].PressRefusal
        : string.Empty
    );
    /// <summary>Refreshes the cached body color of every active seat currently seated on <paramref name="profile"/> —
    /// the server half of a live <c>SetPlayerSection(identity)</c> color edit. The seat renders its color live off the
    /// shared handle client-side, but the per-entry <see cref="BodyColor"/> cache is the snapshot's source of truth, so
    /// it must not lie after an identity change. Bumps the revision when a seat's color actually moves.</summary>
    /// <param name="profile">The edited profile handle.</param>
    public void RefreshSeatColor(WorldIdentity profile) {
        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            var entry = m_entries[slot];

            if (
                (entry is { Active: true, Body: { } body }) &&
                ReferenceEquals(
                objA: body.Profile,
                objB: profile
            ) &&
                (entry.BodyColor != profile.Color)
            ) {
                entry.BodyColor = profile.Color;
                m_revision++;
            }
        }
    }

    // The shared body-construction pattern ActivateInhabitant/ActivateSimulated/RestoreDetachedSeat each already
    // run for their own kit index — factored out here so a restore reconstructs an arbitrary slot's body under
    // ITS OWN captured kit, not just the local-seat kit RestoreDetachedSeat assumes.
    private WorldBody BuildBodyForKit(byte kitIndex, WorldIdentity? profile) {
        var kit = m_kits[kitIndex];

        return new WorldBody(
            motion: m_kitRows[kitIndex].Motion,
            program: kit.BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            actions: kit.Actions,
            actionThresholds: kit.ActionThresholds,
            actionShapes: kit.ActionShapes,
            roleMask: kit.RoleMask,
            roleOrdinals: kit.RoleOrdinals,
            actionState: kit.ActionState,
            collider: kit.Collider,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            sprintChannelOrdinal: kit.SprintChannelOrdinal,
            driftChannelOrdinal: kit.DriftChannelOrdinal,
            planarDynamics: kit.PlanarDynamics
        ) {
            Profile = profile,
        };
    }

    /// <summary>Sets the exact rendered material color retained by an authority-transferred body.</summary>
    public void SetBodyColor(int slot, Vector3 color) {
        if (((uint)slot) < ((uint)Capacity)) {
            m_entries[slot].BodyColor = color;
        }
    }
    /// <summary>Restores a transferred occupant's procedural appearance identity.</summary>
    public void SetCatalogRig(int slot, byte catalogRig) {
        if (((uint)slot) < Capacity) {
            m_entries[slot].CatalogRig = catalogRig;
        }
    }
    /// <summary>Writes one already-validated target into a body's named register.</summary>
    public void SetDesignation(int bodyIndex, int registerIndex, WorldTargetDesignation target) {
        m_entries[bodyIndex].Designations[registerIndex] = target;
        m_entries[bodyIndex].DesignationRefusal = string.Empty;
    }
    /// <summary>Installs the committed mobility epoch on an already-admitted destination occupant.</summary>
    public void SetMobility(int index, in WorldMobilityIdentity mobility) {
        var entry = m_entries[index];

        if (
            !entry.Active ||
            (entry.Body is null)
        ) {
            throw new InvalidOperationException(message: $"body:{index} is not active");
        }
        entry.Mobility = mobility;
        entry.MobilityGeneration = entry.Generation;
    }
    /// <summary>Reseats a seat's body on a profile — the <c>player.identity</c>/confirm server half. The body reads its
    /// speeds live off the profile; the entry color follows for the snapshot.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile to seat on.</param>
    public void SetSeatProfile(int slot, WorldIdentity profile) {
        var entry = m_entries[slot];

        if (entry.Body is not { } body) {
            return;
        }

        if (!string.Equals(
            a: body.Profile?.Id,
            b: profile.Id,
            comparisonType: StringComparison.Ordinal
        )) {
            body.ResetDurableState();
        }
        body.Profile = profile;
        entry.BodyColor = profile.Color;
    }
    /// <summary>Activates the first <paramref name="count"/> census stand-ins (indices <c>4..</c>), clamped to
    /// <c>0..min(networkPlayers cap, </c><see cref="MaxSimulated"/><c>)</c>, and deactivates the rest. A newly-activated
    /// entry is re-seeded to a fresh spawn and given its own <see cref="WorldBody"/> (a server-authoritative spawn at that
    /// pose); a deactivated entry drops its body; entries already active keep wandering. Bumps the revision only when an
    /// occupancy flips.</summary>
    /// <param name="count">The requested active census count.</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the census change.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the census change.</param>
    /// <returns>The clamped count actually applied.</returns>
    public int SetSimulatedCount(int count, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) {
        // Clamp against the shared networkPlayers budget and every non-census occupant's physical slot.
        var clamped = Math.Clamp(
            value: count,
            min: 0,
            max: SimulatedCeiling
        );
        var changed = false;
        var remaining = clamped;

        for (var offset = 0; (offset < PeerCapacity); offset++) {
            var index = (LocalSeatCount + offset);
            var entry = m_entries[index];

            // Inhabited and authority-transferred slots are owned by their own lifecycles. They may occupy any index
            // after a mapped handoff, so census selection counts eligible slots instead of assuming all exclusions
            // live at the top of the table.
            if (
                (entry.PlacementId is not null) ||
                entry.IsRemoteHuman ||
                entry.IsAuthorityTransferred
            ) {
                continue;
            }

            var desired = (remaining > 0);

            if (desired) {
                remaining--;
            }

            if (entry.Active == desired) {
                continue;
            }

            if (desired) {
                ActivateSimulated(index: index);
                entry.Active = true;
                admitted?.Add(item: PeerEventEntry(index: index));
            } else {
                disconnected?.Add(item: PeerEventEntry(index: index));
                // A re-activation mints a fresh body at the canonical spawn.
                entry.Body = null;
                entry.Active = false;
            }

            changed = true;
        }

        m_simulatedCount = clamped;

        if (changed) {
            m_revision++;
        }

        return clamped;
    }
    /// <summary>The most recent <c>body.stop</c> refusal for a body, or <see cref="string.Empty"/> when its last
    /// attempt succeeded (or none has been made). <c>body.stop</c>'s handler checks this before
    /// <see cref="LastStopOutcome"/> — a non-empty refusal means the counts were never applied.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string StopRefusal(int bodyIndex) => ((((uint)bodyIndex) < ((uint)m_entries.Length))
        ? m_entries[bodyIndex].StopRefusal
        : string.Empty
    );
}
