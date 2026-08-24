using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Whether a local seat activates automatically at boot (<see cref="Eager"/>) or waits for a claim
/// (<see cref="OnDemand"/>) — the per-seat authored policy <see cref="WorldBodiesDefaults.SeatActivation"/>
/// declares. Both doors converge on the identical <c>Server.WorldPopulation.ActivateSeat</c> call through the same
/// <c>SessionRequest.Join</c>/<c>WorldServer.ApplySession</c> session-join seam regardless of which policy admitted
/// the seat — a seat activated on demand (<c>player.join</c>, or a controller's own hot-plug first touch via
/// <c>Client.PlayerRoster.ResolveDeviceSlot</c>) is indistinguishable from one activated at boot the instant it is
/// active.</summary>
[JsonConverter(typeof(StrictEnumConverter<SeatActivationPolicy>))]
public enum SeatActivationPolicy : byte {
    /// <summary>The seat's body is minted at boot, mirroring a session join for it immediately — the only policy
    /// seat 0 (player 1) may declare, since a session always needs a first player.</summary>
    Eager,

    /// <summary>The seat stays empty at boot; its body is minted the first time something claims it.</summary>
    OnDemand,
}
/// <summary>The built-in session census. Local players occupy the split-screen seats; network players are represented
/// by authoritative local stand-ins until a transport supplies their intent stream. Every field except
/// <see cref="NetworkPlayers"/> and <see cref="ReconnectGraceSeconds"/> is optional; the resolved (non-"Raw")
/// property of each field's own name states its ABSENT semantics. Seat 0 (player 1) must be
/// <see cref="SeatActivationPolicy.Eager"/> when any local seat is declared — the session always needs a first
/// player. <see cref="ReconnectGraceSeconds"/>: how long a disconnected body stays parked — retained in the
/// sim/collider set at its last pose, still counted <c>IsHumanOccupied</c> — before the deferred teardown (body
/// drop, and for a peer, its generation's grants) actually fires. <c>0</c> disables the grace window outright: a
/// disconnect tears the body down immediately, the pre-park behavior. A positive value authored against a world
/// whose <see cref="WorldDefinition.SimulationRateHz"/> is 0 parks the body forever — there is no tick mapping for a
/// world that never advances, so the deferred teardown never fires (never, not immediately and not zero; see
/// <see cref="CompiledTickDuration"/>). Authored in seconds — a physical unit, not a tick count, so a world's rate
/// can change without silently retuning this window — and compiled once to
/// <see cref="WorldDefinition.PopulationReconnectGraceTicks"/> via <see cref="WorldSimulationTickConversion"/>. Read
/// once at construction/rebuild, like the rest of this section (<c>SetPopulationDefaults</c>'s own timing class) —
/// a live edit takes effect on the next disconnect, never retroactively on an already-parked body. See
/// <c>Server.WorldPopulation</c>'s park-with-grace remarks and the <c>$parked:&lt;bodyRef&gt;</c> reserved rule
/// channel (<see cref="WorldRuleFacts.ParkedPrefix"/>) that reads a parked body's remaining count.</summary>
/// <remarks><c>capacityRow</c> is the census's authored-randomness facet, or <see langword="null"/> for an
/// ordinary literal <see cref="CapacityRaw"/>. A boot-only site (<see cref="WorldDrawSites.PopulationCapacity"/>):
/// settled into <see cref="CapacityRaw"/>, cleared, and narrated exactly like
/// <c>host.backendRow</c>. The site's admissible domain is not the capacity ceiling alone —
/// <see cref="NetworkPlayers"/> is validated against capacity minus the local seats, so a drawn capacity below
/// that sum is a document this same validator would refuse once resolved; the domain is narrowed statically at
/// authoring instead, so the roll can never decide whether the world boots. <see cref="Disclosure"/> is the
/// per-observer snapshot disclosure policy (default null = <see cref="WorldObserverDisclosure.Default"/>,
/// disclose-all) — read through <see cref="ObserverDisclosure"/>, applied at the output hub's sink boundary, never
/// inside the tick, so it changes what an observer is told and never what the simulation computes.</remarks>
public readonly record struct WorldBodiesDefaults(
    // ABSENT semantics below hold for every raw field: the document declares only what it wants to state; a raw
    // field's resolved sibling property (same name, no "Raw" suffix) is what every consumer reads.
    [property: JsonPropertyName("localSeats"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LocalSeatsRaw = null,
    [property: JsonPropertyName("seatActivation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SeatActivationPolicy>? SeatActivationRaw = null,
    int NetworkPlayers = 0,
    [property: JsonPropertyName("defaultPeerSource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntentSource? DefaultPeerSourceRaw = null,
    [property: JsonPropertyName("seatSpawns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SeatSpawnsRaw = null,
    [property: JsonPropertyName("distribution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDistribution? DistributionRaw = null,
    [property: JsonPropertyName("peerVariation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPopulationVariation? PeerVariationRaw = null,
    [property: JsonPropertyName("seatVariation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPopulationVariation? SeatVariationRaw = null,
    [property: JsonPropertyName("peerColors"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSequence? PeerColorsRaw = null,
    [property: JsonPropertyName("capacity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CapacityRaw = null,
    float ReconnectGraceSeconds = 3.0f,
    // OPTIONAL — reads the census from a scalar kind=int state row's slot at boot, AFTER row first-fills, so a
    // Boot-drawn row IS the drawn census: the draw facet lives on the ROW, this site only reads it. Re-resolved on
    // every fresh load; the row itself is the persisted evidence of what was drawn.
    [property: JsonPropertyName("capacityRow"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CapacityRow = null,
    // OPTIONAL per-observer snapshot disclosure. Null resolves to WorldObserverDisclosure.Default (disclose-all),
    // which is what every world authoring none delivers.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldObserverDisclosure? Disclosure = null
) {
    /// <summary>Gets the total authoritative body capacity, including reserved local seats — ABSENT resolves to
    /// <see cref="LocalSeats"/> plus <see cref="NetworkPlayers"/> (no simulated peers beyond the seats the document
    /// actually declares). While a <see cref="CapacityRow"/> read is PENDING (pre-resolve validation, before the boot
    /// resolver settles the row into <see cref="CapacityRaw"/>) this reads the admission ceiling, so capacity-bounded
    /// checks stay permissive exactly once and exact on the post-resolve revalidation.</summary>
    [JsonIgnore]
    public int Capacity => (CapacityRaw ?? ((CapacityRow is not null)
        ? WorldBodiesLimits.CapacityCeiling
        : (LocalSeats + NetworkPlayers)));
    /// <summary>Gets the inert census — zero local seats, zero capacity, no simulated peers.</summary>
    public static WorldBodiesDefaults Default { get; } = new();
    /// <summary>Gets the boot intent-source template every network stand-in wakes on — ABSENT resolves to
    /// <see cref="IntentSource.Idle"/>.</summary>
    [JsonIgnore]
    public IntentSource DefaultPeerSource => (DefaultPeerSourceRaw ?? IntentSource.Idle);
    /// <summary>Gets how simulated peers are distributed at spawn — ABSENT resolves to a degenerate zero-radius disc
    /// (inert: no simulated peer exists to place unless <see cref="CapacityRaw"/> is authored past the local/network
    /// seat count).</summary>
    [JsonIgnore]
    public WorldDistribution Distribution => (DistributionRaw ?? WorldDistribution.Default);
    /// <summary>Gets the number of reserved local-seat slots this world declares (0..the host's seat ceiling,
    /// <see cref="WorldBodiesLimits.LocalSeatCount"/>) — ABSENT resolves to <see cref="SeatSpawnsRaw"/>'s row
    /// count when authored, else 0. This is the document's own declaration; every "exactly N local seats" rule
    /// (seat activation count, seat spawn count, the census floor) reads this instead of a fixed constant.</summary>
    [JsonIgnore]
    public int LocalSeats => (LocalSeatsRaw ?? (SeatSpawnsRaw?.Count ?? 0));
    /// <summary>Gets the resolved per-observer snapshot disclosure policy — <see cref="Disclosure"/>, or
    /// <see cref="WorldObserverDisclosure.Default"/> when this world authors none.</summary>
    [JsonIgnore]
    public WorldObserverDisclosure ObserverDisclosure => (Disclosure ?? WorldObserverDisclosure.Default);
    /// <summary>Gets the stand-in color sequence — ABSENT resolves to the inert additive sequence.</summary>
    [JsonIgnore]
    public WorldSequence PeerColors => (PeerColorsRaw ?? WorldSequence.AdditiveDefault);
    /// <summary>Gets the independently authored producer-state sequences for peer bodies — ABSENT resolves to the
    /// inert index sequence (no variation).</summary>
    [JsonIgnore]
    public WorldPopulationVariation PeerVariation => (PeerVariationRaw ?? WorldPopulationVariation.Default);
    /// <summary>Gets the per-seat boot-activation policy, one entry per <see cref="LocalSeats"/> — ABSENT resolves
    /// to seat 0 Eager (a session always needs a first player) and every remaining seat OnDemand.</summary>
    [JsonIgnore]
    public IReadOnlyList<SeatActivationPolicy> SeatActivation => (SeatActivationRaw ?? DeriveSeatActivation(localSeats: LocalSeats));
    /// <summary>Gets the spawn-point name selected by each local seat ordinal, one entry per <see cref="LocalSeats"/>
    /// — ABSENT resolves to <see cref="WorldSpawnPointDefaults.ImplicitOriginId"/> for every seat (see
    /// <see cref="WorldDefinition.SpawnPoints"/>, which guarantees that name resolves).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> SeatSpawns => (SeatSpawnsRaw ?? DeriveSeatSpawns(localSeats: LocalSeats));
    /// <summary>Gets the independently authored producer-state sequences for local-seat bodies — ABSENT resolves to
    /// the inert index sequence (no variation).</summary>
    [JsonIgnore]
    public WorldPopulationVariation SeatVariation => (SeatVariationRaw ?? WorldPopulationVariation.Default);

    private static IReadOnlyList<SeatActivationPolicy> DeriveSeatActivation(int localSeats) {
        if (localSeats <= 0) {
            return [];
        }

        var activation = new SeatActivationPolicy[localSeats];

        activation[0] = SeatActivationPolicy.Eager;

        for (var index = 1; (index < localSeats); index++) {
            activation[index] = SeatActivationPolicy.OnDemand;
        }

        return activation;
    }
    private static IReadOnlyList<string> DeriveSeatSpawns(int localSeats) {
        if (localSeats <= 0) {
            return [];
        }

        var spawns = new string[localSeats];

        Array.Fill(
            array: spawns,
            value: WorldSpawnPointDefaults.ImplicitOriginId
        );

        return spawns;
    }
}
/// <summary>One participant-specific input-hold override. An omitted body uses the section defaults. The compiled
/// shape — <see cref="Ticks"/> is simulation ticks, the unit the runtime actually consumes. The document and the
/// <c>world.row.set inputHold</c> console verb both author this in seconds instead
/// (<see cref="WorldInputHoldParticipantAuthoring"/>); <see cref="WorldInputHoldAuthoring.Compile"/> is the one seam
/// that converts between the two, so this type itself never sees a raw tick literal from a document.</summary>
/// <param name="BodyIndex">The participant's 0-based population body index.</param>
/// <param name="Ticks">The authored hold floor, in simulation ticks.</param>
/// <param name="Equalized">Whether this participant contributes to and receives the shared maximum.</param>
public readonly record struct WorldInputHoldParticipant(int BodyIndex, int Ticks, bool Equalized);
/// <summary>The world's participant input-hold policy. Measured holds raise authored floors, the applied value is
/// capped by <see cref="CeilingTicks"/>, and a lower target must remain unchanged for <see cref="LowerAfterTicks"/>
/// before the applied hold descends one tick per simulation tick. The compiled shape — every <c>*Ticks</c> field is
/// simulation ticks, the unit <c>Server.WorldInputHoldRuntime</c> actually consumes — never what
/// <see cref="WorldDefinition.InputHold"/> itself stores (that field is the authored seconds shape,
/// <see cref="WorldInputHoldAuthoring"/>; see its own remarks). <see cref="WorldInputHoldAuthoring.Compile"/> and
/// <see cref="ToAuthoring"/> are the two conversions, both parameterized on a simulation rate rather than a pinned
/// constant, since a world's rate is authored (<see cref="WorldSimulationDefaults"/>). The separate addon-mutation ABI
/// (<c>Puck.World.Server.WorldAddonMutationDecoder</c>) still constructs this type directly with raw ticks — a live
/// runtime API, not authored document content, and out of either conversion's reach by architecture.</summary>
/// <param name="CeilingTicks">The maximum applied hold, in simulation ticks.</param>
/// <param name="LowerAfterTicks">How many simulation ticks a lower target must remain unchanged before descent.</param>
/// <param name="DefaultTicks">The authored hold floor for participants without an override.</param>
/// <param name="EqualizeByDefault">Whether participants without an override share the maximum.</param>
/// <param name="Participants">Participant-specific floor and distribution overrides, keyed by body index.</param>
public readonly record struct WorldInputHoldSettings(
    int CeilingTicks,
    int LowerAfterTicks,
    int DefaultTicks,
    bool EqualizeByDefault,
    IReadOnlyList<WorldInputHoldParticipant> Participants
) {
    /// <summary>Decompiles this compiled (ticks) settings row back to its authored (seconds) shape, at
    /// <paramref name="ratePerSecond"/> — the inverse of <see cref="WorldInputHoldAuthoring.Compile"/>. Exact whenever
    /// every tick count is a multiple of <paramref name="ratePerSecond"/> (every value a live seconds-authored
    /// <c>world.row.set inputHold</c> compiled through <see cref="WorldInputHoldAuthoring.Compile"/> is); a raw tick
    /// count from the addon-mutation ABI that is not may round-trip to the nearest second, one tick off on
    /// reconversion — see <see cref="WorldSimulationTickConversion.SecondsFromTicks"/>'s remarks.</summary>
    /// <param name="ratePerSecond">The simulation rate (Hz) this settings row runs under — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    public WorldInputHoldAuthoring ToAuthoring(uint ratePerSecond) {
        var participants = new WorldInputHoldParticipantAuthoring[Participants.Count];

        for (var index = 0; (index < participants.Length); index++) {
            var participant = Participants[index];

            participants[index] = new WorldInputHoldParticipantAuthoring(
                BodyIndex: participant.BodyIndex,
                Seconds: WorldSimulationTickConversion.SecondsFromTicks(
                    ticks: participant.Ticks,
                    ratePerSecond: ratePerSecond
                ),
                Equalized: participant.Equalized
            );
        }

        return new WorldInputHoldAuthoring(
            CeilingSeconds: WorldSimulationTickConversion.SecondsFromTicks(
                ticks: CeilingTicks,
                ratePerSecond: ratePerSecond
            ),
            LowerAfterSeconds: WorldSimulationTickConversion.SecondsFromTicks(
                ticks: LowerAfterTicks,
                ratePerSecond: ratePerSecond
            ),
            DefaultSeconds: WorldSimulationTickConversion.SecondsFromTicks(
                ticks: DefaultTicks,
                ratePerSecond: ratePerSecond
            ),
            EqualizeByDefault: EqualizeByDefault,
            Participants: participants
        );
    }
}
