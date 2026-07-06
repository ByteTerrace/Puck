using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>The priority BAND a <see cref="ScreenSlotLedger"/> claim is made at — lower numeric value always wins a
/// contested slot. Ties (two claims at the same band wanting the same slot) resolve to whichever claimed FIRST (the
/// ledger never reshuffles an existing same-band holder for a same-band challenger).
/// <para>
/// These are ABSTRACT bands, not a menu of named consumers — a claimant's identity lives entirely in its own owner
/// token (see <see cref="ScreenSlotLedger.Claim"/>), never in this axis, so adding a new kind of screen claimant
/// never means adding a new enum member here. The three bands are spaced 10 apart so a future caller can insert an
/// intermediate band (e.g. <c>(Anchored + Overlay) / 2</c>) without renumbering anything, though in practice most
/// claimants fit one of the three as-is.
/// </para></summary>
public enum ScreenSlotPriority {
    /// <summary>A claim tied to a fixed, load-bearing identity that owns its preferred slot outright — never evicted
    /// by anything in a lower band; only another <see cref="Anchored"/> claim for the SAME slot (a same-band race,
    /// e.g. a re-boot) can replace it. The room's booted cabinets claim this band.</summary>
    Anchored = 0,
    /// <summary>A session-scoped claim that temporarily borrows or overrides an <see cref="Anchored"/> slot for as
    /// long as some mode is active, then releases it — the room's creator-mode preview easel claims this band (the
    /// settled index-3 borrow contract).</summary>
    Overlay = 10,
    /// <summary>Any other floating, lowest-priority claim (an incidental diegetic screen, a placeable camera feed) —
    /// the first band evicted/degraded when the room runs out of slots. No concrete claimant of this source's own
    /// uses this band today; it exists for future callers wired through <see cref="ScreenSlotLedger.Claim"/>.</summary>
    Ambient = 20,
}

/// <summary>One resolved claim: the slot the ledger granted, and whether it is the claim's own requested slot or a
/// degraded fallback (<see cref="Slot"/> differs from the index the caller asked for, or no slot was available at
/// all).</summary>
/// <param name="Slot">The granted screen-surface slot (0..<c>SdfProgramBuilder.MaxScreenSurfaces</c> - 1), or -1 when
/// no slot could be granted (every slot is held by a strictly higher-or-equal priority claim).</param>
/// <param name="Degraded">Whether this claim did not get its preferred slot (either evicted from it later, or never
/// granted it in the first place) — the caller should fall back to its non-diegetic presentation (a flat material, a
/// dark face) while this is <see langword="true"/>.</param>
public readonly record struct ScreenSlotClaim(int Slot, bool Degraded) {
    /// <summary>Whether this claim currently holds a real slot (<see cref="Slot"/> is not -1).</summary>
    public bool HasSlot => (Slot >= 0);
}

/// <summary>
/// The host-side allocator for the engine's <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> screen-surface slots —
/// pure state, no GPU, and no built-in notion of WHO its callers are. Any consumer that wants a diegetic screen
/// registers one CLAIM through <see cref="Claim"/> keyed by an opaque owner token, at a <see cref="ScreenSlotPriority"/>
/// band; the ledger resolves every registered claim, LOWEST BAND (highest priority) FIRST, into concrete slot
/// indices. A HIGHER-priority claim is never evicted by a lower one: a preferred-slot claim contesting an
/// already-seated slot only evicts the current holder when they are the SAME band (a genuine same-priority race,
/// e.g. a cabinet reboot) — a strictly lower-priority contest simply fails to seat and degrades instead, narrating
/// either way (a single stderr-style line, like the rest of the demo's diagnostics). Never throws: a claim that
/// cannot be seated comes back <see cref="ScreenSlotClaim.HasSlot"/> false, and the caller degrades to its
/// non-diegetic presentation.
/// <para>
/// Preferred-slot claims (a claimant wanting one EXACT index — e.g. a booted cabinet wanting its own console index)
/// pass a fixed <paramref name="preferredSlot"/>; a floating claim (no exact index in mind) passes -1 and is seated
/// at any FREE slot only, LOWEST INDEX FIRST (a floating claim never evicts), or dropped when none remain — so
/// allocation stays deterministic frame over frame for a fixed set of registered claims.
/// </para>
/// </summary>
public sealed class ScreenSlotLedger {
    private readonly record struct Registration(object OwnerToken, ScreenSlotPriority Priority, int PreferredSlot);

