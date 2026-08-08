namespace Puck.World.Protocol;

/// <summary>
/// The kind-tagged vocabulary of live world edits carried over <see cref="IServerLink.SubmitWorldMutation"/> — the
/// closed set of in-flight mutations that <em>is</em> the editor substrate. One coarse record per
/// <see cref="WorldDefinition"/> section, addressed by stable id, whole-row upsert (never a field poke): a genre world
/// arrives as different DATA through these same messages, never a new message shape. Mutations buffer
/// on the server and drain at the tick boundary before intents; each composes a candidate
/// definition, revalidates the whole document, and — on success — swaps the server's live definition, appends to the
/// journal (the undo engine), and rebuilds the changed section's derived state.
/// </summary>
/// <remarks>Every mutation carries its acting <see cref="Principal"/> on the base; the server checks
/// <see cref="WorldCapability.Mutate"/> over the mutation's <see cref="WorldSection"/> before it applies. The base is
/// positional (uniform with <see cref="WorldCommand"/> and <see cref="SessionRequest"/>); the hierarchy stays closed by
/// convention (every kind is a nested sealed record). Every kind also carries a <see cref="MutationKindAttribute"/>
/// declaring its stable dispatch ordinal and the section it targets — discovered and validated at boot by
/// <see cref="WorldMutationKindCatalog"/> (see that type's remarks).</remarks>
/// <param name="Principal">The acting identity the mutation is checked against.</param>
public abstract record WorldMutation(WorldPrincipal Principal) {
    /// <summary>Upserts a locomotion kit row addressed by <see cref="WorldKit.Name"/> — replaces the matching row or
    /// appends a new one.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Kit">The whole kit row.</param>
    [MutationKind(ordinal: 0, section: WorldSection.Kits)]
    public sealed record UpsertKit(WorldPrincipal Principal, WorldKit Kit) : WorldMutation(Principal);

