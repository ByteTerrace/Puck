using System.Numerics;
using Puck.Assets;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo.World;

/// <summary>
/// The world sculptor's verb LOGIC, kept in a static class so <see cref="WorldCommandModule"/> (the
/// <c>ICommandModule</c> the console registers) stays a thin registration shim — the blessed CA1502/CA1506 escape
/// this codebase already uses for <c>Puck.Demo.Forge.ForgeCommands</c>. Every verb takes the live
/// <see cref="WorldScene"/> plus a resolved <see cref="ContentAddressedStore"/> explicitly, so this class carries no
/// host/render-node coupling at all.
/// </summary>
internal static class WorldCommands {
    /// <summary>The store root every <c>world.*</c> verb resolves against — cwd-relative, sibling of
    /// <c>creations/</c>/<c>tunes/</c>/<c>worlds/</c> (see <see cref="Puck.Demo.Creator.CreationStore"/>/
    /// <see cref="Puck.Demo.Forge.AudioDocumentStore"/>).</summary>
    public static ContentAddressedStore OpenStore() => new(root: "store");

    /// <summary>Lists the saved worlds under <c>./worlds/</c>.</summary>
    public static string List() {
        var names = WorldDocumentStore.List();

        return ((names.Count > 0)
            ? $"[world.list: {string.Join(separator: ", ", values: names)}]"
            : "[world.list: none saved yet — world.save <name> writes one]");
    }

    /// <summary>Loads a saved world by handle or path, resolving every placement's creation against the store and
    /// refusing (loudly, but gracefully — a friendly narration, not a throw) when any placement's source is
    /// missing, per the bit-for-bit doctrine (no partially-resolved world).</summary>
    /// <param name="scene">The live scene to load into.</param>
    /// <param name="store">The content-addressed store to resolve against.</param>
    /// <param name="nameOrPath">The save handle or file path.</param>
    public static string Load(WorldScene scene, ContentAddressedStore store, string nameOrPath) {
        if (WorldDocumentStore.Load(nameOrPath: nameOrPath) is not { } document) {
            return $"[world.load: nothing readable at '{nameOrPath}']";
        }

        var resolvedSources = WorldDocumentStore.TryResolvePlacementSources(document: document, missing: out var missing, store: store);

        if (missing.Count > 0) {
            return $"[world.load: refused — {missing.Count} placement(s) reference creations missing from the store (ids: {string.Join(separator: ", ", values: missing)}); the world is not partially loadable]";
        }

        _ = resolvedSources; // Resolution success is already proven by an empty `missing`; LoadDocument re-resolves
                              // internally (the cache is populated fresh either way).

        var loaded = scene.LoadDocument(document: document, store: store);

        // The loaded bytes are the committed copy — the host's hot-reload consumes it (collision + walk grid +
        // movement lock swap on the next tick boundary), exactly as a fresh save would.
        scene.MarkCommitted(document: document);

        return $"[world.load: {loaded} placement(s) from '{document.Name}' — applying on the next tick]";
    }

    /// <summary>Saves the live scene, narrating the petname the content hash derives. Saving is JUST saving — the
    /// theme song is no longer a save side effect; <see cref="Theme"/> derives it on demand from the saved hash (the
    /// save narration points at the verb).</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="store">The content-addressed store to also land the canonical bytes in.</param>
    public static string Save(WorldScene scene, ContentAddressedStore store) {
        var (path, hash) = scene.Save(store: store);

        if (hash is not { } fullHash) {
            return $"[world.save: saved to {path} (no CAS store)]";
        }

        var hex = fullHash["sha256/".Length..];

        return $"[world: saved as \"{ContentPetname.From(hashHex: fullHash)}\" (sha256/{hex[..12]}…) — world.theme composes its song]";
    }