    private readonly List<Registration> m_registrations = [];
    private readonly object?[] m_ownerBySlot;
    private readonly ScreenSlotPriority[] m_priorityBySlot;
    private readonly List<string> m_narrations = [];

    /// <summary>Initializes an empty ledger over the engine's full screen-surface capacity.</summary>
    public ScreenSlotLedger() {
        m_ownerBySlot = new object?[SdfProgramBuilder.MaxScreenSurfaces];
        m_priorityBySlot = new ScreenSlotPriority[SdfProgramBuilder.MaxScreenSurfaces];
    }

    /// <summary>The narration lines produced by the most recent <see cref="Resolve"/> call (degrades/evictions only —
    /// a quiet frame yields an empty list). Reused across calls; copy before the next <see cref="Resolve"/> if a
    /// caller must retain it.</summary>
    public IReadOnlyList<string> LastNarrations => m_narrations;

    /// <summary>Registers (or updates) one owner's claim for this resolution pass. Calling this again for the SAME
    /// <paramref name="ownerToken"/> before the next <see cref="Resolve"/> replaces its prior claim (a caller that
    /// changes its mind mid-frame does not stack claims). Claims are cleared each <see cref="Resolve"/> — a caller
    /// that still wants a slot must re-claim every frame (or every rebuild), matching the rest of the engine's
    /// per-frame polling seams (<c>screenSources</c>/<c>screenLights</c>).</summary>
    /// <param name="ownerToken">An opaque identity for the claiming consumer (a boxed console index, a singleton
    /// marker object, anything reference-stable for the caller's lifetime — compared by reference, never by value).</param>
    /// <param name="priority">The claim's priority tier.</param>
    /// <param name="preferredSlot">A specific slot this claim wants (0..<c>MaxScreenSurfaces</c> - 1), or -1 for a
    /// floating claim the ledger seats at any available slot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ownerToken"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preferredSlot"/> is neither -1 nor a valid slot index.</exception>
    public void Claim(object ownerToken, ScreenSlotPriority priority, int preferredSlot = -1) {
        ArgumentNullException.ThrowIfNull(ownerToken);

        if (
            (preferredSlot != -1) &&
            ((preferredSlot < 0) || (preferredSlot >= SdfProgramBuilder.MaxScreenSurfaces))
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(preferredSlot), message: $"A preferred slot must be -1 or 0..{SdfProgramBuilder.MaxScreenSurfaces - 1}.");
        }

        for (var index = 0; (index < m_registrations.Count); index++) {
            if (ReferenceEquals(objA: m_registrations[index].OwnerToken, objB: ownerToken)) {
                m_registrations[index] = new Registration(OwnerToken: ownerToken, Priority: priority, PreferredSlot: preferredSlot);

                return;
            }
        }