    /// <summary>Removes the kit row named <paramref name="Name"/>. Rejected loudly if the composed document then names
    /// no seat kit or leaves an assignment table dangling (full-document revalidation).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The kit row name to remove.</param>
    [MutationKind(ordinal: 1, section: WorldSection.Kits)]
    public sealed record RemoveKit(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Sets the default seat kit (by name). Rejected if the name matches no kit row.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The kit row name every seat body constructs from.</param>
    [MutationKind(ordinal: 2, section: WorldSection.Kits)]
    public sealed record SetDefaultSeatKit(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Replaces the kit→entity assignment policy (the whole <see cref="WorldRowAssignment"/> row).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Assignment">The assignment policy.</param>
    [MutationKind(ordinal: 3, section: WorldSection.Kits)]
    public sealed record SetKitAssignment(WorldPrincipal Principal, WorldRowAssignment Assignment) : WorldMutation(Principal);

    /// <summary>Upserts a diegetic screen addressed by <see cref="WorldScreen.Index"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Screen">The whole screen row.</param>
    [MutationKind(ordinal: 4, section: WorldSection.Screens)]
    public sealed record UpsertScreen(WorldPrincipal Principal, WorldScreen Screen) : WorldMutation(Principal);

    /// <summary>Removes the screen at <paramref name="Index"/>. Rejected if no screen declares that index.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Index">The engine screen-surface index to remove.</param>
    [MutationKind(ordinal: 5, section: WorldSection.Screens)]
    public sealed record RemoveScreen(WorldPrincipal Principal, int Index) : WorldMutation(Principal);

    /// <summary>Upserts a placeable camera addressed by <see cref="WorldCamera.Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Camera">The whole camera row.</param>
    [MutationKind(ordinal: 6, section: WorldSection.Cameras)]
    public sealed record UpsertCamera(WorldPrincipal Principal, WorldCamera Camera) : WorldMutation(Principal);

    /// <summary>Removes the camera named <paramref name="Name"/>. Rejected if a View screen still references it.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The camera name to remove.</param>
    [MutationKind(ordinal: 7, section: WorldSection.Cameras)]
    public sealed record RemoveCamera(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Replaces the whole seat spawn-point list (order maps slots; takes effect at the next seat activation).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Spawns">The spawn points.</param>
    [MutationKind(ordinal: 8, section: WorldSection.Spawns)]
    public sealed record SetSpawns(WorldPrincipal Principal, IReadOnlyList<WorldSpawnPoint> Spawns) : WorldMutation(Principal);

    /// <summary>Replaces the profileless locomotion defaults.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Motion">The motion tuning.</param>
    [MutationKind(ordinal: 9, section: WorldSection.Motion)]
    public sealed record SetMotion(WorldPrincipal Principal, WorldMotionDefaults Motion) : WorldMutation(Principal);

    /// <summary>Declares (idempotently — re-declaring is a no-op) or removes a property NAME in the registry — ONE
    /// kind, two shapes, distinguished by <see cref="Remove"/>: the SAME one-kind-two-shapes pattern
    /// <see cref="SettleOwnership"/> uses for accept/reclaim. A property registration carries only a name and the
    /// toggle itself — upsert and remove differ by a single bit, not by payload, so one kind covers both shapes
    /// naturally (unlike <see cref="UpsertInteraction"/>/<see cref="RemoveInteraction"/>, whose two payloads do not
    /// fit one shape). Upsert (<see cref="Remove"/> = <see langword="false"/>) is rejected loudly at whole-document
    /// validation if <see cref="Name"/> is not a legitimate <see cref="WorldCellName"/> spelling, or names no
    /// declared keyed <c>int</c> <c>state</c> row of the SAME name (a property's per-carrier tags are stored there —
    /// see <see cref="WorldPropertyRegistrySection"/>'s remarks). Remove (<see cref="Remove"/> =
    /// <see langword="true"/>) is rejected loudly if no property declares that name, or if a live
    /// <see cref="WorldInteraction"/> row still references it (the conservative no-cascade ruling — remove or
    /// re-target the interactions first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The property name to declare or remove.</param>
    /// <param name="Remove"><see langword="false"/> to declare/re-declare; <see langword="true"/> to remove.</param>
    [MutationKind(ordinal: 10, section: WorldSection.Properties)]
    public sealed record SetProperty(WorldPrincipal Principal, string Name, bool Remove = false) : WorldMutation(Principal);

    /// <summary>Replaces the census defaults (document-only; the live census stays the <c>world.population</c> verb's
    /// session state).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Population">The census defaults.</param>
    [MutationKind(ordinal: 11, section: WorldSection.Population)]
    public sealed record SetPopulationDefaults(WorldPrincipal Principal, WorldPopulationDefaults Population) : WorldMutation(Principal);

    /// <summary>Replaces the render-lever defaults and quality-preset table (document-only; live render levers stay
    /// <c>WorldRenderSettings</c>).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Render">The render defaults.</param>
    [MutationKind(ordinal: 12, section: WorldSection.Render)]
    public sealed record SetRenderDefaults(WorldPrincipal Principal, WorldRenderDefaults Render) : WorldMutation(Principal);

    /// <summary>Upserts a data-side addon descriptor addressed by <see cref="WorldAddonRow.Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Addon">The addon row.</param>
    [MutationKind(ordinal: 13, section: WorldSection.Addons)]
    public sealed record UpsertAddon(WorldPrincipal Principal, WorldAddonRow Addon) : WorldMutation(Principal);

    /// <summary>Removes the addon named <paramref name="Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The addon name to remove.</param>
    [MutationKind(ordinal: 14, section: WorldSection.Addons)]
    public sealed record RemoveAddon(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a per-world binding overlay addressed by <see cref="WorldBindingOverlay.Id"/> — replaces the
    /// matching row or appends a new one. Rejected loudly if the composed mapping (default ⊕ every overlay) then fails to
    /// compile.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Overlay">The whole overlay row.</param>
    [MutationKind(ordinal: 15, section: WorldSection.Bindings)]
    public sealed record UpsertBindingOverlay(WorldPrincipal Principal, WorldBindingOverlay Overlay) : WorldMutation(Principal);

    /// <summary>Removes the binding overlay with id <paramref name="Id"/>. Rejected if no overlay declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The overlay id to remove.</param>
    [MutationKind(ordinal: 16, section: WorldSection.Bindings)]
    public sealed record RemoveBindingOverlay(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a creation ASSET row addressed by <see cref="WorldCreation.Id"/>. The compose boundary
    /// canonicalizes the row's document (doc + hash always come from the SAME <see cref="Puck.Forge.Authoring.CanonicalDocument{TDocument}"/>)
    /// and rejects loudly when the carried hash does not match the canonical one — a hash the pipeline did not itself
    /// compute is never accepted.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Creation">The whole creation row.</param>
    [MutationKind(ordinal: 17, section: WorldSection.Creations)]
    public sealed record UpsertCreation(WorldPrincipal Principal, WorldCreation Creation) : WorldMutation(Principal);

    /// <summary>Removes the creation row with id <paramref name="Id"/>. Rejected loudly when no row declares that id
    /// OR when live placements still reference it (the conservative no-cascade ruling — remove the placements first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The creation id to remove.</param>
    [MutationKind(ordinal: 18, section: WorldSection.Creations)]
    public sealed record RemoveCreation(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a placement INSTANCE row addressed by <see cref="WorldPlacement.Id"/>. Rejected loudly
    /// when it names no creation row, violates the placement policy envelope, or would exceed the probed render
    /// envelope (the capacity-honesty contract).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Placement">The whole placement row.</param>
    [MutationKind(ordinal: 19, section: WorldSection.Placements)]
    public sealed record UpsertPlacement(WorldPrincipal Principal, WorldPlacement Placement) : WorldMutation(Principal);

    /// <summary>Removes the placement row with id <paramref name="Id"/>. Rejected if no row declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The placement id to remove.</param>
    [MutationKind(ordinal: 20, section: WorldSection.Placements)]
    public sealed record RemovePlacement(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a placeable speaker addressed by <see cref="WorldSpeaker.Name"/> (the camera pair's audio
    /// sibling — whole-row, <c>$type fixed|anchored|bed</c>).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Speaker">The whole speaker row.</param>
    [MutationKind(ordinal: 21, section: WorldSection.Speakers)]
    public sealed record UpsertSpeaker(WorldPrincipal Principal, WorldSpeaker Speaker) : WorldMutation(Principal);

    /// <summary>Removes the speaker named <paramref name="Name"/>. Rejected if no row declares that name.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The speaker name to remove.</param>
    [MutationKind(ordinal: 22, section: WorldSection.Speakers)]
    public sealed record RemoveSpeaker(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a tune ASSET row addressed by <see cref="WorldTune.Id"/>. The compose boundary
    /// re-canonicalizes the embedded <c>puck.audio.v1</c> document and REJECTS a hash the pipeline did not itself
    /// compute, the same rule as <see cref="UpsertCreation"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Tune">The whole tune row.</param>
    [MutationKind(ordinal: 23, section: WorldSection.Tunes)]
    public sealed record UpsertTune(WorldPrincipal Principal, WorldTune Tune) : WorldMutation(Principal);

    /// <summary>Removes the tune row with id <paramref name="Id"/>. Rejected loudly while speakers still reference it
    /// (the conservative no-cascade ruling — retarget or remove the speakers first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The tune id to remove.</param>
    [MutationKind(ordinal: 24, section: WorldSection.Tunes)]
    public sealed record RemoveTune(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a synth-patch ASSET row addressed by <see cref="WorldPatch.Id"/> — the <c>puck.synth.v1</c>
    /// twin of <see cref="UpsertTune"/>, same canonicalize + hash-pin boundary.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Patch">The whole patch row.</param>
    [MutationKind(ordinal: 25, section: WorldSection.Patches)]
    public sealed record UpsertPatch(WorldPrincipal Principal, WorldPatch Patch) : WorldMutation(Principal);

    /// <summary>Removes the patch row with id <paramref name="Id"/>. Rejected loudly while speakers or emission
    /// facets still reference it (no cascade — the dependents are named).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The patch id to remove.</param>
    [MutationKind(ordinal: 26, section: WorldSection.Patches)]
    public sealed record RemovePatch(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Replaces the audio host-section defaults (the whole <see cref="WorldAudioDefaults"/> row). Applies
    /// LIVE: the emitter-derivation coalescing, the listener policy, and the cue table read the delivered row.
    /// <c>MasterGain</c> follows the lever-precedence rule: it flows live only until the <c>world.volume</c> session
    /// lever engages — thereafter the lever owns "now" and the field owns the next boot (<c>world.save</c> folds the
    /// lever back into it).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Audio">The audio defaults row.</param>
    [MutationKind(ordinal: 27, section: WorldSection.Audio)]
    public sealed record SetAudioDefaults(WorldPrincipal Principal, WorldAudioDefaults Audio) : WorldMutation(Principal);

    /// <summary>Replaces the whole editor/authoring policy row. A single whole-row mutation carries both
    /// consumption classes the row holds (see <see cref="WorldAuthoringDefaults"/>'s remarks): the boot-consumed
    /// headroom/repeat-cap fields apply at the NEXT boot (the frozen render-envelope probe cannot retroactively grow),
    /// while the live-consumed candidate/layout/preview fields apply at the very next tick — the server's accept echo
    /// narrates the split honestly rather than picking one class for the whole row.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Authoring">The whole authoring policy row.</param>
    [MutationKind(ordinal: 28, section: WorldSection.Authoring)]
    public sealed record SetAuthoringDefaults(WorldPrincipal Principal, WorldAuthoringDefaults Authoring) : WorldMutation(Principal);

    /// <summary>Replaces the whole contact-solver tuning (the <see cref="WorldCollision"/> section). Applies LIVE: the
    /// population rebuilds the collider set and hands it to every body on the next tick.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Collision">The contact-solver tuning.</param>
    [MutationKind(ordinal: 29, section: WorldSection.Collision)]
    public sealed record SetCollision(WorldPrincipal Principal, WorldCollision Collision) : WorldMutation(Principal);

    /// <summary>Replaces the whole host-section defaults row (window/backend/present/pacing/timing/genlock). DOCUMENT-
    /// DEFAULTS class: the boot-only fields take effect at the NEXT boot (a running window cannot resize its backend or
    /// surface), and the two live-lever fields (<c>TargetHertz</c> via <c>world.target</c>, <c>Timing</c> via
    /// <c>world.timing</c>) set the value the next boot wakes on — <c>world.save</c> folds the running levers back into
    /// them. The row is validated immediately, so a bad value is rejected loudly regardless of when it applies.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Host">The whole host defaults row.</param>
    [MutationKind(ordinal: 30, section: WorldSection.Host)]
    public sealed record SetHostDefaults(WorldPrincipal Principal, WorldHostDefaults Host) : WorldMutation(Principal);

    /// <summary>Replaces the whole window-composition defaults row — the seat framing plus the authored layouts (see
    /// <see cref="WorldViewDefaults"/>). Applies LIVE: the frame source recompiles the seat rigs and the composer reads
    /// the new layouts on the next produced frame. The <c>world.row.set views.seatRig</c> verb RMWs the seat rig into this.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Views">The whole views defaults row.</param>
    [MutationKind(ordinal: 31, section: WorldSection.Views)]
    public sealed record SetViewDefaults(WorldPrincipal Principal, WorldViewDefaults Views) : WorldMutation(Principal);

    /// <summary>Replaces the whole player-defaults row — the seed palette, the picker tuning, and the CONTROL FEEL a
    /// seat of this document wakes with. Applies LIVE: every seat still sitting at the world's own feel picks the new
    /// policy up on its very next drag, while a seat carrying its own profile's feel is deliberately untouched. The
    /// <c>world.row.set playerDefaults.seatLook</c> verb RMWs the feel into this.</summary>
    /// <remarks>Ordinal 64 because ordinals are APPEND-ONLY — they ride the replay wire, so renumbering to sit this
    /// beside the other whole-row section writes would invalidate every recorded tape. Position in this file is
    /// topical; the ordinal is not.</remarks>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Defaults">The whole player-defaults row.</param>
    [MutationKind(ordinal: 64, section: WorldSection.PlayerDefaults)]
    public sealed record SetPlayerDefaults(WorldPrincipal Principal, WorldPlayerDefaults Defaults) : WorldMutation(Principal);

    /// <summary>Upserts one named window layout addressed by <see cref="WorldViewLayout.Name"/> — replaces the matching
    /// row or appends a new one (the views section's layout grain).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Layout">The whole layout row.</param>
    [MutationKind(ordinal: 32, section: WorldSection.Views)]
    public sealed record UpsertViewLayout(WorldPrincipal Principal, WorldViewLayout Layout) : WorldMutation(Principal);

    /// <summary>Removes the window layout named <paramref name="Name"/>. Always allowed — the composer falls back to the
    /// authored/built-in selection when the active layout is removed.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The layout name to remove.</param>
    [MutationKind(ordinal: 33, section: WorldSection.Views)]
    public sealed record RemoveViewLayout(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a LOOK row (whole-row, keyed by name) into the <see cref="WorldSection.Looks"/> section. Applies
    /// LIVE — the population re-resolves the look table and the client rebuilds the avatar program (the appearance
    /// change is visible the next frame). Riding the render-envelope gate: a creation look changes the emitted program
    /// word count.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Look">The whole look row.</param>
    [MutationKind(ordinal: 34, section: WorldSection.Looks)]
    public sealed record UpsertLook(WorldPrincipal Principal, WorldLook Look) : WorldMutation(Principal);

    /// <summary>Removes a LOOK row by name. Rejected loudly by full-document revalidation while the look assignment
    /// table still names it.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The look name to remove.</param>
    [MutationKind(ordinal: 35, section: WorldSection.Looks)]
    public sealed record RemoveLook(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Sets the look→entity assignment policy (the same <see cref="WorldRowAssignment"/> primitive the kit
    /// assignment uses). Applies LIVE.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Assignment">The look assignment policy.</param>
    [MutationKind(ordinal: 36, section: WorldSection.Looks)]
    public sealed record SetLookAssignment(WorldPrincipal Principal, WorldRowAssignment Assignment) : WorldMutation(Principal);

    /// <summary>Upserts a cable-link row (whole-row, keyed by name) into the <see cref="WorldSection.Links"/> section —
    /// the durable twin of the <c>screen.link</c> verb. Applies LIVE: the binder reconciles the declared links to the
    /// new definition, establishing (or reporting dormant) the group. Rejected by full-document revalidation when a
    /// named screen is undeclared, a screen is in two links, or fewer than two screens are named.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Link">The whole cable-link row.</param>
    [MutationKind(ordinal: 37, section: WorldSection.Links)]
    public sealed record UpsertScreenLink(WorldPrincipal Principal, WorldScreenLink Link) : WorldMutation(Principal);

    /// <summary>Removes a cable-link row by name. Rejected loudly when no row declares that name.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The link name to remove.</param>
    [MutationKind(ordinal: 38, section: WorldSection.Links)]
    public sealed record RemoveScreenLink(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a document-authored grant row (see <see cref="WorldDefinition.Grants"/>) — replaces the row
    /// matching the SAME (<see cref="WorldGrant.Principal"/>, <see cref="WorldGrant.Capability"/>,
    /// <see cref="WorldGrant.Subject"/>) triple, or appends a new one; a bare re-set of an existing triple changes
    /// only <see cref="WorldGrant.Exclusive"/>. DOCUMENT-ONLY, like <see cref="UpsertAddon"/>: this mutation edits
    /// what the NEXT BOOT applies through <c>Server.WorldServer.Grant</c> — it never touches the LIVE grant
    /// table <c>world.grant</c>/<c>world.revoke</c> administer, so a row added here grants nothing until a relaunch.</summary>
    /// <param name="Principal">The acting identity (checked against Mutate/section:grants).</param>
    /// <param name="Row">The whole grant row.</param>
    [MutationKind(ordinal: 39, section: WorldSection.Grants)]
    public sealed record UpsertGrant(WorldPrincipal Principal, WorldGrant Row) : WorldMutation(Principal);

    /// <summary>Removes the document-authored grant row matching <paramref name="Target"/>'s
    /// (<see cref="WorldGrant.Principal"/>, <see cref="WorldGrant.Capability"/>, <see cref="WorldGrant.Subject"/>) —
    /// <see cref="WorldGrant.Exclusive"/> is ignored, matching <c>world.revoke</c>'s own shape. Rejected if no row
    /// matches. DOCUMENT-ONLY, like <see cref="RemoveAddon"/>: never touches the live grant table.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Target">The (principal, capability, subject) to remove — <see cref="WorldGrant.Exclusive"/> is
    /// ignored.</param>
    [MutationKind(ordinal: 40, section: WorldSection.Grants)]
    public sealed record RemoveGrant(WorldPrincipal Principal, WorldGrant Target) : WorldMutation(Principal);

    /// <summary>Upserts a HUD panel row addressed by <see cref="WorldHudPanel.Id"/> — replaces the matching row or
    /// appends a new one. The panel's <see cref="WorldHudPanel.Elements"/> travel WITH it: this is the cross-row
    /// transaction boundary a whole panel (chrome + every child element) commits under, distinct from
    /// <see cref="UpsertHudElement"/>'s single-element read-modify-write. Rejected loudly when the composed document
    /// would exceed <see cref="WorldHudCapacity.MaxWorldPanels"/> panels, when the row's own element count exceeds
    /// <see cref="WorldHudCapacity.MaxElementsPerPanel"/>, or when an element names an unknown binding.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Panel">The whole panel row, elements included.</param>
    [MutationKind(ordinal: 41, section: WorldSection.Hud)]
    public sealed record UpsertHudPanel(WorldPrincipal Principal, WorldHudPanel Panel) : WorldMutation(Principal);

    /// <summary>Removes the HUD panel row with id <paramref name="Id"/> (its elements go with it). Rejected if no row
    /// declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The panel id to remove.</param>
    [MutationKind(ordinal: 42, section: WorldSection.Hud)]
    public sealed record RemoveHudPanel(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts one HUD element (whole-row, keyed by <see cref="WorldHudElement.Id"/>) inside an
    /// ALREADY-DECLARED panel — a read-modify-write on that panel's <see cref="WorldHudPanel.Elements"/> list, replacing
    /// the matching row or appending a new one. Rejected loudly when no panel named <paramref name="PanelId"/> exists,
    /// when the panel's element count would exceed <see cref="WorldHudCapacity.MaxElementsPerPanel"/>, or when the
    /// element names an unknown binding.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="PanelId">The owning panel's id.</param>
    /// <param name="Element">The whole element row.</param>
    [MutationKind(ordinal: 43, section: WorldSection.Hud)]
    public sealed record UpsertHudElement(WorldPrincipal Principal, string PanelId, WorldHudElement Element) : WorldMutation(Principal);

    /// <summary>Removes one HUD element by id from an already-declared panel. Rejected if the panel or the element does
    /// not exist.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="PanelId">The owning panel's id.</param>
    /// <param name="ElementId">The element id to remove.</param>
    [MutationKind(ordinal: 44, section: WorldSection.Hud)]
    public sealed record RemoveHudElement(WorldPrincipal Principal, string PanelId, string ElementId) : WorldMutation(Principal);

    /// <summary>Replaces the HUD section's defaults row (see <see cref="WorldHudDefaults"/>). Applies LIVE.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Defaults">The whole HUD defaults row.</param>
    [MutationKind(ordinal: 45, section: WorldSection.Hud)]
    public sealed record SetHudDefaults(WorldPrincipal Principal, WorldHudDefaults Defaults) : WorldMutation(Principal);

    /// <summary>Upserts a <c>state</c> row addressed by <see cref="WorldStateRow.Name"/> — replaces the matching row
    /// or appends a new one. Applies LIVE. Checked TWICE: the standard <see cref="WorldCapability.Mutate"/> hold over
    /// <see cref="WorldSection.State"/> every mutation kind requires, PLUS a second, row-scoped
    /// <see cref="WorldCapability.Edit"/> hold over the CONCRETE <c>state:&lt;name&gt;</c> subject the row names (or
    /// the <see cref="GrantSubject.All"/> wildcard) — narrower authority than every other section's single Mutate
    /// gate, matching the campaign's "concrete rows" ruling.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Row">The whole state row.</param>
    [MutationKind(ordinal: 46, section: WorldSection.State)]
    public sealed record UpsertStateRow(WorldPrincipal Principal, WorldStateRow Row) : WorldMutation(Principal);

    /// <summary>Removes the state row named <paramref name="Name"/>. Rejected if no row declares that name. Checked
    /// TWICE — see <see cref="UpsertStateRow"/>'s remarks.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The state row name to remove.</param>
    [MutationKind(ordinal: 47, section: WorldSection.State)]
    public sealed record RemoveStateRow(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts one CELL inside an already-declared <see cref="WorldStateRow"/> — a per-cell write, never a
    /// whole-row re-authoring (that is <see cref="UpsertStateRow"/>'s job). Applies LIVE. Rejected loudly (compose
    /// failure, definition unchanged) if <see cref="Row"/> names no state row; rejected by the whole-document
    /// revalidation if the resulting value falls outside the row's declared envelope, a non-negative row's value
    /// would go negative, the write would grow the row past its effective capacity, or (a <c>Text</c>-kind row) the
    /// written text exceeds <see cref="WorldStateCapacity.MaxTextValueLength"/>. Reaches ANY row — a slot-shaped
    /// row's implicit cell (keyed <see cref="WorldStateRow.SlotKey"/>) as much as an author-keyed cell, since a slot
    /// IS a table with one key (see <see cref="WorldStateRow"/>'s remarks); the console's <c>world.state.cell.set</c>
    /// (numeric/bool and text alike, dispatching on the row's declared kind) / <c>world.state.cell.remove</c> verbs are its whole
    /// spelling, in the same verb family the whole-row pair uses — <see cref="Value"/> carries the former,
    /// <see cref="Text"/> the latter, and a row's own <c>Kind</c> decides which of the two the compose arm reads.
    /// Checked TWICE, mirroring <see cref="UpsertStateRow"/>'s "concrete rows" ruling exactly: the standard
    /// <see cref="WorldCapability.Mutate"/> hold over <see cref="WorldSection.State"/> every mutation kind requires,
    /// PLUS a second, row-scoped <see cref="WorldCapability.Edit"/> hold over the CONCRETE <c>state:&lt;name&gt;</c>
    /// subject <see cref="Row"/> names (or the <see cref="GrantSubject.All"/> wildcard) — the SAME subject
    /// <see cref="UpsertStateRow"/> is checked against (<c>GrantSubjectKind.Table</c>, the former separate subject
    /// this mutation's own <c>table:&lt;name&gt;</c> hold used, is retired — one row, one subject).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Row">The carrying row's name.</param>
    /// <param name="Key">The cell's stable string key.</param>
    /// <param name="Value">The operand, raw-encoded per the row's declared <c>Kind</c>, for a caller that already
    /// knows that <c>Kind</c> when it builds this mutation (the rule-effect engine reads the destination row itself
    /// before submitting). Ignored when <see cref="RawToken"/> is set. Unused (left <c>0</c>) on a <c>Text</c>-kind
    /// row, where <see cref="Text"/> carries the operand instead.</param>
    /// <param name="Kind">Whether to replace the key's value or add the operand to its current value (absent key ==
    /// zero for an add) — a <c>Text</c>-kind row admits only <see cref="WorldDocumentWriteKind.Set"/>, since a
    /// string has no addition.</param>
    /// <param name="Text">The literal text for a <c>Text</c>-kind row's cell; <see langword="null"/> for every other
    /// kind, where <see cref="Value"/> or <see cref="RawToken"/> carries the operand. Its non-null-ness is what marks
    /// this write a TEXT write at compose — an empty string is still a text write, never <see langword="null"/>.</param>
    /// <param name="RawToken">The human-authored wire token for a numeric/bool write (decimal text for
    /// <c>Fixed</c>, <c>true</c>/<c>false</c> for <c>Bool</c>, an integer literal otherwise) — <see langword="null"/>
    /// for a caller that already resolved <see cref="Value"/>, and for every <c>Text</c>-kind write. An ingress that
    /// types a row name the SAME batch may still be declaring (<c>world.state.cell.set</c>) cannot know the row's
    /// <c>Kind</c> before this mutation composes — parsing "-5" as a fixed-point raw bit pattern versus a plain
    /// integer is exactly the decision that needs the row, and the row may not exist yet at submit time. Carrying the
    /// token uninterpreted and parsing it at compose, against the CANDIDATE row's <c>Kind</c> (the same document a
    /// same-batch <see cref="UpsertStateRow"/> ahead of this one has already installed into), is what makes a
    /// same-batch declare-then-write deterministic: see <see cref="WorldStateCellWriter"/>'s token parser, which the
    /// compose arm runs when this is set.</param>
    [MutationKind(ordinal: 49, section: WorldSection.State)]
    public sealed record UpsertStateCell(WorldPrincipal Principal, string Row, string Key, long Value, WorldDocumentWriteKind Kind, string? Text = null, string? RawToken = null) : WorldMutation(Principal);

    /// <summary>Removes one cell from an already-declared <see cref="WorldStateRow"/>. Rejected if <see cref="Row"/>
    /// names no state row, or if no cell inside it carries <see cref="Key"/>. Checked TWICE — see
    /// <see cref="UpsertStateCell"/>'s remarks.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Row">The carrying row's name.</param>
    /// <param name="Key">The key to remove.</param>
    [MutationKind(ordinal: 50, section: WorldSection.State)]
    public sealed record RemoveStateCell(WorldPrincipal Principal, string Row, string Key) : WorldMutation(Principal);

    /// <summary>Replaces the participant input-hold policy.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Settings">The whole input-hold section.</param>
    [MutationKind(ordinal: 48, section: WorldSection.InputHold)]
    public sealed record SetInputHold(WorldPrincipal Principal, WorldInputHoldSettings Settings) : WorldMutation(Principal);

    /// <summary>
    /// Runs ONE emission of a GENERATOR row (a <see cref="WorldStateRow"/> declaring a <see cref="WorldGenerator"/>)
    /// and writes the space-joined result into a text cell — the sampling primitive the whole Markov family reduces
    /// to, and the ONE mechanism the <c>world.generate</c> console verb, a kit's <c>ActionEffect.Generate</c>, and a
    /// world rule's own generate effect all submit.
    /// </summary>
    /// <remarks>
    /// <para><b>A pure function of the candidate document and the instance identity.</b> Composing this mutation
    /// resolves the site's source (named or inlined), SEEKS the PRNG to the position the site's own
    /// <see cref="WorldStateRow.DrawCursor"/> records — an O(1) jump, never a replay — draws, and writes BOTH the
    /// drawn value and the advanced cursor/decks into the SAME candidate. Nothing lives outside the document, so
    /// <c>world.undo</c> rewinds a draw position bit-identically by the ordinary whole-document restore — there is no
    /// separate runtime to reconcile, and no tape record of a draw to keep in step.</para>
    /// <para><b>Authority: ONE hold, plus the mask.</b> The standard <see cref="WorldCapability.Mutate"/> hold over
    /// <see cref="WorldSection.State"/>, PLUS the row-scoped <see cref="WorldCapability.Edit"/> hold over the
    /// CONCRETE <c>state:&lt;Row&gt;</c> subject this mutation WRITES — the identical subject
    /// <see cref="UpsertStateCell"/> checks, so no new grant vocabulary appears. Beneath it, the deciding row's
    /// <see cref="MutationKindMask"/> is exactly the fire-without-redefine separation masks were ratified for: a row
    /// masked <c>verbs:Generate</c> can redraw that site but cannot re-author it. Advancing the site's own cursor is
    /// engine bookkeeping intrinsic to drawing and is not separately gated; re-authoring a site's facet, or the
    /// SOURCE it references, is an <see cref="UpsertStateRow"/> against that row and is gated there, which is where
    /// the interesting authority actually sits.</para>
    /// <para>Rejected loudly (compose failure, definition unchanged) if <see cref="Row"/> names no state row, names
    /// one declaring no draw, or names one whose <see cref="WorldDraw.Timing"/> is
    /// <see cref="WorldDrawTiming.Boot"/> (drawn once at first fill, never again); if the site's facet resolves to no
    /// source; if the source's emission kind the site cannot hold; if a Markov walk reaches
    /// <see cref="WorldGenerator.Bound"/> tokens without terminating; or if a
    /// <see cref="WorldGeneratorMode.WithoutReplacement"/> context is exhausted. Rejected by the whole-document
    /// revalidation if a joined emission exceeds <see cref="WorldStateCapacity.MaxTextValueLength"/>.</para>
    /// </remarks>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Row">The draw site's row name.</param>
    [MutationKind(ordinal: 51, section: WorldSection.State)]
    public sealed record Generate(WorldPrincipal Principal, string Row) : WorldMutation(Principal);

    /// <summary>Upserts a world rule addressed by <see cref="WorldRule.Name"/> — the AUTHORING door for the
    /// <c>rules</c> section, never the firing one: a rule EVALUATING and its effects APPLYING both ride
    /// <see cref="WorldPrincipal.World"/> and never submit this kind. Rejected loudly if the rule fails to compile
    /// against the candidate document (an undeclared state row or cell, an inadmissible predicate/effect kind for
    /// world scope, an unknown reserved channel) — see <c>WorldRuleCompiler</c>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Rule">The whole rule row.</param>
    [MutationKind(ordinal: 52, section: WorldSection.Rules)]
    public sealed record UpsertWorldRule(WorldPrincipal Principal, WorldRule Rule) : WorldMutation(Principal);

    /// <summary>Removes the world rule named <paramref name="Name"/>. Rejected if no rule declares that name.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The rule name to remove — a <see cref="WorldCellName"/>, the same validated-identifier type
    /// <see cref="WorldRule.Name"/> itself rides, so a name this mutation could never match is refused at the verb
    /// (or the JSON converter) instead of travelling as a miss.</param>
    [MutationKind(ordinal: 53, section: WorldSection.Rules)]
    public sealed record RemoveWorldRule(WorldPrincipal Principal, WorldCellName Name) : WorldMutation(Principal);

    /// <summary>Upserts an interaction row addressed by <see cref="WorldInteraction.Name"/> — replaces the matching
    /// row or appends a new one. The AUTHORING door for the <c>interactions</c> section, never the firing one: an
    /// interaction EVALUATING and its effects APPLYING both ride <see cref="WorldPrincipal.World"/> and never submit
    /// this kind (the SAME split <see cref="UpsertWorldRule"/> draws for rules). Rejected loudly if the row fails to
    /// compile against the candidate document — an unregistered <c>left</c>/<c>right</c> property, an unknown region
    /// placement, or an inadmissible effect kind — see <c>WorldRuleCompiler.CompileAllInteractions</c>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Interaction">The whole interaction row.</param>
    [MutationKind(ordinal: 54, section: WorldSection.Interactions)]
    public sealed record UpsertInteraction(WorldPrincipal Principal, WorldInteraction Interaction) : WorldMutation(Principal);

    /// <summary>Removes the interaction named <paramref name="Name"/>. Rejected if no interaction declares that
    /// name.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The interaction name to remove — a <see cref="WorldCellName"/>, the same
    /// validated-identifier type <see cref="WorldInteraction.Name"/> itself rides, so a name this mutation could
    /// never match is refused at the verb (or the JSON converter) instead of travelling as a miss.</param>
    [MutationKind(ordinal: 55, section: WorldSection.Interactions)]
    public sealed record RemoveInteraction(WorldPrincipal Principal, WorldCellName Name) : WorldMutation(Principal);

    /// <summary>Upserts a group KIND addressed by <see cref="WorldGroupKind.Name"/> — replaces the matching row or
    /// appends a new one. Rejected loudly if the resulting kind set contains two kinds identical in every
    /// behavior-bearing field except <see cref="WorldGroupKind.Capacity"/> (a capacity-only difference is a VALUE, not
    /// a kind), or if <see cref="WorldGroupKind.SharedStateScope"/> names no declared <c>state</c> row.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Kind">The whole kind row.</param>
    [MutationKind(ordinal: 56, section: WorldSection.Groups)]
    public sealed record UpsertGroupKind(WorldPrincipal Principal, WorldGroupKind Kind) : WorldMutation(Principal);

    /// <summary>Removes the group kind named <paramref name="Name"/>. Rejected loudly while a live group row still
    /// names it (the conservative no-cascade ruling — remove or re-kind the groups first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The kind name to remove.</param>
    [MutationKind(ordinal: 57, section: WorldSection.Groups)]
    public sealed record RemoveGroupKind(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Forms a new, empty RUNTIME group row of a declared kind. Rejected loudly if <see cref="Id"/> is
    /// already taken or <see cref="KindName"/> names no declared kind (validated vocabulary — unknown-by-name).
    /// Never written back to the server's base document, so a whole-document rebuild (<c>world.reset</c>/<c>.load</c>/
    /// <c>.reload</c>) discards it — the runtime half of the party-vs-roster split.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The new group's stable id.</param>
    /// <param name="KindName">The declared kind it forms under.</param>
    [MutationKind(ordinal: 58, section: WorldSection.Groups)]
    public sealed record FormGroup(WorldPrincipal Principal, string Id, string KindName) : WorldMutation(Principal);

    /// <summary>Adds <see cref="Member"/> to the group named <see cref="GroupId"/>. Rejected loudly if the group does
    /// not exist, <see cref="Member"/> already belongs, admitting it would exceed the kind's declared
    /// <see cref="WorldGroupKind.Capacity"/>, or <see cref="Member"/> is not a legitimate member kind (a
    /// <see cref="PrincipalKind.Group"/> value is refused BY NAME — FLAT ONLY, a member is a principal, never a
    /// group; <see cref="PrincipalKind.World"/>/<see cref="PrincipalKind.Document"/> are refused the same way — neither
    /// is a real actor).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="GroupId">The target group's id.</param>
    /// <param name="Member">The principal to admit.</param>
    [MutationKind(ordinal: 59, section: WorldSection.Groups)]
    public sealed record JoinGroup(WorldPrincipal Principal, string GroupId, WorldPrincipal Member) : WorldMutation(Principal);

    /// <summary>Removes <see cref="Member"/> from the group named <see cref="GroupId"/> — voluntary self-departure:
    /// always just removes the one row (never the kind's <see cref="WorldGroupKind.EvictionPolicy"/>, which governs
    /// <see cref="KickMember"/> alone), then dissolves the whole group if that empties it and the kind's
    /// <see cref="WorldGroupKind.Lifetime"/> is <see cref="WorldGroupLifetime.Ephemeral"/>. Rejected loudly if the
    /// group does not exist or <see cref="Member"/> does not belong to it.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="GroupId">The target group's id.</param>
    /// <param name="Member">The principal to remove.</param>
    [MutationKind(ordinal: 60, section: WorldSection.Groups)]
    public sealed record LeaveGroup(WorldPrincipal Principal, string GroupId, WorldPrincipal Member) : WorldMutation(Principal);

    /// <summary>Removes <see cref="Member"/> from the group named <see cref="GroupId"/> under the kind's OWN
    /// <see cref="WorldGroupKind.EvictionPolicy"/> — <see cref="WorldGroupEvictionPolicy.Remove"/> drops only the
    /// member's row (then dissolves the group under the SAME <see cref="WorldGroupLifetime.Ephemeral"/> rule
    /// <see cref="LeaveGroup"/> applies), <see cref="WorldGroupEvictionPolicy.Disband"/> drops the WHOLE group row
    /// immediately. Authority is the SAME <see cref="WorldCapability.Mutate"/>/<c>section:groups</c> hold every group
    /// mutation checks — the kind decides the CONSEQUENCE of a kick, never who may issue one. Rejected loudly if the
    /// group does not exist or <see cref="Member"/> does not belong to it.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="GroupId">The target group's id.</param>
    /// <param name="Member">The principal to remove.</param>
    [MutationKind(ordinal: 61, section: WorldSection.Groups)]
    public sealed record KickMember(WorldPrincipal Principal, string GroupId, WorldPrincipal Member) : WorldMutation(Principal);

    /// <summary>Places a Principal-owned subject into ESCROW — the durable INTERMEDIATE OWNER a trade parks a
    /// subject in, owned by neither party while it holds (see <see cref="OwnershipOwnerKind.Escrow"/>). Rejected
    /// loudly if <see cref="Subject"/> names no declared <see cref="WorldOwnership"/> row, that row's owner is not
    /// <see cref="OwnershipOwnerKind.Principal"/>, <see cref="Principal"/> does not equal that row's OWN owner (only
    /// the current owner may offer — group-owned and already-escrowed subjects are refused, never silently
    /// re-offered), <see cref="Recipient"/> equals <see cref="Principal"/> (an offer to oneself is not a trade), or
    /// <see cref="DeadlineTick"/> does not lie strictly after the tick this mutation applies at (a deadline that has
    /// already passed could never be accepted before <see cref="SettleOwnership"/>'s reclaim admits, so it is refused
    /// rather than silently accepted as a zero-length window). The counterpart mutation is
    /// <see cref="SettleOwnership"/>, the only door back OUT of escrow.</summary>
    /// <param name="Principal">The acting identity — must equal the subject's current owner.</param>
    /// <param name="Subject">The subject to place into escrow.</param>
    /// <param name="Recipient">The principal named to accept the subject.</param>
    /// <param name="DeadlineTick">The tick at or after which a reclaim (see <see cref="SettleOwnership"/>) admits.</param>
    [MutationKind(ordinal: 62, section: WorldSection.Groups)]
    public sealed record OfferOwnership(WorldPrincipal Principal, OwnershipSubject Subject, WorldPrincipal Recipient, long DeadlineTick) : WorldMutation(Principal);

    /// <summary>Resolves a subject currently held in ESCROW — the only door back OUT of the state
    /// <see cref="OfferOwnership"/> enters. Two shapes under one kind, distinguished by <see cref="Reclaim"/>: an
    /// ACCEPT (<see langword="false"/>) transfers ownership to the escrow's own named
    /// <see cref="OwnershipEscrow.Recipient"/>, admitted only when <see cref="Principal"/> equals that recipient; a
    /// RECLAIM (<see langword="true"/>) returns ownership to the escrow's own named
    /// <see cref="OwnershipEscrow.Offerer"/>, admitted only once the tick this mutation applies at has reached
    /// <see cref="OwnershipEscrow.DeadlineTick"/> AND <see cref="Principal"/> is either that offerer or
    /// <see cref="WorldPrincipal.World"/> (the engine's own automatic sweep — see
    /// <c>Server.WorldServer.ReclaimExpiredEscrows</c> — which fires this SAME mutation once a deadline passes with
    /// no accept, so recovery needs no operator action). Rejected loudly if <see cref="Subject"/> names no declared
    /// row or that row's owner is not <see cref="OwnershipOwnerKind.Escrow"/> — in particular, a
    /// <see cref="Reclaim"/>=<see langword="false"/> attempt against a Principal- or Group-owned row is refused
    /// rather than treated as a direct transfer: THIS is the structural guard that closes the naive "flip the owner
    /// field directly" two-submission race — every transfer must pass through the escrow intermediate, so at most one
    /// of a racing accept/reclaim pair (drained in submission order at the same tick boundary) can ever find the row
    /// still in escrow; the other finds it already resolved and refuses, never double-applies.</summary>
    /// <param name="Principal">The acting identity — the escrow's recipient (accept) or offerer/<see cref="WorldPrincipal.World"/> (reclaim).</param>
    /// <param name="Subject">The subject to resolve out of escrow.</param>
    /// <param name="Reclaim"><see langword="false"/> for an accept by the named recipient; <see langword="true"/> for
    /// a reclaim to the named offerer once the deadline has passed.</param>
    [MutationKind(ordinal: 63, section: WorldSection.Groups)]
    public sealed record SettleOwnership(WorldPrincipal Principal, OwnershipSubject Subject, bool Reclaim) : WorldMutation(Principal);
}
