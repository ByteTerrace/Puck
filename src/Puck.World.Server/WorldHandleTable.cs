using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A guest-facing reference into one <see cref="WorldHandleTable"/> slot — an index plus the GENERATION its
/// slot carried when this value was minted (<see cref="WorldHandleTable.TryMint"/>), STAMPED with the identity of the
/// table that minted it. Only the index (and, once channels carry one, the generation) is meant to ever cross to a
/// guest; the generation is how <see cref="WorldHandleTable.TryResolve"/> tells a handle minted before a rebuild apart
/// from a fresh one that happens to reuse the same index — see <see cref="WorldHandleTable"/>'s own remarks.
/// <para><b>A handle is bound to the table that minted it.</b> <see cref="TablePrincipal"/> and
/// <see cref="TableCapability"/> never cross to a guest and are never guest-supplied — the host alone stamps them at
/// <see cref="WorldHandleTable.TryMint"/> and checks them at <see cref="WorldHandleTable.TryResolve"/>. Without them, a
/// bare <c>(Index, Generation)</c> pair is, by construction, interchangeable across every principal's and every
/// capability's table: every table's generation counter starts at 0 and climbs slowly, so two different tables'
/// same-index slots collide on generation far more often than not, and <see cref="WorldHandleTable.TryResolve"/> would
/// silently answer whatever the WRONG table's matching slot holds. Docs/capability-channels-plan.md's Open Decision 1
/// names this exactly: "a guest presenting a small index and generation lands in a table it does not own." Stamping
/// the table's own identity into the value turns a mismatched resolve into a verification failure instead of a silent
/// hit.</para></summary>
/// <param name="Index">The 0-based slot index.</param>
/// <param name="Generation">The slot's generation at mint time.</param>
/// <param name="TablePrincipal">The principal of the <see cref="WorldHandleTable"/> that minted this handle.</param>
/// <param name="TableCapability">The capability of the <see cref="WorldHandleTable"/> that minted this handle.</param>
public readonly record struct WorldHandle(int Index, int Generation, WorldPrincipal TablePrincipal, WorldCapability TableCapability);

/// <summary>
/// A per-(principal, capability) HANDLE TABLE — the host-side, PURE PROJECTION of <see cref="WorldGrants"/> for one
/// principal outside the trust boundary (docs/capability-channels-plan.md's "Authority is a handle, never a name"). A
/// handle resolves a DESIGNATION — the <see cref="GrantSubject"/> a slot names — never a decision: the caller must
/// still call <see cref="WorldGrants.Allows"/> before acting on what it resolves to, because <see cref="WorldGrants.Allows"/>
/// carries the exclusivity override and a cached DECISION would go stale the moment another principal exclusively
/// reserves the same subject. Resolving only the designation costs nothing to keep correct and loses none of the
/// security property — a guest still cannot name what it was not handed.
/// </summary>
/// <remarks>
/// <para><b>Only a principal outside the trust boundary gets one.</b> The constructor refuses every
/// <see cref="PrincipalKind"/> but the two untrusted kinds, <see cref="PrincipalKind.Addon"/> and
/// <see cref="PrincipalKind.Peer"/> (the socket transport that admits peers, <see cref="WorldTcpHost"/>, lives in this
/// same project) — a fully-trusted <see cref="PrincipalKind.Console"/> or a locally-trusted
/// <see cref="PrincipalKind.Seat"/> could grant itself anything, so handing either a handle table is ceremony, not
/// security; both keep naming subjects directly.</para>
/// <para><b>Host-side, never guest-writable.</b> Only an INDEX (and, once channels carry one, its generation) is ever
/// meant to cross to a guest. Guest memory is guest-writable and the guest runs between every host write and host
/// read, so a table living in guest memory would be a table the guest can rewrite — bounds checks constrain WHERE it
/// writes, never WHAT an index means. This table lives here instead, and the host alone resolves an index against
/// it.</para>
/// <para><b>A pure projection — no independent write path.</b> This type never mutates <see cref="WorldGrants"/> and
/// carries no state <see cref="WorldGrants"/> does not already have; it only re-derives its slots from
/// <see cref="WorldGrants.ProjectSubjects"/> whenever <see cref="WorldGrants.Revision"/> has moved since its last
/// rebuild. <c>world.grant</c>/<c>world.revoke</c> (and the engagement-route helpers that touch the same per-principal
/// storage) are the only way the projection changes; a cleared slot is what THIS table becomes on the next resolve
/// after one, never an edit made here.</para>
/// <para><b>Slots carry a generation.</b> A rebuild re-projects in a DETERMINISTIC order (see
/// <see cref="WorldGrants.ProjectSubjects"/>) — never <see cref="HashSet{T}"/> enumeration order, which is a
/// free-list/insertion-history artifact not stable across a rebuild that ends at an identical subject set — so the
/// SAME index can name a DIFFERENT subject after a rebuild if the held set shrank, grew, or simply re-sorted
/// differently. <see cref="TryMint"/> stamps the slot's CURRENT generation into the returned <see cref="WorldHandle"/>;
/// <see cref="TryResolve"/> refuses a handle whose generation no longer matches the slot's — a REVOKED grant is
/// exactly this case, and it must resolve to nothing rather than whatever now happens to occupy the same index. The
/// generation counter is monotonic for the table's lifetime and never reused, so a stale handle can never
/// coincidentally match a newly-minted one at the same index.</para>
/// <para><b>A rebuild is triggered by ANY grant-table write, but a generation only moves for an index whose
/// designation actually changed.</b> <see cref="WorldGrants.Revision"/> is process-global — bumped by every
/// principal's grant, revoke, engagement route, and revoke miss — so a table for ONE (principal, capability) rebuilds
/// on a write that touches a completely different principal, or a different capability of the same one. Re-minting a
/// fresh generation at every index regardless would invalidate every live handle on any unrelated write, which
/// defeats "revocation is a cleared slot" by making an unrelated grant indistinguishable from a revocation. So a
/// rebuild compares each index's newly-projected subject against what THAT index named before: unchanged, it keeps
/// the old generation (a live handle survives an unrelated write); changed, it mints a fresh one (the only case that
/// should invalidate anything). This is also what makes <see cref="TryResolve"/>'s generation check load-bearing for a
/// caller that caches a handle across ticks instead of minting fresh every time — see
/// <c>Client.PlayerRoster.DriveTarget</c>.</para>
/// </remarks>
public sealed class WorldHandleTable {
    private readonly record struct Slot(GrantSubject Subject, int Generation);

