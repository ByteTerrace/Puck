using System.Numerics;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The row-scoped Mutate laws — <c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c> as a grant subject, exercised
/// through the REAL apply pipeline (<c>EnqueueMutation</c> → tick drain → <c>WorldServer.TryAdmitMutation</c>), never
/// against the grant table alone. Every case pairs a denial with the same action under one reversed fact.
/// <para>The acting principal is an untrusted <see cref="PrincipalKind.Peer"/> throughout: a seat or the console is
/// seeded <c>Mutate/section:*</c> at boot, so granting one a row subject would discriminate nothing.</para>
/// </summary>
public sealed class RowScopedGrantLawTests {
    /// <summary>The host's own placement row, present in the fixture document — the row a slot holder must not be
    /// able to overwrite through the compose arm's replace-by-key.</summary>
    private const string HostRow = "ball";
    /// <summary>The contribution slot's own row, absent from the fixture document — authoring it is the granted
    /// act.</summary>
    private const string SlotRow = "slot-nw";

    private static readonly WorldPrincipal Partner = WorldPrincipal.Peer(generation: 1, index: 4);

    [Fact]
    public void RowScopedMutateAdmitsOnlyItsOwnRow_ForeignRowRefused_OwnRowAdmitted() {
        using var fixture = FreshSlottedServer();

        GrantSlot(
            fixture: fixture,
            kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements),
            row: SlotRow
        );

