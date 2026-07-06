using System.Numerics;
using Puck.Input.Devices;

namespace Puck.Demo.World;

/// <summary>
/// The world sculptor's pad state machine — CHORD-FIRST (the binding-bar model), not the creator's page-cycle
/// model: LT/RT are MODIFIERS whose ORDER-SENSITIVE held combination selects one of five pages, exactly like
/// <c>Puck.Demo.BindingProfileDocuments</c>'s chord vocabulary (bare, [LT], [RT], [LT,RT], [RT,LT] — pressing LT
/// then RT selects a DIFFERENT page than RT then LT). Because the triggers are consumed as modifiers here, every
/// page's analog verbs rehome onto the left stick, right stick, and d-pad instead — each page's stick/d-pad meaning
/// is documented below and MUST be read before rebinding anything.
///
/// <para><b>The chord/page map</b> (12 physical slots per page, in <c>BindingProfileDocuments.PageButtons</c> order —
/// DpadUp/Right/Down/Left, LeftShoulder, LeftStickClick, ButtonNorth/West/South/East, RightShoulder,
/// RightStickClick):</para>
///
/// <list type="table">
/// <listheader><term>Page (chord)</term><description>Slot bindings + stick/d-pad semantics</description></listheader>
/// <item><term>SCULPT (bare)</term><description>
/// Left stick = move ghost planar; right stick X = spin ghost yaw; d-pad up/down = scale ghost. South = place;
/// East = cancel ghost (clears the armed creation); West = cycle a placed creation by ref name (console-loaded
/// list); North = undo; LeftShoulder = redo; RightShoulder = toggle group-scope reserved (unused here — kept free);
/// LeftStickClick = save; RightStickClick = enter/exit sculpt (host-level toggle, consumed by the render node).
/// </description></item>
/// <item><term>SELECT ([LT])</term><description>
/// Left stick = move SELECTED placement planar; right stick X = rotate it; d-pad up/down = scale it — all three
/// ride the DRAG dynamic slot while held (<see cref="WorldScene.BeginDrag"/>/<see cref="WorldScene.EndDrag"/> bracket
/// the drag). South = cycle selection forward; North = cycle selection backward; East = delete selected;
/// West = deselect; LeftShoulder = rebind selected to the ghost's armed creation; RightShoulder = toggle the
/// selected placement's role between plain decoration and the console-set cabinet role (informational only here).
/// </description></item>
/// <item><term>TERRAIN/LIGHTS ([RT])</term><description>
/// Left stick = resize the pending terrain patch's X/Z half-extents; right stick X = cycle the world palette
/// material; d-pad up/down = adjust pending light intensity. South = add a terrain slab at the target position;
/// East = add a plaza patch; West = add a light; North = remove the nearest terrain patch or light to the target
/// (terrain first, then lights).
/// </description></item>
/// <item><term>WALK OVERRIDES ([LT,RT])</term><description>
/// Left stick = resize the pending override rectangle's half-extents; right stick / d-pad unused (reserved).
/// South = paint a BLOCKER rectangle at the target; East = paint a WALKABLE rectangle; North = remove the nearest
/// override to the target.
/// </description></item>
/// <item><term>BOUNDS/REPEAT/SAVE ([RT,LT])</term><description>
/// Left stick = grow/shrink the lot bounds (X/Z); right stick X = adjust the selected placement's repeat count on
/// X; d-pad up/down = adjust the selected placement's repeat count on Z. South = save (via the CAS, narrating the
/// petname); East = verify (byte-compare the live model against the stored object); West = clear the selected
/// placement's repeat (back to a single copy); North = cycle the selected placement's mirror axis (off → x → z →
/// off; <c>world.pattern</c> stays console-only — a wallpaper group needs typed parameters).
/// </description></item>
/// </list>
///
/// The player IS the cursor: this controller consumes only the buttons/axes it binds above — normal movement
/// (walking, camera) continues exactly as the base game defines it; nothing here reads or writes player position.
/// </summary>
internal sealed class WorldSculptController {
    /// <summary>The order-sensitive chord pages, in the profile's chord vocabulary
    /// (<c>Puck.Demo.BindingProfileDocuments.LeftModifier</c>/<c>RightModifier</c>).</summary>
    public enum Page {
        /// <summary>Bare (no modifier held): ghost placement.</summary>
        Sculpt = 0,
        /// <summary>[LT] held: selection + move/rotate/scale/rebind/delete.</summary>
        Select = 1,
        /// <summary>[RT] held: terrain slabs/plazas + lights.</summary>
        TerrainLights = 2,
        /// <summary>[LT] then [RT] held (LT first): walkability overrides.</summary>
        WalkOverrides = 3,
        /// <summary>[RT] then [LT] held (RT first): bounds, repeat, save/verify, mirror.</summary>
        BoundsSaveRepeat = 4,
    }