    private readonly WorldGrants m_grants;
    private readonly WorldPrincipal m_principal;
    private readonly WorldCapability m_capability;
    private Slot[] m_slots = [];
    private int m_nextGeneration;
    // -1 so the very first EnsureFresh() always rebuilds, even against a fresh WorldGrants whose own Revision starts
    // at 0 — an untouched table must never answer as though it already reflects a grant that has not been projected
    // yet.
    private int m_builtAtRevision = -1;

    /// <summary>Builds a handle table over <paramref name="principal"/>'s <paramref name="capability"/> subjects,
    /// projected from <paramref name="grants"/>. The projection is computed lazily, on first use.</summary>
    /// <param name="grants">The authoritative grant table this table projects.</param>
    /// <param name="principal">The principal the table is for — <see cref="PrincipalKind.Addon"/> or
    /// <see cref="PrincipalKind.Peer"/> only (see the type's own remarks).</param>
    /// <param name="capability">The capability the table designates handles over.</param>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="principal"/> is inside the trust boundary (Console or
    /// Seat).</exception>
    public WorldHandleTable(WorldGrants grants, WorldPrincipal principal, WorldCapability capability) {
        ArgumentNullException.ThrowIfNull(argument: grants);

        if (principal.Kind is not (PrincipalKind.Addon or PrincipalKind.Peer)) {
            throw new ArgumentException(
                message: $"a handle table is only for a principal outside the trust boundary (addon or peer) — {principal.Describe()} could grant itself anything, so it keeps naming subjects directly (docs/capability-channels-plan.md)",
                paramName: nameof(principal)
            );
        }

        m_grants = grants;
        m_principal = principal;
        m_capability = capability;
    }

    /// <summary>Mints a handle for slot <paramref name="index"/> as the table stands RIGHT NOW (rebuilding first if
    /// the grants changed). Call this fresh every time the caller wants "whatever this slot designates today" rather
    /// than caching the result across a rebuild — index 0 is the LOWEST subject the principal holds this capability
    /// over, ordered by <see cref="WorldGrants.ProjectSubjects"/>'s deterministic sort.
    /// <para>Every projected slot names ONE INSTANCE of a capability's domain — a body, a screen, a section, or a
    /// profile: <see cref="WorldGrants.ProjectSubjects"/> projects exactly that positive kind set (see its
    /// <c>IsProjectable</c>) and withholds every other kind by default, so a table never has a slot designating "the
    /// whole domain" regardless of what the principal holds. (The first version filtered only
    /// <see cref="GrantSubjectKind.All"/>, and an adversarial probe showed <see cref="GrantSubjectKind.Composition"/> —
    /// also a whole-domain designation — sailing through; the positive statement is what makes the guarantee hold for
    /// kinds nobody has invented yet.) That still says nothing about WHICH per-instance kind a slot carries, and
    /// callers must not assume a particular one: a Drive table only ever projects
    /// <see cref="GrantSubjectKind.Body"/> today because the grant door (<c>IsLegitimateSubject</c>) admits no other
    /// concrete shape for Drive, not because of anything this type enforces — a caller that needs a particular kind
    /// must check it after resolving.</para></summary>
    /// <param name="index">The 0-based slot index.</param>
    /// <param name="handle">The minted handle, stamped with the slot's current generation.</param>
    /// <returns><see langword="true"/> when the index names a live slot.</returns>
    public bool TryMint(int index, out WorldHandle handle) {
        EnsureFresh();

        if ((uint)index >= (uint)m_slots.Length) {
            handle = default;

            return false;
        }

        handle = new WorldHandle(Index: index, Generation: m_slots[index].Generation, TablePrincipal: m_principal, TableCapability: m_capability);

        return true;
    }