        m_registrations.Add(item: new Registration(OwnerToken: ownerToken, Priority: priority, PreferredSlot: preferredSlot));
    }

    /// <summary>Withdraws <paramref name="ownerToken"/>'s claim before the next <see cref="Resolve"/> (a cabinet that
    /// ejected, an avatar that despawned) — a no-op if it never claimed, or already withdrew.</summary>
    /// <param name="ownerToken">The owner token previously passed to <see cref="Claim"/>.</param>
    public void Release(object ownerToken) {
        for (var index = 0; (index < m_registrations.Count); index++) {
            if (ReferenceEquals(objA: m_registrations[index].OwnerToken, objB: ownerToken)) {
                m_registrations.RemoveAt(index: index);

                return;
            }
        }
    }

    /// <summary>Resolves every currently registered claim into slot assignments, lowest tier first (ties keep
    /// registration order — the order <see cref="Claim"/> was called in this pass), and clears the registrations for
    /// the NEXT pass (see <see cref="Claim"/>'s per-pass re-claim contract). A preferred-slot claim that loses its
    /// slot to a strictly higher-priority claim narrates the degrade; a floating claim that finds no free slot
    /// narrates that it was dropped entirely.</summary>
    /// <returns>Each currently-registered owner token's resolved claim, in registration order — the same order
    /// <see cref="Claim"/> was called in this pass (call this BEFORE anything mutates the registration list further,
    /// since resolving clears it).</returns>
    public IReadOnlyList<(object OwnerToken, ScreenSlotClaim Claim)> Resolve() {
        m_narrations.Clear();
        Array.Clear(array: m_ownerBySlot);

        var ordered = new List<int>(capacity: m_registrations.Count);

        for (var index = 0; (index < m_registrations.Count); index++) {
            ordered.Add(item: index);
        }

        // Stable sort by tier: List<T>.Sort is not guaranteed stable, so index-sort with a tiebreak on original
        // position instead of sorting the registrations themselves.
        ordered.Sort(comparison: (left, right) => {
            var byPriority = m_registrations[left].Priority.CompareTo(m_registrations[right].Priority);

            return (byPriority != 0) ? byPriority : left.CompareTo(right);
        });

        var results = new ScreenSlotClaim[m_registrations.Count];

        foreach (var registrationIndex in ordered) {
            results[registrationIndex] = ResolveOne(registration: m_registrations[registrationIndex]);
        }

        var resolved = new (object OwnerToken, ScreenSlotClaim Claim)[m_registrations.Count];

        for (var index = 0; (index < m_registrations.Count); index++) {
            resolved[index] = (m_registrations[index].OwnerToken, results[index]);
        }

        m_registrations.Clear();

        return resolved;
    }

    // Seats one claim (called in priority order, lowest tier/highest priority first): a preferred slot already held
    // by the SAME tier is evicted (narrated) — a genuine same-priority race; held by a STRICTLY HIGHER priority (by
    // construction, since we walk ascending), the claim degrades instead (also narrated) and never evicts. A
    // floating claim takes the lowest-indexed FREE slot only (it never evicts), or is dropped (narrated) when none
    // remain.
    private ScreenSlotClaim ResolveOne(Registration registration) {
        if (registration.PreferredSlot >= 0) {
            var slot = registration.PreferredSlot;
            var priorOwner = m_ownerBySlot[slot];

            // Claims resolve in ASCENDING tier order, so a slot's current occupant (if any) was seated by a claim at
            // a tier <= this one's. Only a SAME-tier claim may evict it (a genuine same-tier race, e.g. a cabinet
            // reboot); a STRICTLY LOWER-priority claim contesting an occupied slot never evicts — it degrades.
            if (priorOwner is not null) {
                if (ReferenceEquals(objA: priorOwner, objB: registration.OwnerToken)) {
                    return new ScreenSlotClaim(Slot: slot, Degraded: false);
                }

                if (m_priorityBySlot[slot] != registration.Priority) {
                    m_narrations.Add(item: $"[screen-slots] {registration.Priority} claim from {Describe(ownerToken: registration.OwnerToken)} dropped (slot {slot} held by a higher-priority claim)");

                    return new ScreenSlotClaim(Slot: -1, Degraded: true);
                }

                m_narrations.Add(item: $"[screen-slots] slot {slot} evicted ({registration.Priority} claim from {Describe(ownerToken: registration.OwnerToken)} bumped {Describe(ownerToken: priorOwner)})");
            }

            m_ownerBySlot[slot] = registration.OwnerToken;
            m_priorityBySlot[slot] = registration.Priority;

            return new ScreenSlotClaim(Slot: slot, Degraded: false);
        }

        for (var slot = 0; (slot < m_ownerBySlot.Length); slot++) {
            if (m_ownerBySlot[slot] is null) {
                m_ownerBySlot[slot] = registration.OwnerToken;
                m_priorityBySlot[slot] = registration.Priority;

                return new ScreenSlotClaim(Slot: slot, Degraded: false);
            }
        }

        m_narrations.Add(item: $"[screen-slots] {registration.Priority} claim from {Describe(ownerToken: registration.OwnerToken)} dropped (no free screen-surface slot)");

        return new ScreenSlotClaim(Slot: -1, Degraded: true);
    }
    private static string Describe(object ownerToken) =>
        ownerToken.ToString() ?? ownerToken.GetType().Name;
}