    /// <summary>Composes the world's theme song from the CURRENT saved hash and lands it beside the tunes — the
    /// un-welded successor to the save side effect: every world has a theme, but it is derived ON DEMAND, not smuggled
    /// into Save. Friendly error when the world was never saved (a theme needs a hash to derive from).</summary>
    /// <param name="scene">The live scene (its name resolves the saved hash).</param>
    /// <param name="store">The content-addressed store the saved world's ref lives in.</param>
    public static string Theme(WorldScene scene, ContentAddressedStore store) {
        if (!store.TryResolveRef(category: "worlds", name: scene.Name, hash: out var fullHash)) {
            return "[world.theme: nothing saved yet — world.save first, then world.theme composes its song from the saved hash]";
        }

        var themeName = $"{scene.Name}-theme";

        // The hash composes a small pentatonic tune — change the world (and re-save), the hash changes, the song
        // changes. Playable via --forge-tune-from or the tracker.
        Forge.AudioDocumentStore.SaveNamed(document: WorldThemeComposer.Compose(hashHex: fullHash, name: themeName), name: themeName, store: store);

        return $"[world.theme: composed from sha256/{fullHash["sha256/".Length..][..12]}… — landed at tunes/{themeName}.audio.json]";
    }

    /// <summary>Re-serializes the live model and byte-compares it against the last saved copy under the scene's
    /// current name.</summary>
    /// <param name="scene">The live scene.</param>
    public static string Verify(WorldScene scene) {
        if (WorldDocumentStore.Load(nameOrPath: scene.Name) is not { } stored) {
            return "[world.verify: no saved copy to compare against — world.save first]";
        }

        // The stored copy carries the BAKED walk grid; run the live model through the same deliberate-save
        // transformation (the bake is deterministic — bake-twice is byte-identical) so the compare is honest.
        var liveDocument = scene.ToDocument();

        if (scene.PrepareForSave is { } prepare) {
            liveDocument = prepare(arg: liveDocument);
        }

        var live = WorldDocumentStore.ToJson(document: liveDocument);
        var storedJson = WorldDocumentStore.ToJson(document: stored);

        return (string.Equals(a: live, b: storedJson, comparisonType: StringComparison.Ordinal)
            ? "[world.verify: MATCH]"
            : "[world.verify: MISMATCH — the live model differs from the last save; world.save to reconcile]");
    }

    /// <summary>Arms the ghost stamp with a named/hashed creation (dry-measuring it against the per-stamp shape
    /// budget first) and, when repeat counts/spacing are given, places it immediately with that repeat.</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="store">The content-addressed store.</param>
    /// <param name="args">
    /// <c>&lt;creation&gt; [countX countZ spacingX spacingZ]</c> — the creation's ref name (resolved against the
    /// <c>creations</c> ref category) or a raw <c>sha256/&lt;hex64&gt;</c> hash.
    /// </param>
    public static string Place(WorldScene scene, ContentAddressedStore store, string[] args) {
        if (args.Length == 0) {
            return "[world.place: usage — world.place <creation> [countX countZ spacingX spacingZ]]";
        }

        var refName = args[0];

        if (!TryResolveCreationHash(store: store, nameOrHash: refName, hash: out var hash)) {
            return $"[world.place: '{refName}' did not resolve — save it as a creation first (creator.save), or check world store 'creations' refs]";
        }

        if (!scene.TryArmGhost(hash: hash, refName: refName, refusal: out var refusal, store: store)) {
            return $"[world.place: refused — {refusal}]";
        }

        var placedId = scene.Place();

        if (placedId is null) {
            return $"[world.place: armed '{refName}' — the placement budget is full ({WorldScene.MaxPlacements}); nothing placed]";
        }

        if ((args.Length >= 5) &&
            TryParseInt(text: args[1], value: out var countX) &&
            TryParseInt(text: args[2], value: out var countZ) &&
            TryParseFloat(text: args[3], value: out var spacingX) &&
            TryParseFloat(text: args[4], value: out var spacingZ)) {
            var wasSelected = scene.SelectionIndex;

            SelectById(scene: scene, id: placedId.Value);

            var applied = scene.SetSelectedRepeat(refusal: out var repeatRefusal, repeat: new WorldPlacementRepeat(CountX: Math.Max(val1: 1, val2: countX), CountZ: Math.Max(val1: 1, val2: countZ), SpacingX: spacingX, SpacingZ: spacingZ));

            if (wasSelected < 0) {
                scene.Deselect();
            }

            return (applied
                ? $"[world.place: placed #{placedId} '{refName}' with a {countX}×{countZ} repeat]"
                : $"[world.place: placed #{placedId} '{refName}' — repeat refused: {repeatRefusal}]");
        }

        return $"[world.place: placed #{placedId} '{refName}']";
    }