    /// <summary>Mints a handle over the slot that currently designates <paramref name="subject"/>, as the table
    /// stands RIGHT NOW (rebuilding first if the grants changed) — the mint-by-requested-subject shape: the caller
    /// names WHAT it wants materialized and the table finds its own position, so no caller ever re-derives the
    /// projection (or allocates one) to locate a slot.</summary>
    /// <param name="subject">The subject to mint over.</param>
    /// <param name="handle">The minted handle, when a slot designates <paramref name="subject"/>.</param>
    /// <returns><see langword="true"/> when a slot currently designates the subject.</returns>
    public bool TryMintFor(GrantSubject subject, out WorldHandle handle) {
        EnsureFresh();

        for (var index = 0; (index < m_slots.Length); ++index) {
            if (m_slots[index].Subject == subject) {
                handle = new WorldHandle(Index: index, Generation: m_slots[index].Generation, TablePrincipal: m_principal, TableCapability: m_capability);

                return true;
            }
        }

        handle = default;

        return false;
    }

    /// <summary>Resolves <paramref name="handle"/> to the <see cref="GrantSubject"/> it designated AT MINT TIME.
    /// Fails — the caller's own attribution decides how loudly — when <paramref name="handle"/> was not minted by
    /// THIS table (its stamped <see cref="WorldHandle.TablePrincipal"/>/<see cref="WorldHandle.TableCapability"/> do
    /// not match this table's own — see <see cref="WorldHandle"/>'s own remarks on why cross-table collision is
    /// otherwise guaranteed, not coincidental), when the index is out of range, or when a rebuild since minting
    /// repacked or cleared the slot (a revoked grant is exactly this case): a cleared slot resolves to nothing, never
    /// to whatever now occupies the same index. This tells the caller only WHAT the handle designates; the caller must
    /// still call <see cref="WorldGrants.Allows"/> before acting on it (see the type's own remarks).</summary>
    /// <param name="handle">The handle to resolve.</param>
    /// <param name="subject">The designated subject, when resolution succeeds.</param>
    /// <returns><see langword="true"/> when the handle still designates a live slot of THIS table.</returns>
    public bool TryResolve(WorldHandle handle, out GrantSubject subject) {
        if ((handle.TablePrincipal != m_principal) || (handle.TableCapability != m_capability)) {
            subject = default;

            return false;
        }

        EnsureFresh();

        if (((uint)handle.Index >= (uint)m_slots.Length) || (m_slots[handle.Index].Generation != handle.Generation)) {
            subject = default;

            return false;
        }

        subject = m_slots[handle.Index].Subject;

        return true;
    }

    private void EnsureFresh() {
        if (m_builtAtRevision == m_grants.Revision) {
            return;
        }

        var projected = m_grants.ProjectSubjects(principal: m_principal, capability: m_capability);
        var next = new Slot[projected.Length];

        for (var index = 0; (index < projected.Length); index++) {
            var subject = projected[index];
            // Keep the OUTGOING slot's generation when this index still names the IDENTICAL subject it named before —
            // the common case, since WorldGrants.Revision is process-global (bumped by every principal's grant/revoke
            // and by SetControlRoute/ClearControlRoute) while a rebuild it triggers here often reprojects to the exact
            // same array for THIS principal/capability. Minting fresh regardless would invalidate every live handle of
            // every principal on any unrelated write — "revocation is a cleared slot, O(1), immediate" would otherwise
            // become "every write clears every slot of every principal". Only an index whose designation actually
            // changed (a new subject arrived, or an earlier removal/insertion shifted what this index means) mints a
            // fresh generation — a stale WorldHandle from before that change can then never coincidentally match
            // whatever now sits at the same index, exactly the property TryResolve depends on.
            var generation = (((uint)index < (uint)m_slots.Length) && (m_slots[index].Subject == subject))
                ? m_slots[index].Generation
                : m_nextGeneration++;

            next[index] = new Slot(Subject: subject, Generation: generation);
        }

        m_slots = next;
        m_builtAtRevision = m_grants.Revision;
    }
}
