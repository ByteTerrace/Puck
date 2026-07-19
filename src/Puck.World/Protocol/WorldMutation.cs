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
/// convention (every kind is a nested sealed record).</remarks>
/// <param name="Principal">The acting identity the mutation is checked against.</param>
internal abstract record WorldMutation(WorldPrincipal Principal) {
    /// <summary>Upserts a locomotion kit row addressed by <see cref="WorldKit.Name"/> — replaces the matching row or
    /// appends a new one.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Kit">The whole kit row.</param>
    internal sealed record UpsertKit(WorldPrincipal Principal, WorldKit Kit) : WorldMutation(Principal);

    /// <summary>Removes the kit row named <paramref name="Name"/>. Rejected loudly if the composed document then names
    /// no seat kit or leaves an assignment table dangling (full-document revalidation).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The kit row name to remove.</param>
    internal sealed record RemoveKit(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Sets the default seat kit (by name). Rejected if the name matches no kit row.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The kit row name every seat body constructs from.</param>
    internal sealed record SetDefaultSeatKit(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Replaces the kit→entity assignment policy (the whole <see cref="WorldKitAssignment"/> row).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Assignment">The assignment policy.</param>
    internal sealed record SetKitAssignment(WorldPrincipal Principal, WorldKitAssignment Assignment) : WorldMutation(Principal);

    /// <summary>Upserts a diegetic screen addressed by <see cref="WorldScreen.Index"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Screen">The whole screen row.</param>
    internal sealed record UpsertScreen(WorldPrincipal Principal, WorldScreen Screen) : WorldMutation(Principal);

    /// <summary>Removes the screen at <paramref name="Index"/>. Rejected if no screen declares that index.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Index">The engine screen-surface index to remove.</param>
    internal sealed record RemoveScreen(WorldPrincipal Principal, int Index) : WorldMutation(Principal);

    /// <summary>Upserts a placeable camera addressed by <see cref="WorldCamera.Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Camera">The whole camera row.</param>
    internal sealed record UpsertCamera(WorldPrincipal Principal, WorldCamera Camera) : WorldMutation(Principal);

    /// <summary>Removes the camera named <paramref name="Name"/>. Rejected if a View screen still references it.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The camera name to remove.</param>
    internal sealed record RemoveCamera(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Replaces the whole static scene (ground albedos + shape rows).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Scene">The scene.</param>
    internal sealed record SetScene(WorldPrincipal Principal, WorldScene Scene) : WorldMutation(Principal);

    /// <summary>Upserts one static-scene shape row addressed by <see cref="WorldSceneRow.Id"/> — replaces the matching
    /// row or appends a new one. The editor's per-act scene grain (a whole-scene resend per act stays wrong).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Row">The whole scene row.</param>
    internal sealed record UpsertSceneRow(WorldPrincipal Principal, WorldSceneRow Row) : WorldMutation(Principal);

    /// <summary>Removes the scene row with id <paramref name="Id"/>. Rejected if no row declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The scene-row id to remove.</param>
    internal sealed record RemoveSceneRow(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Replaces the whole seat spawn-point list (order maps slots; takes effect at the next seat activation).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Spawns">The spawn points.</param>
    internal sealed record SetSpawns(WorldPrincipal Principal, IReadOnlyList<WorldSpawnPoint> Spawns) : WorldMutation(Principal);

    /// <summary>Replaces the profileless locomotion/jump tuning.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Motion">The motion tuning.</param>
    internal sealed record SetMotion(WorldPrincipal Principal, MotionTuning Motion) : WorldMutation(Principal);

    /// <summary>Replaces the wander tuning.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Wander">The wander tuning.</param>
    internal sealed record SetWander(WorldPrincipal Principal, WanderTuning Wander) : WorldMutation(Principal);

    /// <summary>Replaces the census defaults (document-only; the live census stays the <c>world.population</c> verb's
    /// session state).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Population">The census defaults.</param>
    internal sealed record SetPopulationDefaults(WorldPrincipal Principal, WorldPopulationDefaults Population) : WorldMutation(Principal);

    /// <summary>Replaces the render-lever defaults and quality-preset table (document-only; live render levers stay
    /// <c>WorldRenderSettings</c>).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Render">The render defaults.</param>
    internal sealed record SetRenderDefaults(WorldPrincipal Principal, WorldRenderDefaults Render) : WorldMutation(Principal);

    /// <summary>Upserts a data-side addon descriptor addressed by <see cref="WorldAddonRow.Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Addon">The addon row.</param>
    internal sealed record UpsertAddon(WorldPrincipal Principal, WorldAddonRow Addon) : WorldMutation(Principal);

    /// <summary>Removes the addon named <paramref name="Name"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The addon name to remove.</param>
    internal sealed record RemoveAddon(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a per-world binding overlay addressed by <see cref="WorldBindingOverlay.Id"/> — replaces the
    /// matching row or appends a new one. Rejected loudly if the composed mapping (default ⊕ every overlay) then fails to
    /// compile.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Overlay">The whole overlay row.</param>
    internal sealed record UpsertBindingOverlay(WorldPrincipal Principal, WorldBindingOverlay Overlay) : WorldMutation(Principal);

    /// <summary>Removes the binding overlay with id <paramref name="Id"/>. Rejected if no overlay declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The overlay id to remove.</param>
    internal sealed record RemoveBindingOverlay(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a creation ASSET row addressed by <see cref="WorldCreation.Id"/>. The compose boundary
    /// canonicalizes the row's document (doc + hash always come from the SAME <see cref="Puck.Authoring.CanonicalDocument{TDocument}"/>)
    /// and rejects loudly when the carried hash does not match the canonical one — a hash the pipeline did not itself
    /// compute is never accepted.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Creation">The whole creation row.</param>
    internal sealed record UpsertCreation(WorldPrincipal Principal, WorldCreation Creation) : WorldMutation(Principal);

    /// <summary>Removes the creation row with id <paramref name="Id"/>. Rejected loudly when no row declares that id
    /// OR when live placements still reference it (the conservative no-cascade ruling — remove the placements first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The creation id to remove.</param>
    internal sealed record RemoveCreation(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a placement INSTANCE row addressed by <see cref="WorldPlacement.Id"/>. Rejected loudly
    /// when it names no creation row, violates the placement policy envelope, or would exceed the probed render
    /// envelope (the capacity-honesty contract).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Placement">The whole placement row.</param>
    internal sealed record UpsertPlacement(WorldPrincipal Principal, WorldPlacement Placement) : WorldMutation(Principal);

    /// <summary>Removes the placement row with id <paramref name="Id"/>. Rejected if no row declares that id.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The placement id to remove.</param>
    internal sealed record RemovePlacement(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a placeable speaker addressed by <see cref="WorldSpeaker.Name"/> (the camera pair's audio
    /// sibling — whole-row, <c>$type fixed|anchored|bed</c>).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Speaker">The whole speaker row.</param>
    internal sealed record UpsertSpeaker(WorldPrincipal Principal, WorldSpeaker Speaker) : WorldMutation(Principal);

    /// <summary>Removes the speaker named <paramref name="Name"/>. Rejected if no row declares that name.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Name">The speaker name to remove.</param>
    internal sealed record RemoveSpeaker(WorldPrincipal Principal, string Name) : WorldMutation(Principal);

    /// <summary>Upserts a tune ASSET row addressed by <see cref="WorldTune.Id"/>. The compose boundary
    /// re-canonicalizes the embedded <c>puck.audio.v1</c> document and REJECTS a hash the pipeline did not itself
    /// compute, the same rule as <see cref="UpsertCreation"/>.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Tune">The whole tune row.</param>
    internal sealed record UpsertTune(WorldPrincipal Principal, WorldTune Tune) : WorldMutation(Principal);

    /// <summary>Removes the tune row with id <paramref name="Id"/>. Rejected loudly while speakers still reference it
    /// (the conservative no-cascade ruling — retarget or remove the speakers first).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The tune id to remove.</param>
    internal sealed record RemoveTune(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Upserts a synth-patch ASSET row addressed by <see cref="WorldPatch.Id"/> — the <c>puck.synth.v1</c>
    /// twin of <see cref="UpsertTune"/>, same canonicalize + hash-pin boundary.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Patch">The whole patch row.</param>
    internal sealed record UpsertPatch(WorldPrincipal Principal, WorldPatch Patch) : WorldMutation(Principal);

    /// <summary>Removes the patch row with id <paramref name="Id"/>. Rejected loudly while speakers or emission
    /// facets still reference it (no cascade — the dependents are named).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Id">The patch id to remove.</param>
    internal sealed record RemovePatch(WorldPrincipal Principal, string Id) : WorldMutation(Principal);

    /// <summary>Replaces the audio host-section defaults (the whole <see cref="WorldAudioDefaults"/> row). Applies
    /// LIVE: the emitter-derivation coalescing, the listener policy, and the cue table read the delivered row.
    /// <c>MasterGain</c> follows the lever-precedence rule: it flows live only until the <c>world.volume</c> session
    /// lever engages — thereafter the lever owns "now" and the field owns the next boot (<c>world.save</c> folds the
    /// lever back into it).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Audio">The audio defaults row.</param>
    internal sealed record SetAudioDefaults(WorldPrincipal Principal, WorldAudioDefaults Audio) : WorldMutation(Principal);

    /// <summary>Replaces the whole editor/authoring policy row. A single whole-row mutation carries both
    /// consumption classes the row holds (see <see cref="WorldAuthoringDefaults"/>'s remarks): the boot-consumed
    /// headroom/repeat-cap fields apply at the NEXT boot (the frozen render-envelope probe cannot retroactively grow),
    /// while the live-consumed candidate/layout/preview fields apply at the very next tick — the server's accept echo
    /// narrates the split honestly rather than picking one class for the whole row.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Authoring">The whole authoring policy row.</param>
    internal sealed record SetAuthoringDefaults(WorldPrincipal Principal, WorldAuthoringDefaults Authoring) : WorldMutation(Principal);

    /// <summary>Replaces the whole contact-solver tuning (the <see cref="WorldCollision"/> section). Applies LIVE: the
    /// population rebuilds the collider set and hands it to every body on the next tick.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Collision">The contact-solver tuning.</param>
    internal sealed record SetCollision(WorldPrincipal Principal, WorldCollision Collision) : WorldMutation(Principal);

    /// <summary>Replaces the whole host-section defaults row (window/backend/present/pacing/timing/genlock). DOCUMENT-
    /// DEFAULTS class: the boot-only fields take effect at the NEXT boot (a running window cannot resize its backend or
    /// surface), and the two live-lever fields (<c>TargetHertz</c> via <c>world.target</c>, <c>Timing</c> via
    /// <c>world.timing</c>) set the value the next boot wakes on — <c>world.save</c> folds the running levers back into
    /// them. The row is validated immediately, so a bad value is rejected loudly regardless of when it applies.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Host">The whole host defaults row.</param>
    internal sealed record SetHostDefaults(WorldPrincipal Principal, WorldHostDefaults Host) : WorldMutation(Principal);
}