    /// <summary>Selects a placement by id.</summary>
    public static string Select(WorldScene scene, string[] args) {
        if ((args.Length == 0) || !TryParseInt(text: args[0], value: out var id)) {
            return "[world.select: give a placement id — world.list has none of these; check the last world.place/world.load narration]";
        }

        return (SelectById(scene: scene, id: id)
            ? $"[world.select: #{id} '{scene.SelectedPlacement?.RefName}']"
            : $"[world.select: no placement with id {id}]");
    }

    /// <summary>Deletes the selected placement.</summary>
    public static string Delete(WorldScene scene) {
        return (scene.DeleteSelected()
            ? $"[world.del: deleted — {scene.Placements.Count} placement(s) remain]"
            : "[world.del: nothing selected — world.select <id> first]");
    }

    /// <summary>Moves the selected placement (or the ghost) to an exact position — one meaning only; the movement
    /// direction lock lives on its own verb (<see cref="Movement"/>).</summary>
    public static string Move(WorldScene scene, string[] args) {
        if (!TryParseFloats(args: args, count: 3, start: 0, values: out var xyz)) {
            return "[world.move: usage — world.move <x> <y> <z>]";
        }

        SetTargetPositionExact(scene: scene, position: new Vector3(xyz[0], xyz[1], xyz[2]));

        return $"[world.move: ({xyz[0]:F2}, {xyz[1]:F2}, {xyz[2]:F2})]";
    }