        Laws.RefusalWithControl(
            lawId: "authority.row-scoped-mutate-admits-only-its-own-row",
            deniedOutcome: () => UpsertAndObserveChange(fixture: fixture, row: HostRow),
            controlOutcome: () => UpsertAndObserveChange(fixture: fixture, row: SlotRow));
    }
    [Fact]
    public void SectionGrantStillAdmitsEveryRow_RowGrantAlone_ThenSectionGrant() {
        using var fixture = FreshSlottedServer();
        var everyPlacementKind = WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements);

        // The denied arm holds the slot row and nothing else; the control adds the coarse section row beside it —
        // the ONE reversed fact — and the same foreign-row write then lands, proving a section hold still reaches
        // every row rather than being narrowed by the row grant sitting next to it.
        GrantSlot(
            fixture: fixture,
            kinds: everyPlacementKind,
            row: SlotRow
        );

        Laws.RefusalWithControl(
            lawId: "authority.section-grant-still-admits-every-row",
            deniedOutcome: () => UpsertAndObserveChange(fixture: fixture, row: HostRow),
            controlOutcome: () => {
                fixture.Server.Grant(
                    actor: WorldPrincipal.Console,
                    grant: new WorldGrant(
                        Budget: 16,
                        Capability: WorldCapability.Mutate,
                        Exclusive: false,
                        KindMask: everyPlacementKind,
                        Principal: Partner,
                        Subject: GrantSubject.Section(section: WorldSection.Placements)
                    )
                );

                return UpsertAndObserveChange(fixture: fixture, row: HostRow);
            });
    }
    [Fact]
    public void RowScopedVerbMaskNarrowsTheRow_RemoveMasked_RemoveAdmitted() {
        using var fixture = FreshSlottedServer();
        var upsertOnly = MutationKindMask.Empty.With(ordinal: WorldMutationKindCatalog.OrdinalOf(mutation: SlotUpsert(scale: 1f)));

        GrantSlot(
            fixture: fixture,
            kinds: upsertOnly,
            row: SlotRow
        );

        // The slot row has to exist before a remove can be observed at all, and the upsert-only mask admits that.
        Assert.True(condition: UpsertAndObserveChange(fixture: fixture, row: SlotRow), userMessage: "the upsert-only mask was expected to admit the slot row's own upsert");

        Laws.RefusalWithControl(
            lawId: "authority.row-scoped-verb-mask-narrows-the-row",
            deniedOutcome: () => RemoveAndObserveChange(fixture: fixture, row: SlotRow),
            controlOutcome: () => {
                GrantSlot(
                    fixture: fixture,
                    kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements),
                    row: SlotRow
                );

                return RemoveAndObserveChange(fixture: fixture, row: SlotRow);
            });
    }
    [Fact]
    public void UntrustedRowScopedGrantRequiresAVerbMask_MasklessRefused_MaskedHeld() {
        using var fixture = FreshSlottedServer();

        Laws.RefusalWithControl(
            lawId: "authority.untrusted-row-scoped-grant-requires-a-verb-mask",
            deniedOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: null, subject: GrantSubject.Placement(id: SlotRow)),
            controlOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements), subject: GrantSubject.Placement(id: SlotRow)));
    }
    [Fact]
    public void RowScopedVerbMaskIsBoundedByItsOwnSection_ForeignKindRefused_OwnKindHeld() {
        using var fixture = FreshSlottedServer();

        Laws.RefusalWithControl(
            lawId: "authority.row-scoped-verb-mask-bounded-by-its-section",
            deniedOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Kits), subject: GrantSubject.Creation(id: HostRow)),
            controlOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Creations), subject: GrantSubject.Creation(id: HostRow)));
    }
    [Fact]
    public void AddonHoldsNoRowScopedRow_RowSubjectRefused_SectionSubjectHeld() {
        using var fixture = FreshSlottedServer();
        // The addon name the fixture document declares — an undeclared one would be refused for a different reason.
        var addon = WorldPrincipal.Addon(name: "probe");

        Laws.RefusalWithControl(
            lawId: "authority.addon-holds-no-row-scoped-mutate-row",
            deniedOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements), principal: addon, subject: GrantSubject.Placement(id: SlotRow)),
            controlOutcome: () => GrantAndObserveHeld(fixture: fixture, kinds: WorldMutationKindCatalog.KindsOf(section: WorldSection.Placements), principal: addon, subject: GrantSubject.Section(section: WorldSection.Placements)));
    }

    /// <summary>A server over the collider-bearing fixture document — the only code-built document carrying a
    /// creation and a placement row, which is what <see cref="HostRow"/> names.</summary>
    private static WorldFixture FreshSlottedServer() => Fixtures.FreshServer(definition: Fixtures.BuildGradientUpDocument(gradientUp: false));
    private static void GrantSlot(WorldFixture fixture, string row, MutationKindMask kinds) {
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Principal: Partner,
                Subject: GrantSubject.Placement(id: row)
            )
        );
    }
    private static bool GrantAndObserveHeld(WorldFixture fixture, GrantSubject subject, MutationKindMask? kinds, WorldPrincipal? principal = null) {
        var holder = (principal ?? Partner);

        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Principal: holder,
                Subject: subject
            )
        );

        return fixture.Server.Grants.Allows(
            capability: WorldCapability.Mutate,
            principal: holder,
            subject: subject
        );
    }
    private static bool RemoveAndObserveChange(WorldFixture fixture, string row) {
        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemovePlacement(
            Id: row,
            Principal: Partner
        ));
        fixture.Step();

        return !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes());
    }
    /// <summary>The slot's own placement row, at a scale distinct from the host row's authored 1.0 so an ADMITTED
    /// write over <see cref="HostRow"/> would visibly change the document — without that, a denial and an admitted
    /// no-op would look identical and the law could not fail.</summary>
    private static WorldMutation.UpsertPlacement SlotUpsert(float scale, string row = SlotRow) => new(
        Placement: new WorldPlacement(
            PrototypeId: HostRow,
            Id: row,
            Position: new Vector3(
                x: 12f,
                y: 0f,
                z: 12f
            ),
            Scale: scale,
            YawDegrees: 0f
        ),
        Principal: Partner
    );
    private static bool UpsertAndObserveChange(WorldFixture fixture, string row) {
        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: SlotUpsert(
            row: row,
            scale: 2f
        ));
        fixture.Step();

        return !before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes());
    }
}