    private const float TriggerThreshold = 0.5f;

    private readonly WorldScene m_scene;
    private readonly Action<string> m_narrate;
    private readonly EditHistory<WorldScene.Snapshot> m_history;
    private readonly Func<ContentAddressedStoreHandle> m_store;
    private GamepadButtons m_prevButtons;
    private bool m_prevLeftTrigger;
    private bool m_prevRightTrigger;
    private readonly List<int> m_heldModifierOrder = [];
    private Page m_page;
    private bool m_dragActive;
    // Pending terrain/light/walk-override authoring knobs (STYLE-page-like state the console/pad both nudge).
    private Vector2 m_pendingTerrainHalfExtents = new(1f, 1f);
    private int m_pendingMaterial;
    private float m_pendingLightIntensity = 1f;
    private Vector2 m_pendingWalkHalfExtents = new(1f, 1f);

    /// <summary>A late-bound accessor for the content-addressed store the sculptor saves/loads/rebinds against — a
    /// delegate (not a constructor dependency) so the host may construct the controller before the store exists.</summary>
    /// <param name="Store">The resolved store.</param>
    public readonly record struct ContentAddressedStoreHandle(Puck.Assets.ContentAddressedStore Store);

    /// <summary>Initializes the controller over the scene it edits.</summary>
    /// <param name="scene">The authored scene the verbs mutate.</param>
    /// <param name="history">The undo/redo ring (capacity 64 — see the World skill's snapshot contract).</param>
    /// <param name="store">Resolves the content-addressed store on demand (save/verify/rebind).</param>
    /// <param name="narrate">Writes a one-line status to the player's console.</param>
    public WorldSculptController(WorldScene scene, EditHistory<WorldScene.Snapshot> history, Func<ContentAddressedStoreHandle> store, Action<string> narrate) {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(narrate);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(store);

        m_history = history;
        m_narrate = narrate;
        m_scene = scene;
        m_store = store;
    }

    /// <summary>The scene this controller edits.</summary>
    public WorldScene Scene => m_scene;

    /// <summary>The active chord page.</summary>
    public Page ActivePage => m_page;

    /// <summary>Clears edge tracking and modifier order — call when sculpt mode toggles so a held button/trigger
    /// never fires a stale edge into the other mode.</summary>
    public void Reset() {
        m_prevButtons = GamepadButtons.None;
        m_prevLeftTrigger = false;
        m_prevRightTrigger = false;
        m_heldModifierOrder.Clear();
        m_page = Page.Sculpt;

        if (m_dragActive) {
            m_scene.EndDrag();
            m_dragActive = false;
        }
    }

    /// <summary>Advances one frame of sculptor input. Presentation + host-side authoring state only — nothing here
    /// reaches the deterministic simulation.</summary>
    /// <param name="raw">The sculpting slot's raw pad state this frame.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Advance(in GamepadState raw, float deltaSeconds) {
        var buttons = raw.Buttons;