    /// <summary>The daylight dial: <c>world.dusk</c> eases to evening (0.3), <c>world.dusk &lt;0.15..1&gt;</c> sets it
    /// exactly, <c>world.dusk day</c> restores noon. Presentation only — authored lamps glow once the room dims.</summary>
    public static string Dusk(WorldScene scene, string[] args) {
        var value = ((args.Length == 0)
            ? 0.3f
            : (string.Equals(a: args[0], b: "day", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? 1f
                : (TryParseFloat(text: args[0], value: out var parsed) ? parsed : float.NaN)));

        if (float.IsNaN(f: value)) {
            return "[world.dusk: usage — world.dusk (evening), world.dusk <0.15..1>, or world.dusk day]";
        }

        scene.SetDaylight(value: value);

        return $"[world: daylight = {scene.Daylight:F2}{((scene.Daylight < 0.5f) ? " — the lamps have the floor" : "")}]";
    }

    /// <summary>Sets the world's MOVEMENT DIRECTION LOCK (sim config, applied on save/load — the same hook as the
    /// walk-grid bake): <c>free</c>, <c>four</c>, <c>eight</c>, or <c>hex</c>.</summary>
    public static string Movement(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (args.Length != 1) {
            return "[world.movement: usage — world.movement <free|four|eight|hex>]";
        }

        var baseline = scene.CaptureSnapshot();

        if (!scene.SetMovementLock(mode: args[0])) {
            return $"[world.movement: '{args[0]}' is not a movement lock — free, four, eight, or hex]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world: movement lock = {scene.MovementLock} — applies on save/load]";
    }

    /// <summary>Sets the selected placement's (or the ghost's) yaw directly, in degrees.</summary>
    public static string Rotate(WorldScene scene, string[] args) {
        if ((args.Length == 0) || !TryParseFloat(text: args[0], value: out var yawDegrees)) {
            return "[world.rotate: usage — world.rotate <yaw°>]";
        }

        SetTargetYawExact(scene: scene, yawDegrees: yawDegrees);

        return $"[world.rotate: {yawDegrees:F0}°]";
    }

    /// <summary>Sets the selected placement's (or the ghost's) uniform scale directly.</summary>
    public static string Scale(WorldScene scene, string[] args) {
        if ((args.Length == 0) || !TryParseFloat(text: args[0], value: out var scale)) {
            return "[world.scale: usage — world.scale <factor>]";
        }

        var clamped = SetTargetScaleExact(scene: scene, scale: scale);

        return $"[world.scale: {clamped:F2}]";
    }

    /// <summary>Sets (or clears) the selected placement's repeat block.</summary>
    /// <param name="args"><c>&lt;countX&gt; &lt;countZ&gt; [spacingX] [spacingZ]</c>, or <c>clear</c>.</param>
    public static string Repeat(WorldScene scene, string[] args) {
        if (scene.TargetIsGhost) {
            return "[world.repeat: nothing selected — world.select <id> first (a repeat needs a placed stamp)]";
        }

        if ((args.Length == 1) && string.Equals(a: args[0], b: "clear", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            _ = scene.SetSelectedRepeat(refusal: out _, repeat: null);

            return "[world.repeat: cleared — a single copy]";
        }

        if ((args.Length < 2) || !TryParseInt(text: args[0], value: out var countX) || !TryParseInt(text: args[1], value: out var countZ)) {
            return "[world.repeat: usage — world.repeat <countX> <countZ> [spacingX] [spacingZ], or world.repeat clear]";
        }

        var spacingX = (((args.Length > 2) && TryParseFloat(text: args[2], value: out var sx)) ? sx : 1.5f);
        var spacingZ = (((args.Length > 3) && TryParseFloat(text: args[3], value: out var sz)) ? sz : 1.5f);

        return (scene.SetSelectedRepeat(refusal: out var refusal, repeat: new WorldPlacementRepeat(CountX: Math.Max(val1: 1, val2: countX), CountZ: Math.Max(val1: 1, val2: countZ), SpacingX: spacingX, SpacingZ: spacingZ))
            ? $"[world.repeat: {countX}×{countZ} @ ({spacingX:F2}, {spacingZ:F2})]"
            : $"[world.repeat: refused — {refusal}]");
    }

    /// <summary>Grows/shrinks the authored lot bounds.</summary>
    /// <param name="args"><c>&lt;±x&gt; &lt;±z&gt;</c>.</param>
    public static string Bounds(WorldScene scene, string[] args) {
        if (!TryParseFloats(args: args, count: 2, start: 0, values: out var xz)) {
            return "[world.bounds: usage — world.bounds <±x> <±z>]";
        }

        var bounds = scene.GrowBounds(deltaX: xz[0], deltaZ: xz[1]);

        return $"[world.bounds: ({bounds.MinX:F1}, {bounds.MinZ:F1}) to ({bounds.MaxX:F1}, {bounds.MaxZ:F1})]";
    }

    /// <summary>Rebinds the selected placement to a different creation.</summary>
    /// <param name="args">The replacement creation's ref name or hash.</param>
    public static string Rebind(WorldScene scene, ContentAddressedStore store, string[] args) {
        if (args.Length == 0) {
            return "[world.rebind: usage — world.rebind <creation>]";
        }

        var refName = args[0];

        if (!TryResolveCreationHash(store: store, nameOrHash: refName, hash: out var hash)) {
            return $"[world.rebind: '{refName}' did not resolve]";
        }

        return (scene.RebindSelected(hash: hash, refName: refName, refusal: out var refusal, store: store)
            ? $"[world.rebind: now '{refName}']"
            : $"[world.rebind: refused — {refusal}]");
    }

    /// <summary>Opens a placement by id for a console-narrated edit session (a lightweight alias for
    /// <see cref="Select"/> — the seam a future rich editor UI hooks).</summary>
    public static string Edit(WorldScene scene, string[] args) =>
        Select(scene: scene, args: args);

    /// <summary>Sets or clears the selected placement's mirror fold axis (console-assist <c>SymmetryX</c>/<c>Z</c> —
    /// the frozen <c>PlacementDocument.Mirror</c> wire format; the renderer folds the chain inside the placement's
    /// instance, and the probe already reserves the op). Structural edit; snapshot-covered when a history is
    /// wired.</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="history">The shared undo ring (null = no undo checkpoint, the edit still applies).</param>
    /// <param name="args"><c>&lt;x|z|off&gt;</c>.</param>
    public static string Mirror(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (scene.TargetIsGhost) {
            return "[world.mirror: nothing selected — world.select <id> first]";
        }

        if (args.Length == 0) {
            return $"[world.mirror: {scene.SelectedPlacement?.Mirror ?? "off"} — give x, z, or off to change it]";
        }

        var axis = (args[0].ToLowerInvariant() switch {
            "x" => "x",
            "z" => "z",
            "off" or "none" => null,
            _ => "invalid",
        });

        if (string.Equals(a: axis, b: "invalid", comparisonType: StringComparison.Ordinal)) {
            return $"[world.mirror: '{args[0]}' is not a mirror axis — x, z, or off]";
        }

        var baseline = scene.CaptureSnapshot();

        _ = scene.SetSelectedMirror(axis: axis);
        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world.mirror: #{scene.SelectedPlacement?.Id} {axis ?? "off"}]";
    }

    /// <summary>Sets or clears the selected placement's wallpaper pattern (console-assist <c>WallpaperFold</c> —
    /// the frozen <c>PlacementDocument.Pattern</c> wire format; the renderer folds the chain inside the placement's
    /// instance, and the probe already reserves the op). Structural edit; snapshot-covered when a history is
    /// wired.</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="history">The shared undo ring (null = no undo checkpoint, the edit still applies).</param>
    /// <param name="args"><c>&lt;group&gt; &lt;cellW&gt; &lt;cellH&gt; [limitX limitZ] [stride]</c>, or <c>off</c>.</param>
    public static string Pattern(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (scene.TargetIsGhost) {
            return "[world.pattern: nothing selected — world.select <id> first]";
        }

        if ((args.Length == 1) && (string.Equals(a: args[0], b: "off", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: args[0], b: "none", comparisonType: StringComparison.OrdinalIgnoreCase))) {
            var clearBaseline = scene.CaptureSnapshot();

            _ = scene.SetSelectedPattern(pattern: null);
            history?.Push(snapshot: clearBaseline);
            history?.Push(snapshot: scene.CaptureSnapshot());

            return $"[world.pattern: #{scene.SelectedPlacement?.Id} cleared]";
        }

        if ((args.Length < 3) || !TryParseFloat(text: args[1], value: out var cellW) || !TryParseFloat(text: args[2], value: out var cellH)) {
            return "[world.pattern: usage — world.pattern <group> <cellW> <cellH> [limitX limitZ] [stride], or world.pattern off]";
        }

        if (!Enum.TryParse<Puck.SdfVm.SdfWallpaperGroup>(ignoreCase: true, result: out var group, value: args[0])) {
            return $"[world.pattern: '{args[0]}' is not a wallpaper group — one of {string.Join(separator: ", ", values: Enum.GetNames<Puck.SdfVm.SdfWallpaperGroup>())}]";
        }

        if ((cellW <= 0f) || (cellH <= 0f) || !float.IsFinite(f: cellW) || !float.IsFinite(f: cellH)) {
            return "[world.pattern: cell sizes must be positive]";
        }

        float? limitX = null;
        float? limitZ = null;

        if ((args.Length >= 5) && TryParseFloat(text: args[3], value: out var lx) && TryParseFloat(text: args[4], value: out var lz)) {
            limitX = MathF.Max(lx, 1f);
            limitZ = MathF.Max(lz, 1f);
        }

        int? stride = null;

        if ((args.Length >= 6) && TryParseInt(text: args[5], value: out var parsedStride)) {
            stride = Math.Max(val1: parsedStride, val2: 0);
        } else if ((args.Length == 4) && TryParseInt(text: args[3], value: out var soloStride)) {
            // The [stride]-without-limits form: world.pattern <group> <cellW> <cellH> <stride>.
            stride = Math.Max(val1: soloStride, val2: 0);
        }

        var baseline = scene.CaptureSnapshot();

        _ = scene.SetSelectedPattern(pattern: new WorldPlacementPattern(
            CellHeight: cellH,
            CellWidth: cellW,
            Group: group.ToString(),
            LimitX: limitX,
            LimitZ: limitZ,
            MaterialStride: stride
        ));
        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world.pattern: #{scene.SelectedPlacement?.Id} {group} @ ({cellW:F2}×{cellH:F2}){((limitX is { } appliedX) ? $" limit ({appliedX:F0}×{limitZ:F0})" : "")}{((stride is { } appliedStride) ? $" stride {appliedStride}" : "")}]";
    }

    /// <summary>Sets the walk grid tessellation the next save bakes (<c>square</c> or <c>hex</c>) — sim config,
    /// carried on the document's <c>WalkGrid.Kind</c>; the host's save hook bakes the actual grid.</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="history">The shared undo ring (null = no undo checkpoint, the edit still applies).</param>
    /// <param name="args"><c>&lt;square|hex&gt;</c>.</param>
    public static string Grid(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (args.Length == 0) {
            return $"[world.grid: {scene.WalkGridKind} — give square or hex to change it]";
        }

        var baseline = scene.CaptureSnapshot();

        if (!scene.SetWalkGridKind(kind: args[0])) {
            return $"[world.grid: '{args[0]}' is not a tessellation — square or hex]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world: walk grid = {scene.WalkGridKind} — takes effect on the next save]";
    }

    /// <summary>The placed-camera verbs — <c>world.camera add [x y z] [yaw°] [pitch°]</c>,
    /// <c>world.camera del &lt;id&gt;</c>, <c>world.camera list</c>. A camera is a posed EYE the developer drops
    /// anywhere; its feed shows on any screen that wires it (<c>world.wire</c>). Add with no coordinates drops the eye
    /// at the lot center looking level. Snapshot-covered (undo-able).</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="history">The edit history (undo/redo).</param>
    /// <param name="args">The subcommand and its arguments.</param>
    public static string Camera(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (args.Length == 0) {
            return "[world.camera: usage — world.camera add [x y z] [yaw°] [pitch°] | world.camera del <id> | world.camera list]";
        }

        return (args[0].ToLowerInvariant() switch {
            "add" => CameraAdd(scene: scene, history: history, args: args),
            "del" or "delete" or "rm" => CameraDelete(scene: scene, history: history, args: args),
            "list" or "ls" => CameraList(scene: scene),
            _ => $"[world.camera: '{args[0]}' is not add, del, or list]",
        });
    }

    /// <summary>The screen wiring verbs — <c>world.wire &lt;source&gt; &lt;screen&gt;</c>,
    /// <c>world.wire list</c>, <c>world.wire clear &lt;screen&gt;</c>. A source is <c>brick:N</c> (a booted cabinet),
    /// <c>feed:N</c> (a camera feed), <c>named:NAME</c> (a host feed like the emote face), or <c>none</c> (clear).
    /// Wiring is pure data, never a heuristic. Snapshot-covered (undo-able).</summary>
    /// <param name="scene">The live scene.</param>
    /// <param name="history">The edit history (undo/redo).</param>
    /// <param name="args">The source (or list/clear) and screen index.</param>
    public static string Wire(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if (args.Length == 0) {
            return "[world.wire: usage — world.wire <brick:N|feed:N|named:NAME|none> <screen> | world.wire list | world.wire clear <screen>]";
        }

        var head = args[0].ToLowerInvariant();

        if (head is "list" or "ls") {
            return WireList(scene: scene);
        }

        if (head == "clear") {
            return WireClear(scene: scene, history: history, args: args);
        }

        if ((args.Length < 2) || !TryParseInt(text: args[1], value: out var screenIndex)) {
            return "[world.wire: usage — world.wire <brick:N|feed:N|named:NAME|none> <screen>]";
        }

        if (!TryParseWireSource(text: args[0], source: out var source, out var parseError)) {
            return $"[world.wire: {parseError}]";
        }

        var baseline = scene.CaptureSnapshot();

        if (!scene.WireScreen(screenIndex: screenIndex, source: source)) {
            return $"[world.wire: screen {screenIndex} is out of range (0..{Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces - 1})]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return ((source.Kind == ScreenWireKind.None)
            ? $"[world.wire: screen {screenIndex} cleared — falls back to its default]"
            : $"[world.wire: screen {screenIndex} ← {source}]");
    }

    private static string CameraAdd(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        // world.camera add                    -> lot center, level
        // world.camera add x y z              -> that world position, level
        // world.camera add x y z yaw          -> + heading
        // world.camera add x y z yaw pitch    -> + tilt
        var rest = args[1..];
        var position = scene.Bounds.Center;
        var yawDegrees = 0f;
        var pitchDegrees = 0f;

        if (rest.Length >= 3) {
            if (!TryParseFloats(args: rest, count: 3, start: 0, out var xyz)) {
                return "[world.camera add: x y z must be numbers]";
            }

            position = new Vector3(xyz[0], xyz[1], xyz[2]);
        }

        if ((rest.Length >= 4) && !TryParseFloat(text: rest[3], value: out yawDegrees)) {
            return "[world.camera add: yaw° must be a number]";
        }

        if ((rest.Length >= 5) && !TryParseFloat(text: rest[4], value: out pitchDegrees)) {
            return "[world.camera add: pitch° must be a number]";
        }

        var baseline = scene.CaptureSnapshot();
        var id = scene.AddCamera(
            pitch: (pitchDegrees * (MathF.PI / 180f)),
            position: position,
            yaw: (yawDegrees * (MathF.PI / 180f))
        );

        if (id is not { } cameraId) {
            return $"[world.camera add: the camera budget is full ({WorldScene.MaxCameras})]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world.camera: eye #{cameraId} at ({position.X:F1}, {position.Y:F1}, {position.Z:F1}) yaw {yawDegrees:F0}° — wire its feed with world.wire feed:{cameraId} <screen>]";
    }

    private static string CameraDelete(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if ((args.Length < 2) || !TryParseInt(text: args[1], value: out var id)) {
            return "[world.camera del: usage — world.camera del <id>]";
        }

        var baseline = scene.CaptureSnapshot();

        if (!scene.DeleteCamera(id: id)) {
            return $"[world.camera del: no camera #{id}]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world.camera: eye #{id} deleted]";
    }

    private static string CameraList(WorldScene scene) {
        if (scene.Cameras.Count == 0) {
            return "[world.camera list: none placed — world.camera add drops one]";
        }

        var lines = new List<string>(capacity: scene.Cameras.Count);

        foreach (var eye in scene.Cameras) {
            var anchor = ((eye.Anchor == CameraAnchorKind.Placement) ? $" @placement:{eye.AnchorId}" : "");

            lines.Add(item: $"#{eye.Id} ({eye.Position.X:F1}, {eye.Position.Y:F1}, {eye.Position.Z:F1}) yaw {eye.Yaw * (180f / MathF.PI):F0}°{anchor}");
        }

        return $"[world.camera list: {string.Join(separator: ", ", values: lines)}]";
    }

    private static string WireList(WorldScene scene) {
        if (scene.Wiring.Count == 0) {
            return "[world.wire list: nothing wired — every screen shows its default]";
        }

        var lines = new List<string>(capacity: scene.Wiring.Count);

        foreach (var screenIndex in scene.Wiring.Keys.Order()) {
            lines.Add(item: $"screen {screenIndex} ← {scene.Wiring[screenIndex]}");
        }

        return $"[world.wire list: {string.Join(separator: ", ", values: lines)}]";
    }

    private static string WireClear(WorldScene scene, EditHistory<WorldScene.Snapshot>? history, string[] args) {
        if ((args.Length < 2) || !TryParseInt(text: args[1], value: out var screenIndex)) {
            return "[world.wire clear: usage — world.wire clear <screen>]";
        }

        var baseline = scene.CaptureSnapshot();

        if (!scene.ClearWire(screenIndex: screenIndex)) {
            return $"[world.wire clear: screen {screenIndex} was not wired]";
        }

        history?.Push(snapshot: baseline);
        history?.Push(snapshot: scene.CaptureSnapshot());

        return $"[world.wire: screen {screenIndex} cleared — falls back to its default]";
    }

    // Parses a wiring source token: brick:N / feed:N / named:NAME / none. Friendly error otherwise.
    /// <summary>Parses a wiring-grammar source token (<c>brick:N</c>, <c>feed:N</c>, <c>named:NAME</c>, or
    /// <c>none</c>) — the single source of truth the <c>world.wire</c> verb and the headless capture aid both use so
    /// the grammar can never drift between them.</summary>
    /// <param name="text">The source token.</param>
    /// <param name="source">The parsed source (<see cref="ScreenWireSource.None"/> on failure).</param>
    /// <param name="error">The parse error, or empty on success.</param>
    /// <returns>Whether the token parsed.</returns>
    public static bool TryParseWireSource(string text, out ScreenWireSource source, out string error) {
        source = ScreenWireSource.None;
        error = "";

        if (string.Equals(a: text, b: "none", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var colon = text.IndexOf(value: ':');

        if (colon < 0) {
            error = $"'{text}' is not a source — use brick:N, feed:N, named:NAME, or none";

            return false;
        }

        var kind = text[..colon].ToLowerInvariant();
        var rest = text[(colon + 1)..];

        switch (kind) {
            case "brick": {
                if (!TryParseInt(text: rest, value: out var brickIndex) || (brickIndex < 0)) {
                    error = $"'{rest}' is not a console index";

                    return false;
                }

                source = ScreenWireSource.Brick(consoleIndex: brickIndex);

                return true;
            }
            case "feed": {
                if (!TryParseInt(text: rest, value: out var feedIndex) || (feedIndex < 0)) {
                    error = $"'{rest}' is not a feed index";

                    return false;
                }

                source = ScreenWireSource.Feed(feedIndex: feedIndex);

                return true;
            }
            case "named": {
                if (rest.Length == 0) {
                    error = "named: needs a feed name";

                    return false;
                }

                source = ScreenWireSource.Named(name: rest);

                return true;
            }
            default: {
                error = $"'{kind}' is not a source kind — brick, feed, named, or none";

                return false;
            }
        }
    }

    private static bool SelectById(WorldScene scene, int id) {
        var placements = scene.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            if (placements[index].Id != id) {
                continue;
            }

            // CycleSelection only moves relative to the CURRENT index — walk to the exact index the direct way.
            while (scene.SelectionIndex != index) {
                scene.CycleSelection(direction: ((scene.SelectionIndex < index) ? 1 : -1));

                if (scene.TargetIsGhost && (index > 0)) {
                    // Wrapped past "none" without landing — walk forward from the start instead of looping forever.
                    scene.CycleSelection(direction: 1);
                }

                if (scene.SelectionIndex == index) {
                    break;
                }

                if ((scene.SelectionIndex < 0) && (scene.Placements.Count == 0)) {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static void SetTargetPositionExact(WorldScene scene, Vector3 position) {
        var delta = (position - scene.TargetPosition);

        scene.Move(deltaSeconds: 1f, planar: new Vector2(delta.X, delta.Z));
    }

    private static void SetTargetYawExact(WorldScene scene, float yawDegrees) {
        var current = scene.TargetYawDegrees;
        var delta = (yawDegrees - current);

        // Rotate integrates rate*deltaSeconds*rotateSpeed(90°/s); solving for a one-shot exact set uses rate=delta,
        // deltaSeconds=1/90 so the applied degrees equal the requested delta exactly (mirrors Move's approach above).
        scene.Rotate(deltaSeconds: (1f / 90f), rate: delta);
    }

    private static float SetTargetScaleExact(WorldScene scene, float scale) {
        var clamped = Math.Clamp(value: scale, max: WorldScene.MaxScale, min: WorldScene.MinScale);
        var current = scene.TargetScale;

        if (current <= 0f) {
            return current;
        }

        // ScaleTarget integrates exp(rate*1.2*deltaSeconds); solving rate for an exact target ratio in one step.
        var ratio = (clamped / current);
        var rate = (MathF.Log(ratio) / 1.2f);

        scene.ScaleTarget(deltaSeconds: 1f, rate: rate);

        return scene.TargetScale;
    }

    private static bool TryResolveCreationHash(ContentAddressedStore store, string nameOrHash, out string hash) {
        if (nameOrHash.StartsWith(value: "sha256/", comparisonType: StringComparison.Ordinal) && store.Contains(hash: nameOrHash)) {
            hash = nameOrHash;

            return true;
        }

        return store.TryResolveRef(category: "creations", hash: out hash, name: nameOrHash);
    }

}