        bool Pressed(GamepadButtons button) => ((0 != (buttons & button)) && (0 == (m_prevButtons & button)));

        AdvanceChord(raw: in raw);

        switch (m_page) {
            case Page.Select:
                AdvanceSelectPage(raw: in raw, deltaSeconds: deltaSeconds, pressed: Pressed);
                break;
            case Page.TerrainLights:
                AdvanceTerrainLightsPage(raw: in raw, deltaSeconds: deltaSeconds, pressed: Pressed);
                break;
            case Page.WalkOverrides:
                AdvanceWalkOverridesPage(raw: in raw, deltaSeconds: deltaSeconds, pressed: Pressed);
                break;
            case Page.BoundsSaveRepeat:
                AdvanceBoundsSaveRepeatPage(raw: in raw, deltaSeconds: deltaSeconds, pressed: Pressed);
                break;
            default:
                AdvanceSculptPage(raw: in raw, deltaSeconds: deltaSeconds, pressed: Pressed);
                break;
        }

        m_prevButtons = buttons;
    }

    // Tracks LT/RT as ORDER-SENSITIVE modifiers (mirrors Puck.Commands.BindingChordTracker's held-order model): a
    // press appends to the held-order list, a release removes it, and the page resolves from the exact ordered
    // sequence — [LT] alone, [RT] alone, [LT then RT], or [RT then LT] are four distinct chords past bare.
    private void AdvanceChord(in GamepadState raw) {
        const int leftModifier = 0;
        const int rightModifier = 1;
        var leftHeld = (raw.LeftTrigger >= TriggerThreshold);
        var rightHeld = (raw.RightTrigger >= TriggerThreshold);

        if (leftHeld && !m_prevLeftTrigger) {
            m_heldModifierOrder.Remove(item: leftModifier);
            m_heldModifierOrder.Add(item: leftModifier);
        } else if (!leftHeld && m_prevLeftTrigger) {
            m_heldModifierOrder.Remove(item: leftModifier);
        }

        if (rightHeld && !m_prevRightTrigger) {
            m_heldModifierOrder.Remove(item: rightModifier);
            m_heldModifierOrder.Add(item: rightModifier);
        } else if (!rightHeld && m_prevRightTrigger) {
            m_heldModifierOrder.Remove(item: rightModifier);
        }

        m_prevLeftTrigger = leftHeld;
        m_prevRightTrigger = rightHeld;

        var nextPage = m_heldModifierOrder.Count switch {
            0 => Page.Sculpt,
            1 => ((m_heldModifierOrder[0] == leftModifier) ? Page.Select : Page.TerrainLights),
            _ => ((m_heldModifierOrder[0] == leftModifier) ? Page.WalkOverrides : Page.BoundsSaveRepeat),
        };

        if (nextPage != m_page) {
            // Switching pages mid-drag would strand the drag slot armed on the wrong page's semantics — settle it
            // first (bake-back-on-release), matching the drag contract exactly as a button release would.
            if (m_dragActive) {
                m_scene.EndDrag();
                m_dragActive = false;
            }

            m_page = nextPage;
            m_narrate($"[world] page: {PageName(page: m_page)}");
        }
    }

    private void AdvanceSculptPage(in GamepadState raw, float deltaSeconds, Func<GamepadButtons, bool> pressed) {
        m_scene.Move(deltaSeconds: deltaSeconds, planar: new Vector2(raw.LeftStick.X, -raw.LeftStick.Y));
        m_scene.Rotate(deltaSeconds: deltaSeconds, rate: raw.RightStick.X);
        m_scene.ScaleTarget(deltaSeconds: deltaSeconds, rate: (VerticalAxis(raw: in raw)));

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            PushUndoBaseline();

            if (m_scene.Place() is { } id) {
                m_narrate($"[world] placed #{id} '{m_scene.GhostRefName}'");
            } else {
                m_narrate(m_scene.GhostReady ? "[world] placement budget full" : "[world] nothing armed — world.place <creation> first");
            }
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            m_narrate("[world] ghost cleared — world.place <creation> to arm another");
        }

        if (pressed(arg: GamepadButtons.ButtonNorth)) {
            Undo();
        }

        if (pressed(arg: GamepadButtons.LeftShoulder)) {
            Redo();
        }

        if (pressed(arg: GamepadButtons.LeftStickPress)) {
            Save();
        }
    }

    private void AdvanceSelectPage(in GamepadState raw, float deltaSeconds, Func<GamepadButtons, bool> pressed) {
        var planar = new Vector2(raw.LeftStick.X, -raw.LeftStick.Y);
        var yawRate = raw.RightStick.X;
        var scaleRate = VerticalAxis(raw: in raw);
        var dragInput = ((planar != Vector2.Zero) || (yawRate != 0f) || (scaleRate != 0f));

        if (!m_scene.TargetIsGhost && dragInput && !m_dragActive) {
            PushUndoBaseline();
            m_scene.BeginDrag();
            m_dragActive = true;
        }

        m_scene.Move(deltaSeconds: deltaSeconds, planar: planar);
        m_scene.Rotate(deltaSeconds: deltaSeconds, rate: yawRate);
        m_scene.ScaleTarget(deltaSeconds: deltaSeconds, rate: scaleRate);

        if (m_dragActive && !dragInput) {
            m_scene.EndDrag();
            m_dragActive = false;
            PushRedoTarget();
        }

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            m_scene.CycleSelection(direction: 1);
            NarrateSelection();
        }

        if (pressed(arg: GamepadButtons.ButtonNorth)) {
            m_scene.CycleSelection(direction: -1);
            NarrateSelection();
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            PushUndoBaseline();

            if (m_scene.DeleteSelected()) {
                m_narrate($"[world] deleted — {m_scene.Placements.Count} placement(s) remain");
                PushRedoTarget();
            }
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_scene.Deselect();
            m_narrate("[world] deselected — the ghost is the target again");
        }

        if (pressed(arg: GamepadButtons.LeftShoulder) && (m_scene.GhostRefName is { } refName) && (m_scene.GhostSourceHash is { } hash)) {
            PushUndoBaseline();

            if (m_scene.RebindSelected(hash: hash, refName: refName, refusal: out var refusal, store: m_store().Store)) {
                m_narrate($"[world] rebound selection to '{refName}'");
                PushRedoTarget();
            } else {
                m_narrate($"[world] rebind refused: {refusal}");
            }
        }

        if (pressed(arg: GamepadButtons.RightShoulder) && (m_scene.SelectedPlacement is { } selected)) {
            PushUndoBaseline();
            m_scene.SetSelectedRole(role: ((selected.Role is null) ? "cabinet:0" : null));
            m_narrate($"[world] role: {m_scene.SelectedPlacement?.Role ?? "decoration"}");
            PushRedoTarget();
        }
    }

    private void AdvanceTerrainLightsPage(in GamepadState raw, float deltaSeconds, Func<GamepadButtons, bool> pressed) {
        const float resizeSpeed = 1.5f;
        const float intensitySpeed = 1.2f;

        m_pendingTerrainHalfExtents = Vector2.Max(value1: (m_pendingTerrainHalfExtents + (raw.LeftStick * resizeSpeed * deltaSeconds)), value2: new Vector2(0.1f));

        if (raw.RightStick.X != 0f) {
            m_pendingMaterial = (((m_pendingMaterial + Math.Sign(raw.RightStick.X)) % 4) + 4) % 4;
        }

        m_pendingLightIntensity = Math.Clamp(value: (m_pendingLightIntensity + (VerticalAxis(raw: in raw) * intensitySpeed * deltaSeconds)), max: 4f, min: 0.05f);

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            AddTerrainWithHistory(kind: "slab");
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            AddTerrainWithHistory(kind: "plaza");
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            if (m_scene.Lights.Count >= WorldScene.MaxLights) {
                m_narrate($"[world] light budget full ({WorldScene.MaxLights})");
            } else {
                PushUndoBaseline();

                var id = m_scene.AddLight(color: Vector3.One, intensity: m_pendingLightIntensity);

                m_narrate($"[world] light #{id} added (intensity {m_pendingLightIntensity:F2})");
                PushRedoTarget();
            }
        }

        if (pressed(arg: GamepadButtons.ButtonNorth)) {
            RemoveNearestTerrainOrLight();
        }
    }

    private void AddTerrainWithHistory(string kind) {
        if (m_scene.Terrain.Count >= WorldScene.MaxTerrainPatches) {
            m_narrate($"[world] terrain budget full ({WorldScene.MaxTerrainPatches})");

            return;
        }

        PushUndoBaseline();

        var id = m_scene.AddTerrain(halfExtents: new Vector3(m_pendingTerrainHalfExtents.X, 0.05f, m_pendingTerrainHalfExtents.Y), kind: kind, material: m_pendingMaterial);

        m_narrate($"[world] {kind} #{id} added ({m_pendingTerrainHalfExtents.X:F1}×{m_pendingTerrainHalfExtents.Y:F1}, material {m_pendingMaterial})");
        PushRedoTarget();
    }

    private void AdvanceWalkOverridesPage(in GamepadState raw, float deltaSeconds, Func<GamepadButtons, bool> pressed) {
        const float resizeSpeed = 1.5f;

        m_pendingWalkHalfExtents = Vector2.Max(value1: (m_pendingWalkHalfExtents + (raw.LeftStick * resizeSpeed * deltaSeconds)), value2: new Vector2(0.1f));

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            AddWalkOverrideWithHistory(kind: "blocker");
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            AddWalkOverrideWithHistory(kind: "walkable");
        }

        if (pressed(arg: GamepadButtons.ButtonNorth)) {
            RemoveNearestWalkOverride();
        }
    }

    private void AddWalkOverrideWithHistory(string kind) {
        if (m_scene.WalkOverrides.Count >= WorldScene.MaxWalkOverrides) {
            m_narrate($"[world] walk-override budget full ({WorldScene.MaxWalkOverrides})");

            return;
        }

        PushUndoBaseline();

        var id = m_scene.AddWalkOverride(halfExtents: m_pendingWalkHalfExtents, kind: kind);

        m_narrate($"[world] {kind} #{id} painted ({m_pendingWalkHalfExtents.X:F1}×{m_pendingWalkHalfExtents.Y:F1})");
        PushRedoTarget();
    }

    private void AdvanceBoundsSaveRepeatPage(in GamepadState raw, float deltaSeconds, Func<GamepadButtons, bool> pressed) {
        const float boundsSpeed = 3f;

        if (raw.LeftStick != Vector2.Zero) {
            PushUndoBaseline();

            var bounds = m_scene.GrowBounds(deltaX: (raw.LeftStick.X * boundsSpeed * deltaSeconds), deltaZ: (-raw.LeftStick.Y * boundsSpeed * deltaSeconds));

            m_narrate($"[world] bounds: ({bounds.MinX:F1},{bounds.MinZ:F1}) to ({bounds.MaxX:F1},{bounds.MaxZ:F1})");
        }

        if (!m_scene.TargetIsGhost && (m_scene.SelectedPlacement is { } selected) && ((raw.RightStick.X != 0f) || (VerticalAxis(raw: in raw) != 0f))) {
            PushUndoBaseline();

            var current = (selected.Repeat ?? new WorldPlacementRepeat(CountX: 1, CountZ: 1, SpacingX: 1.5f, SpacingZ: 1.5f));
            var countX = Math.Max(val1: 1, val2: (current.CountX + Math.Sign(raw.RightStick.X)));
            var countZ = Math.Max(val1: 1, val2: (current.CountZ + Math.Sign(VerticalAxis(raw: in raw))));

            m_narrate(m_scene.SetSelectedRepeat(refusal: out var refusal, repeat: (current with { CountX = countX, CountZ = countZ }))
                ? $"[world] repeat: {countX}×{countZ}"
                : $"[world] repeat refused — {refusal}");
        }

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            Save();
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            Verify();
        }

        if (pressed(arg: GamepadButtons.ButtonWest) && !m_scene.TargetIsGhost) {
            PushUndoBaseline();
            _ = m_scene.SetSelectedRepeat(refusal: out _, repeat: null);
            m_narrate("[world] repeat cleared — a single copy");
        }

        if (pressed(arg: GamepadButtons.ButtonNorth)) {
            CycleSelectedMirror();
        }
    }

    // North on the BOUNDS/SAVE/REPEAT page cycles the SELECTED placement's mirror axis off → x → z → off (the
    // console world.mirror verb sets it exactly; world.pattern stays console-only — a wallpaper group needs typed
    // parameters).
    private void CycleSelectedMirror() {
        if (m_scene.SelectedPlacement is not { } selected) {
            m_narrate("[world] mirror needs a selection — SELECT page ([LT]) South/North cycles placements");

            return;
        }

        var next = (selected.Mirror switch {
            "x" => "z",
            "z" => null,
            _ => "x",
        });

        PushUndoBaseline();
        _ = m_scene.SetSelectedMirror(axis: next);
        m_narrate($"[world] mirror: {next ?? "off"} (world.pattern adds a wallpaper fold from the console)");
        PushRedoTarget();
    }

    private void RemoveNearestTerrainOrLight() {
        var target = m_scene.TargetPosition;
        var nearestTerrainId = -1;
        var nearestTerrainDistance = float.MaxValue;

        foreach (var patch in m_scene.Terrain) {
            var distance = Vector3.DistanceSquared(value1: patch.Center, value2: target);

            if (distance < nearestTerrainDistance) {
                nearestTerrainDistance = distance;
                nearestTerrainId = patch.Id;
            }
        }

        if (nearestTerrainId >= 0) {
            PushUndoBaseline();
            m_scene.RemoveTerrain(id: nearestTerrainId);
            m_narrate($"[world] removed terrain #{nearestTerrainId}");
            PushRedoTarget();

            return;
        }

        var nearestLightId = -1;
        var nearestLightDistance = float.MaxValue;

        foreach (var light in m_scene.Lights) {
            var distance = Vector3.DistanceSquared(value1: light.Position, value2: target);

            if (distance < nearestLightDistance) {
                nearestLightDistance = distance;
                nearestLightId = light.Id;
            }
        }

        if (nearestLightId >= 0) {
            PushUndoBaseline();
            m_scene.RemoveLight(id: nearestLightId);
            m_narrate($"[world] removed light #{nearestLightId}");
            PushRedoTarget();

            return;
        }

        m_narrate("[world] nothing nearby to remove");
    }

    private void RemoveNearestWalkOverride() {
        var target = m_scene.TargetPosition;
        var nearestId = -1;
        var nearestDistance = float.MaxValue;

        foreach (var entry in m_scene.WalkOverrides) {
            var center = new Vector2((0.5f * (entry.MinX + entry.MaxX)), (0.5f * (entry.MinZ + entry.MaxZ)));
            var distance = Vector2.DistanceSquared(value1: center, value2: new Vector2(target.X, target.Z));

            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearestId = entry.Id;
            }
        }

        if (nearestId < 0) {
            m_narrate("[world] no walk overrides to remove");

            return;
        }

        PushUndoBaseline();
        m_scene.RemoveWalkOverride(id: nearestId);
        m_narrate($"[world] removed walk override #{nearestId}");
        PushRedoTarget();
    }

    private void Undo() {
        if (m_history.TryUndo(snapshot: out var snapshot)) {
            m_scene.RestoreSnapshot(snapshot: snapshot);
            m_narrate("[world] undo");
        } else {
            m_narrate("[world] nothing to undo");
        }
    }

    private void Redo() {
        if (m_history.TryRedo(snapshot: out var snapshot)) {
            m_scene.RestoreSnapshot(snapshot: snapshot);
            m_narrate("[world] redo");
        } else {
            m_narrate("[world] nothing to redo");
        }
    }

    private void Save() {
        var (path, hash) = m_scene.Save(store: m_store().Store);
        var petname = ((hash is { } h) ? Puck.Assets.ContentPetname.From(hashHex: h) : null);

        m_narrate((hash is { } fullHash)
            ? $"[world: saved as \"{petname}\" (sha256/{fullHash["sha256/".Length..][..12]}…)]"
            : $"[world: saved to {path} (no CAS store)]");
    }

    private void Verify() {
        var live = WorldDocumentStore.ToJson(document: m_scene.ToDocument());
        var loaded = WorldDocumentStore.Load(nameOrPath: m_scene.Name);

        if (loaded is null) {
            m_narrate("[world.verify: no saved copy to compare against — world.save first]");

            return;
        }

        var stored = WorldDocumentStore.ToJson(document: loaded);

        m_narrate(string.Equals(a: live, b: stored, comparisonType: StringComparison.Ordinal)
            ? "[world.verify: MATCH]"
            : "[world.verify: MISMATCH — the live model differs from the last save; world.save to reconcile]");
    }

    private void PushUndoBaseline() {
        m_history.Push(snapshot: m_scene.CaptureSnapshot());
    }

    // After a structural edit lands, the model's NEW state also belongs in the ring so a subsequent undo steps back
    // to the pre-edit baseline already pushed and a subsequent redo can restore the edit itself.
    private void PushRedoTarget() {
        m_history.Push(snapshot: m_scene.CaptureSnapshot());
    }

    private void NarrateSelection() {
        m_narrate((m_scene.SelectedPlacement is { } placement)
            ? $"[world] selected #{placement.Id} '{placement.RefName}'"
            : "[world] selection cleared — the ghost is the target again");
    }

    // The d-pad's vertical axis is the one page-independent analog every page reads the same way (scale/intensity/
    // repeat-Z) — up = +1, down = -1, matching CreatorController's convention.
    private static float VerticalAxis(in GamepadState raw) {
        var up = (0 != (raw.Buttons & GamepadButtons.DpadUp));
        var down = (0 != (raw.Buttons & GamepadButtons.DpadDown));

        return ((up ? 1f : 0f) - (down ? 1f : 0f));
    }

    private static string PageName(Page page) {
        return page switch {
            Page.Select => "SELECT — South/North cycle selection, East deletes, West deselects, LB rebinds to the armed ghost, RB toggles cabinet role; left stick moves/right stick rotates/d-pad scales the SELECTED stamp (drag)",
            Page.TerrainLights => "TERRAIN/LIGHTS — South adds a slab, East a plaza, West a light, North removes the nearest; left stick resizes, right stick cycles material, d-pad adjusts light intensity",
            Page.WalkOverrides => "WALK OVERRIDES — South paints a blocker, East a walkable, North removes the nearest; left stick resizes the pending rectangle",
            Page.BoundsSaveRepeat => "BOUNDS/SAVE/REPEAT — South saves, East verifies, West clears the selected repeat, North cycles the selected mirror (off/x/z); left stick grows bounds, right stick/d-pad adjust the selected repeat count",
            _ => "SCULPT — South places the ghost, East clears it, North undoes, LB redoes, left-stick-click saves; left stick moves/right stick spins/d-pad scales the ghost",
        };
    }
}
