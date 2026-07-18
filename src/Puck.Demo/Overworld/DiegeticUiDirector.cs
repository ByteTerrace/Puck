using System.Numerics;
using Puck.Demo.BindingBar;
using Puck.Demo.DevConsole;
using Puck.Demo.Text;
using Puck.Demo.Ui;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.Demo.Overworld;

/// <summary>
/// The diegetic-UI Tier-2 composition point: the overlay action bar (the chord-first binding bar) re-expressed as
/// REAL world geometry, mounted on the camera rig so it reads as a physical HUD floating low in view. The overlay
/// console/bar stays FOREVER as the convenience/agent surface (stdin/stdout is the control plane); this MIRRORS it
/// for immersion — same layout (<see cref="BindingBarLayout"/>), same data (the live <see cref="IBindingBarSource"/>
/// frame the overlay renders), re-projected onto a camera-rig plane as extruded SDF panels with EMBOSSED text labels.
///
/// <para>This type also OWNS the shared glyph-atlas infrastructure — ONE atlas built once
/// (<see cref="GlyphAtlasBuilder"/>, the real-font GDI+ path) serving three consumers: the world-glyph op marches
/// its ALPHA channel (<see cref="WorldAtlas"/>, surfaced to the engine through the <see cref="ISdfFrameSource.GlyphAtlas"/>
/// seam this director decorates onto the inner frame source), and the FontAtlas itself (<see cref="Font"/>, carrying
/// the glyph table + metrics) is the seam a future diegetic-decal / overlay-2D consumer samples with median-of-3
/// coverage. Built as SDF-shaped data so the single generation call is never forked.</para>
///
/// <para>It rides the frame source through a pure DELEGATE seam (<c>OverworldFrameSource.InstallDiegeticUi</c>): the
/// frame source names no new type to host it — it invokes <see cref="Emit"/> inside its program build, reads
/// <see cref="Mount"/> for the per-frame camera-rig transform, and folds <see cref="ContentSignature"/> into its
/// rebuild trigger. The bar is ONE dynamic instance parked on a reserved dynamic-transform slot: the geometry is
/// baked in bar-local space and REBUILT only when the bar's CONTENT changes (page flip, binding change, family swap
/// — the dirty signature), while the camera-rig pose rides the slot's per-frame <see cref="DynamicTransform"/>, so a
/// moving camera never rebuilds the program.</para>
/// </summary>
internal sealed class DiegeticUiDirector : ISdfFrameSource {
    // The primary bar's twelve physical slots (the WoW cluster). The diegetic mirror shows the PRIMARY player's bar
    // only this tier; multiplayer quadrant bars stay overlay-only (see the class remarks / the deferred list).
    private const int MaxChips = 12;
    // The longest button label the atlas is asked to lay out (e.g. "LB"/"RT"). KEEP IN SYNC with
    // OverworldFrameSource's DiegeticBarMaxLabelChars (the worst-case probe reserves this many glyph cells per chip).
    private const int MaxLabelChars = 2;

    // Bar-LOCAL geometry (world units, in the mount's plane: +X right, +Y up, +Z toward the viewer). The chips ride
    // proud of a dark backing plate (the contrast pattern), and the labels emboss proud of the chips (never coplanar).
    private const float WorldScale = 1.35f;          // aspect-units (BindingBarLayout) -> world metres at the rig
    private const float ChipReliefHalf = 0.010f;     // REST/ACCENT chip slab half-depth along +Z (fully proud/emboss)
    // HELD/DISABLED chips sit pressed toward the plate (a shallow, still-proud relief — never coplanar, the standing
    // law) instead of the REST depth: a physical button reads "pushed in" and a free socket reads "barely there".
    private const float ChipRecessedReliefHalf = (ChipReliefHalf * 0.3f);
    private const float ChipFrontZ = (ChipReliefHalf * 2f);   // the REST chip's front face (its centre sits at +ChipReliefHalf)
    private const float BackingReliefHalf = 0.010f;  // backing plate half-depth (its front face sits at z = 0, behind the chips)
    private const float LabelReliefHalf = 0.006f;    // label glyph extrude half-depth (straddles the chip face -> protrudes)
    // Corner radii carry the TOKEN PROPORTION, not an absolute px value (docs/ui-design-tokens.md section 2): a chip
    // is the r.1 (Radius1) component sized against its own HeightChip half-extent, and the whole strip (the backing
    // plate) is the r.2 (Radius2) component sized against HeightBindBar — exactly the ratio BindingBarOverlayNode's
    // InitializePushConstants bakes for the 2D chip, so the diegetic and overlay chips read as the SAME rounding.
    private const float ChipCornerFraction = (DesignTokens.Radius.Radius1 / (DesignTokens.Space.HeightChip * 0.5f));
    private const float BackingCornerRadiusRatio = (DesignTokens.Radius.Radius2 / (DesignTokens.Space.HeightBindBar * 0.5f));
    private const float BackingPadding = 0.05f;      // the backing plate's margin past the chip cluster
    private const float LabelEmFraction = 1.30f;     // label em height as a fraction of the chip half-height
    private const float BoundRadius = 1.20f;         // the whole strip's bounding sphere (folds the rig orientation in)

    // The hub mode readout (Wave 5 Theme A): the highlighted authoring mode's name embossed proud of the strip, above
    // the chip cluster, when the mirrored bar is a hub page. It rides the SAME ActivePageId the bar already carries, so
    // the diegetic name is the overlay bar's page id expressed in world text. The longest readout is "HUB TRACKER"
    // (11 chars) — KEEP IN SYNC with OverworldFrameSource's DiegeticTitleMaxChars (the worst-case probe reserves it).
    private const float TitleEmHeight = 0.055f;      // the readout's em height (larger than a chip label)
    private const float TitleTopGap = 0.03f;         // gap above the backing-plate top to the readout's centre

    // Camera-rig mount (the physical-HUD placement). The bar sits a fixed distance ahead of the eye and dropped low,
    // facing the camera — constant apparent size, so it never goes sub-pixel (the camera-rig-text rule).
    private const float MountDistance = 1.55f;       // metres ahead of the eye
    private const float MountDrop = 0.62f;           // metres below the view centre (low in view, like the overlay bar)

    // The material palette — docs/ui-design-tokens.md section 6 (the diegetic emboss/engrave physics), mirroring the
    // FOUR overlay chip tiers (section 5) to whatever extent world geometry carries the signal (BindingSlotView's
    // Bound/Pressed/Accent, the same fields the 2D chip shader keys on — see binding-bar-overlay.frag.hlsl's
    // isHeld/isAccentTier/isDisabled precedence, mirrored exactly by the Emit loop's own isHeld/isAccentTier/isDisabled):
    //   REST      (Bound, !Pressed, !Accent) — proud/EMBOSSED: fill strictly BRIGHTER than the plate.
    //   HELD      (Pressed)                  — pressed toward the plate + ENGRAVED: fill strictly DARKER, shallow relief.
    //   ACCENT    (Accent && !Pressed)        — proud + the ONE electric accent hue, strong emissive (the glow stand-in
    //                                           a world material has no bloom pass for).
    //   DISABLED  (!Bound && !Pressed && !Accent) — a barely-there ENGRAVED socket (dim, no emissive).
    // The backing plate is the physical CHASSIS METAL itself (PlateMid) the chips are relief-cut into/proud of.
    private static readonly Vector3 BackingAlbedo = DesignTokens.Diegetic.PlateMid.Rgb;
    private static readonly Vector3 ChipRestAlbedo = DesignTokens.Diegetic.EmbossFill.Rgb;
    private static readonly Vector3 ChipHeldAlbedo = DesignTokens.Diegetic.EngraveFill.Rgb;
    private static readonly Vector3 ChipAccentAlbedo = DesignTokens.Color.Accent.Rgb;
    private static readonly Vector3 ChipDisabledAlbedo = DesignTokens.Diegetic.EngraveFill.Rgb;

    private const float ChipRestEmissive = 0.06f;    // a faint raised-metal catchlight, never a signal
    private const float ChipAccentEmissive = 0.55f;  // the ONE lit signal (bloom.accent's world stand-in)
    // The button-badge label: EMBOSSED proud of the chip face in every tier except ACCENT, where section 5's chip
    // recipe reads "badge fills accent, glyph accent.ink" — a dark ink label on the bright accent fill.
    private static readonly Vector3 LabelAlbedo = DesignTokens.Diegetic.EmbossFill.Rgb;
    private static readonly Vector3 LabelAccentInkAlbedo = DesignTokens.Color.AccentInk.Rgb;

    private const float LabelEmissive = 0.10f;

    // The terminal nameplate (signage delight): an embossed short sign on the diegetic console terminal. Its world
    // frame is pulled LIVE from the frame source each build (the terminal moves when a world loads), so the sign
    // tracks the terminal. KEEP the char count in sync with OverworldFrameSource's DiegeticNameplateMaxChars (the
    // worst-case probe reserves that many glyph cells for the nameplate's static instance).
    private const string NameplateText = "PUCK/1";
    private const float NameplateEmHeight = 0.11f;      // world height of one em
    private const float NameplateReliefHalf = 0.008f;   // extrude half-depth (straddles the terminal face -> embossed relief)
    private const float NameplateBoundRadius = 0.6f;
    // Embossed relief (docs/ui-design-tokens.md section 6): the sign's fill is the SAME raised-fill token every other
    // proud glyph on the diegetic tier uses — no bespoke hue.
    private static readonly Vector3 NameplateAlbedo = DesignTokens.Diegetic.EmbossFill.Rgb;

    private const float NameplateEmissive = 0.12f;

    private readonly ISdfFrameSource m_inner;
    private readonly IBindingBarSource m_barSource;
    private readonly FontAtlas? m_font;
    // The UI grotesque's medium weight — the KERNED Inter view of the SAME shared image m_font/m_worldAtlas already
    // upload (SharedGlyphAtlas bakes every font into ONE combined PNG), used for the two proud-standing text runs
    // that read better as UI type than terminal mono: the terminal nameplate and the hub-mode readout (see EmitNameplate
    // / HubTitle's EmitGlyphRun call). Falls back to the mono m_font when the pre-baked UI atlas is absent (no
    // wasted glyph run — same fallback posture as m_font itself).
    private readonly FontAtlas? m_uiFontMedium;
    private readonly SdfGlyphAtlas? m_worldAtlas;
    private readonly TextLayout m_textLayout = new();
    // The dynamic-transform slot the whole bar rides (bound by the frame source at install — see BindSlotBase).
    private int m_slot;
    // The live terminal-nameplate frame (centre / right / up world axes), pulled each build so the sign tracks the
    // terminal when a world load moves it. Null until SetTerminalNameplate wires them (no nameplate then).
    private Func<Vector3>? m_nameplateCentre;
    private Func<Vector3>? m_nameplateRight;
    private Func<Vector3>? m_nameplateUp;
    // The diegetic terminal's GLYPH DECAL (the cell-grid text mode): the console mirror as resolution-independent text
    // the engine's decal sampling reconstructs from the shared atlas, crisp at walk-up distance. m_screenDecals is the
    // per-frame ScreenDecals provider (built once at SetTerminalDecal when the shared atlas exists; null off-Windows so
    // the terminal falls back to the CPU bitmap / dark CRT). The cells are re-baked only on a console content change.
    private IConsoleTextSource? m_terminalConsole;
    private IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? m_screenDecals;
    private uint[]? m_terminalCells;
    private long m_terminalDecalSignature = -1L;

    /// <summary>Composes the director over the inner frame source it decorates, reading the live binding-bar state the
    /// overlay renders and building the shared glyph atlas once.</summary>
    /// <param name="inner">The frame source whose per-frame capture + screen-surface transforms this forwards; its
    /// program build invokes <see cref="Emit"/> through the installed delegate seam.</param>
    /// <param name="barSource">The published binding-bar frame the overlay renders — the diegetic bar's data source.</param>
    public DiegeticUiDirector(ISdfFrameSource inner, IBindingBarSource barSource) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(barSource);

        m_inner = inner;
        m_barSource = barSource;
        // The ONE shared atlas (SharedGlyphAtlas memoizes the exact-EDT coverage->SDF build, alpha the marchable
        // channel) — the SAME instance the 2D overlay surfaces sample as decals, so the diegetic bar's world text and
        // the overlay's screen text are literally one atlas + one TextLayout. Null off-Windows or with no suitable font
        // — the bar then renders its chips as plain panels with no letters, still a legible physical mirror (the glyph
        // cells fall back to their conservative boxes).
        m_font = SharedGlyphAtlas.MonoFont;
        m_uiFontMedium = (SharedGlyphAtlas.UiFontMedium ?? m_font);

        if ((m_font is { } font) && (font.ImageData is { } image)) {
            m_worldAtlas = new SdfGlyphAtlas(Rgba: image.RgbaPixels, Width: (uint)font.Width, Height: (uint)font.Height);
        }
    }

    /// <summary>The single font atlas (glyph table + metrics + SDF image) — the shared infrastructure a future diegetic
    /// decal / overlay-2D consumer samples (median-of-3 coverage is legitimate there); the world-glyph op marches its
    /// alpha through <see cref="WorldAtlas"/>. Null when GDI+ produced no atlas.</summary>
    public FontAtlas? Font => m_font;

    /// <summary>The world-glyph atlas the engine uploads once — the ALPHA-channel true distance the
    /// <see cref="SdfShapeType.Glyph"/> op marches (see the class remarks' three-consumer note). Null when no atlas built.</summary>
    public SdfGlyphAtlas? WorldAtlas => m_worldAtlas;

    // ---- ISdfFrameSource (the decorator: the inner source does the real work; this supplies the shared atlas) --------

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
        m_inner.CaptureFrame(width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);

    /// <inheritdoc/>
    public void AdvanceBricks(Puck.SdfVm.ISdfBrickBakeService bakes) =>
        m_inner.AdvanceBricks(bakes: bakes);

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => m_inner.ScreenSurfaceTransforms;

    /// <inheritdoc/>
    public SdfGlyphAtlas? GlyphAtlas => m_worldAtlas;

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => (m_screenDecals ?? m_inner.ScreenDecals);

    // ---- The installed delegate seam (called by OverworldFrameSource; primitive/already-named types only) -----------

    /// <summary>Binds the dynamic-transform slot the bar rides (the frame source reserves it in its slot layout and
    /// hands it here at install). One slot: the whole bar is one rigid unit mounted on the camera rig.</summary>
    /// <param name="slot">The reserved dynamic-transform slot.</param>
    public void BindSlotBase(int slot) => m_slot = slot;

    /// <summary>Wires the LIVE terminal-nameplate frame (world centre + right/up axes of the sign's face), pulled each
    /// build so the embossed sign tracks the diegetic terminal (which moves when a world loads). The frame source owns
    /// the terminal geometry; these primitive-typed readers keep it — at its coupling ceiling — from naming this type.</summary>
    /// <param name="centre">The nameplate face centre (world).</param>
    /// <param name="right">The face's world +X axis (advance direction).</param>
    /// <param name="up">The face's world +Y axis.</param>
    public void SetTerminalNameplate(Func<Vector3> centre, Func<Vector3> right, Func<Vector3> up) {
        m_nameplateCentre = centre;
        m_nameplateRight = right;
        m_nameplateUp = up;
    }

    /// <summary>Binds the diegetic terminal's screen slot to the CELL-GRID text mode: its CRT face samples a grid of
    /// glyph cells the engine's decal sampling reconstructs from the SHARED atlas (owned here), so the console mirror
    /// reads crisp at walk-up distance instead of a scaled bitmap. Composes over the console read seam directly (the
    /// baking lives in <see cref="ConsoleFeed"/>'s static helpers), so the ceilinged frame source names no new type to
    /// wire it. No decal is offered off-Windows (no shared atlas) — the terminal then falls back to the CPU bitmap /
    /// dark CRT, the documented degrade.</summary>
    /// <param name="slot">The terminal's declared screen slot.</param>
    /// <param name="console">The console read seam the CRT mirrors, or null (an empty terminal).</param>
    public void SetTerminalDecal(int slot, IConsoleTextSource? console) {
        m_terminalConsole = console;

        if (m_font is not { } font) {
            m_screenDecals = null; // no shared atlas => the terminal stays on the CPU bitmap / dark CRT fallback

            return;
        }

        m_screenDecals = new Dictionary<int, Func<SdfScreenDecalFrame?>>(capacity: 1) {
            [slot] = () => ComposeTerminalDecal(font: font),
        };
    }

    // Re-bakes the terminal's cell grid from the console's trailing history + prompt only when the shown content changed
    // (the director-side dirty cache over ConsoleFeed's signature); the reused cell buffer is copied into engine scratch
    // by SetScreenDecal each frame, so returning it stably is safe.
    private SdfScreenDecalFrame ComposeTerminalDecal(FontAtlas font) {
        var signature = ConsoleFeed.ComputeDecalSignature(source: m_terminalConsole);

        if ((m_terminalCells is null) || (signature != m_terminalDecalSignature)) {
            m_terminalCells ??= new uint[((ConsoleFeed.DecalColumns * ConsoleFeed.DecalRows) * 4)];

            ConsoleFeed.BakeDecalCells(source: m_terminalConsole, atlas: font, cells: m_terminalCells);
            m_terminalDecalSignature = signature;
        }

        return new SdfScreenDecalFrame(Columns: ConsoleFeed.DecalColumns, Rows: ConsoleFeed.DecalRows, DistanceRange: font.DistanceRange, Cells: m_terminalCells);
    }

    /// <summary>The per-frame camera-rig transform: places the bar a fixed distance ahead of the eye, dropped low, and
    /// oriented to face the camera — the physical-HUD mount. Read by the frame source each capture and written into the
    /// bar's dynamic-transform slot, so the moving camera repositions the bar without any program rebuild.</summary>
    /// <param name="views">This frame's composed views; view 0 is the room/reveal camera the bar rides.</param>
    /// <returns>The bar mount as a rigid <see cref="DynamicTransform"/>.</returns>
    public DynamicTransform Mount(IReadOnlyList<SdfViewSnapshot> views) {
        if (views.Count == 0) {
            return new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        }

        var camera = views[0].Camera;
        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        // The bar faces the camera: local +X -> right, +Y -> up, +Z (the extrude/emboss normal) -> toward the eye
        // (-forward). Same row-basis quaternion the Text op uses (System.Numerics row-vector Transform).
        var facing = -forward;
        var orientation = Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: right.X, m12: right.Y, m13: right.Z, m14: 0f,
            m21: up.X, m22: up.Y, m23: up.Z, m24: 0f,
            m31: facing.X, m32: facing.Y, m33: facing.Z, m34: 0f,
            m41: 0f, m42: 0f, m43: 0f, m44: 1f
        ));
        var position = ((camera.Position + (forward * MountDistance)) - (up * MountDrop));

        return new DynamicTransform(Position: position, Orientation: orientation);
    }

    /// <summary>A content signature that changes when the bar's content changes (page id, controller family, each
    /// slot's badge/action/visibility) OR when a chip's TIER changes (Bound/Pressed/Accent — the diegetic emboss/
    /// engrave physics now key off these, mirroring the overlay's four tiers). Pressed only EDGES twice per press
    /// (down, then up) rather than changing every frame a button is held, so folding it in still costs at most two
    /// rebuilds per press — not a rebuild-every-frame regression (the restraint this signature has always kept). The
    /// frame source folds this into its program-rebuild trigger.</summary>
    public int ContentSignature {
        get {
            if (!m_barSource.TrySnapshot(frame: out var frame)) {
                return 0;
            }

            var hash = new HashCode();

            hash.Add(value: frame.ActivePageId, comparer: StringComparer.Ordinal);
            hash.Add(value: (int)frame.Family);
            hash.Add(value: frame.BarCount);

            var slots = frame.Slots.Span;
            var count = Math.Min(val1: slots.Length, val2: MaxChips);

            for (var index = 0; (index < count); index++) {
                var slot = slots[index];

                hash.Add(value: (int)slot.Glyph);
                hash.Add(value: (int)slot.Icon);
                hash.Add(value: slot.Visible);
                hash.Add(value: slot.Bound);
                hash.Add(value: slot.Pressed);
                hash.Add(value: slot.Accent);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>Emits the diegetic bar as ONE dynamic instance riding the reserved slot: a dark backing plate, one
    /// rounded panel per visible chip (accented when the slot is bound), and the button label embossed proud of each
    /// chip. Called by the frame source inside its program build when the bar is visible; no-op when no bar frame has
    /// been published yet (frame 0). The geometry is baked in bar-local space — the camera-rig pose rides the slot's
    /// per-frame transform (see <see cref="Mount"/>).</summary>
    /// <param name="builder">The program builder being assembled.</param>
    public void Emit(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        // The terminal nameplate (signage delight) — a static embossed sign, emitted whenever the diegetic UI is up.
        EmitNameplate(builder: builder);

        if (!m_barSource.TrySnapshot(frame: out var frame)) {
            return;
        }

        var backingMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: BackingAlbedo));
        var chipRestMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ChipRestAlbedo, Emissive: ChipRestEmissive));
        var chipHeldMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ChipHeldAlbedo));
        var chipAccentMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ChipAccentAlbedo, Emissive: ChipAccentEmissive));
        var chipDisabledMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ChipDisabledAlbedo));
        var labelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: LabelAlbedo, Emissive: LabelEmissive));
        var labelAccentInkMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: LabelAccentInkAlbedo));

        var options = (BindingBarLayoutOptions.Default with { BarCount = 1 });
        var anchor = BindingBarLayout.BarAnchor(bar: 0, barCount: 1, aspect: 1f, anchorOffsetY: options.AnchorOffsetY);
        var slots = frame.Slots.Span;
        var chipCount = Math.Min(val1: slots.Length, val2: MaxChips);

        // The cluster extent (bar-local, world units) — used to size the backing plate that spans every chip.
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var anyVisible = false;

        for (var index = 0; (index < chipCount); index++) {
            if (!slots[index].Visible) {
                continue;
            }

            var placement = BindingBarLayout.Place(index: index, options: in options, aspect: 1f);

            var (cx, cy) = LocalCentre(placement: placement, anchor: anchor);
            var half = (placement.HalfSize * WorldScale);

            anyVisible = true;
            minX = MathF.Min(x: minX, y: (cx - half));
            maxX = MathF.Max(x: maxX, y: (cx + half));
            minY = MathF.Min(x: minY, y: (cy - half));
            maxY = MathF.Max(x: maxY, y: (cy + half));
        }

        if (!anyVisible) {
            return;
        }

        _ = builder.BeginInstanceDynamic(slot: m_slot, boundOffset: Vector3.Zero, boundRadius: BoundRadius, active: true);

        // The backing plate: a dark rounded slab spanning the whole cluster, its front face at z = 0 so the chips sit
        // proud of it (the contrast pattern for the uncontrolled world background behind the bar).
        var backingHalfWidth = ((0.5f * (maxX - minX)) + BackingPadding);
        var backingHalfHeight = ((0.5f * (maxY - minY)) + BackingPadding);
        var backingCentreX = (0.5f * (minX + maxX));
        var backingCentreY = (0.5f * (minY + maxY));

        var backingCornerRadius = (backingHalfHeight * BackingCornerRadiusRatio);

        _ = builder
            .ResetPoint()
            .TransformDynamic(slot: m_slot)
            .Translate(offset: new Vector3(x: backingCentreX, y: backingCentreY, z: -BackingReliefHalf))
            .RoundedRectangle(halfWidth: backingHalfWidth, halfHeight: backingHalfHeight, cornerRadius: backingCornerRadius, lift: SdfLift.Extrude, liftAmount: BackingReliefHalf, material: backingMaterial);

        for (var index = 0; (index < chipCount); index++) {
            var slot = slots[index];

            if (!slot.Visible) {
                continue;
            }

            var placement = BindingBarLayout.Place(index: index, options: in options, aspect: 1f);

            var (cx, cy) = LocalCentre(placement: placement, anchor: anchor);
            var half = (placement.HalfSize * WorldScale);

            // The FOUR chip tiers, mirroring the overlay chip shader's EXACT precedence (docs/ui-design-tokens.md
            // section 5 + binding-bar-overlay.frag.hlsl's isHeld/isAccentTier/isDisabled): HELD (the physical button
            // is currently down) beats ACCENT (the context-primary action) beats DISABLED (no action bound) beats
            // REST. The diegetic tier expresses each as the section-6 emboss/engrave physics instead of the overlay's
            // flat surface roles: HELD/DISABLED press the chip toward the plate and ENGRAVE its fill (strictly
            // darker); REST/ACCENT stand fully proud and EMBOSS it (strictly brighter) — never coplanar with the plate.
            var isHeld = slot.Pressed;
            var isAccentTier = (slot.Accent && !slot.Pressed);
            var isDisabled = (!slot.Bound && !slot.Pressed && !slot.Accent);
            var chipMaterial = (isHeld ? chipHeldMaterial : (isAccentTier ? chipAccentMaterial : (isDisabled ? chipDisabledMaterial : chipRestMaterial)));
            var chipReliefHalf = ((isHeld || isDisabled) ? ChipRecessedReliefHalf : ChipReliefHalf);
            // ACCENT's badge fills accent + reads its glyph in accent.ink (section 5's accent chip recipe); every
            // other tier keeps the standard embossed label.
            var chipLabelMaterial = (isAccentTier ? labelAccentInkMaterial : labelMaterial);
            var chipFrontZ = (chipReliefHalf * 2f);

            // The chip panel: a rounded slab centred on +chipReliefHalf so its front face sits at chipFrontZ — proud of
            // the backing plate at every tier (never flush/coplanar), just shallower when held/disabled.
            _ = builder
                .ResetPoint()
                .TransformDynamic(slot: m_slot)
                .Translate(offset: new Vector3(x: cx, y: cy, z: chipReliefHalf))
                .RoundedRectangle(halfWidth: half, halfHeight: half, cornerRadius: (half * ChipCornerFraction), lift: SdfLift.Extrude, liftAmount: chipReliefHalf, material: chipMaterial);

            // The label embossed proud of the chip's OWN face (never coplanar — the glyph plane sits AT chipFrontZ and
            // the extrude straddles it, so the letters protrude with real relief and free bevel lighting from the dual).
            EmitLabel(builder: builder, text: ButtonLabel(glyph: slot.Glyph), centreX: cx, centreY: cy, chipHalf: half, frontZ: chipFrontZ, material: chipLabelMaterial);
        }

        // The hub mode readout: when the mirrored bar is a hub page, emboss the highlighted mode's name above the
        // cluster, riding the same dynamic slot — set in the kerned UI grotesque (UiFontMedium) rather than the
        // terminal mono voice, since this reads as a proud signage title, not console text.
        if (HubTitle(activePageId: frame.ActivePageId) is { } title) {
            EmitGlyphRun(builder: builder, text: title, centreX: (0.5f * (minX + maxX)), centreY: ((maxY + BackingPadding) + TitleTopGap), worldEmHeight: TitleEmHeight, frontZ: ChipFrontZ, material: labelMaterial, font: m_uiFontMedium);
        }

        _ = builder.EndInstance();
    }

    // The hub mode readout string for a bar page id: "HUB <LABEL>" when the mirrored bar is a hub page ("hub-<id>"),
    // else null. Translates the id back to the registry LABEL so the embossed name matches the hub tile and the console
    // echo (the id "creator" reads as its label "SCULPT").
    private static string? HubTitle(string activePageId) {
        const string prefix = "hub-";

        return (activePageId.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)
            ? $"HUB {AuthoringModeRegistry.LabelForId(id: activePageId[prefix.Length..])}"
            : null);
    }

    // The embossed nameplate on the diegetic terminal: a static world-space instance carrying the sign's letters,
    // centred on the live terminal face and embossed proud of it (the glyph plane sits AT the face, the extrude
    // straddles — never coplanar, and the analytic normal gives the letters free bevel lighting). Uses SdfProgramBuilder.Text
    // (static world text — its own ResetPoint is correct here, unlike the camera-rig bar) so the same shared atlas and
    // TextLayout metrics drive it. Set in the kerned UI grotesque (UiFontMedium) — signage reads better as UI type than
    // the terminal's mono voice — sampling the SAME shared image m_worldAtlas already uploads (SharedGlyphAtlas bakes
    // every font into ONE combined PNG), so no second texture upload is needed. No-op until the terminal frame is
    // wired or with no atlas built.
    private void EmitNameplate(SdfProgramBuilder builder) {
        if ((m_uiFontMedium is not { } font) || (m_nameplateCentre is not { } centreSource) || (m_nameplateRight is not { } rightSource) || (m_nameplateUp is not { } upSource)) {
            return;
        }

        var centre = centreSource();
        var right = Vector3.Normalize(value: rightSource());
        var up = Vector3.Normalize(value: upSource());
        var layout = m_textLayout.Layout(atlas: font, text: NameplateText, scale: NameplateEmHeight);
        // Text lays out from the baseline-left pen; shift left by half the run and down by half the cap height so the
        // sign sits centred on the terminal face.
        var origin = ((centre - (right * (0.5f * layout.Width))) - (up * ((0.5f * font.Metrics.Ascender) * NameplateEmHeight)));
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: NameplateAlbedo, Emissive: NameplateEmissive));

        _ = builder.BeginInstance(boundCenter: centre, boundRadius: NameplateBoundRadius);
        _ = builder.Text(atlas: font, text: NameplateText, origin: origin, right: right, up: up, worldEmHeight: NameplateEmHeight, material: material, blend: SdfBlendOp.Union, extrudeHalfDepth: NameplateReliefHalf);
        _ = builder.EndInstance();
    }

    // A button label rides at the chip-half-derived em, embossed proud of ITS OWN chip's face (frontZ tracks the
    // chip's tier-dependent relief — see the Emit loop); delegates to the shared glyph-run emitter, in the terminal
    // mono voice (the physical badge letter, not UI signage).
    private void EmitLabel(SdfProgramBuilder builder, string text, float centreX, float centreY, float chipHalf, float frontZ, int material) =>
        EmitGlyphRun(builder: builder, text: text, centreX: centreX, centreY: centreY, worldEmHeight: (chipHalf * LabelEmFraction), frontZ: frontZ, material: material, font: m_font);

    // Emits one glyph run in bar-local space, riding the dynamic slot. Mirrors SdfProgramBuilder.Text's per-glyph math
    // but PREFIXES each cell with the dynamic transform (Text's own ResetPoint would wipe it), so the letters ride the
    // camera-rig mount with the rest of the bar. Uses Puck.Text's TextLayout (identical glyph metrics to the shared
    // atlas the overlay samples) so the diegetic bar is literally the overlay's layout in world. Shared by the per-chip
    // button labels (the terminal mono voice) and the hub mode readout (the kerned UI grotesque) — the FONT is a
    // parameter rather than always m_font because the world-glyph atlas TEXTURE is one shared image across every font
    // (SharedGlyphAtlas bakes them together), so any FontAtlas view lays out kerned placements against the SAME
    // uploaded texture with no extra upload.
    private void EmitGlyphRun(SdfProgramBuilder builder, string text, float centreX, float centreY, float worldEmHeight, float frontZ, int material, FontAtlas? font) {
        if ((font is null) || (text.Length == 0)) {
            return;
        }

        var worldPerTexel = (worldEmHeight / font.Size);
        var distanceScale = (font.DistanceRange * worldPerTexel);
        var atlasWidth = (float)font.Width;
        var atlasHeight = (float)font.Height;
        var layout = m_textLayout.Layout(atlas: font, text: text, scale: worldEmHeight);
        // Centre the run: the layout runs y-up with the baseline at y = 0, so shift left by half the run width and up by
        // half the cap height to sit the glyphs centred on (centreX, centreY).
        var originX = (centreX - (0.5f * layout.Width));
        var originY = (centreY - ((0.5f * font.Metrics.Ascender) * worldEmHeight));

        foreach (var placement in layout.Placements) {
            var atlasBounds = placement.AtlasBounds;
            var planeBounds = placement.PlaneBounds;
            var halfWidth = ((0.5f * (atlasBounds.Right - atlasBounds.Left)) * worldPerTexel);
            var halfHeight = ((0.5f * (atlasBounds.Bottom - atlasBounds.Top)) * worldPerTexel);
            var glyphX = (originX + (0.5f * (planeBounds.Left + planeBounds.Right)));
            var glyphY = (originY + (0.5f * (planeBounds.Bottom + planeBounds.Top)));
            var uvBottomLeft = new Vector2(x: (atlasBounds.Left / atlasWidth), y: (atlasBounds.Bottom / atlasHeight));
            var uvTopRight = new Vector2(x: (atlasBounds.Right / atlasWidth), y: (atlasBounds.Top / atlasHeight));

            _ = builder
                .ResetPoint()
                .TransformDynamic(slot: m_slot)
                .Translate(offset: new Vector3(x: glyphX, y: glyphY, z: frontZ))
                .Glyph(uvBottomLeft: uvBottomLeft, uvTopRight: uvTopRight, halfWidth: halfWidth, halfHeight: halfHeight, extrudeHalfDepth: LabelReliefHalf, distanceScale: distanceScale, material: material, blend: SdfBlendOp.Union);
        }
    }

    // The chip's bar-local centre (world units): BindingBarLayout works in aspect units (x in [0, aspect], y-down from
    // the top) around the bottom-centre anchor; subtracting the anchor and flipping y yields a plane centred on the
    // mount, scaled to world metres. The anchor's x is aspect*0.5 and every layout x offset is aspect-independent, so
    // aspect = 1 gives the same relative cluster the overlay draws.
    private static (float X, float Y) LocalCentre(BindingSlotPlacement placement, Vector2 anchor) =>
        (((placement.Center.X - anchor.X) * WorldScale), ((anchor.Y - placement.Center.Y) * WorldScale));

    // The short ASCII label for a physical-button badge — the diegetic mirror of the overlay's gamepad-glyph sprite.
    // At most MaxLabelChars characters (the worst-case probe reserves that many glyph cells per chip).
    private static string ButtonLabel(BindingGlyphId glyph) =>
        glyph switch {
            BindingGlyphId.ArrowUp => "^",
            BindingGlyphId.ArrowRight => ">",
            BindingGlyphId.ArrowDown => "v",
            BindingGlyphId.ArrowLeft => "<",
            BindingGlyphId.ShapeTriangle => "^",
            BindingGlyphId.ShapeCircle => "O",
            BindingGlyphId.ShapeCross => "X",
            BindingGlyphId.ShapeSquare => "#",
            BindingGlyphId.LetterA => "A",
            BindingGlyphId.LetterB => "B",
            BindingGlyphId.LetterX => "X",
            BindingGlyphId.LetterY => "Y",
            BindingGlyphId.BumperLeft => "LB",
            BindingGlyphId.BumperRight => "RB",
            BindingGlyphId.TriggerLeft => "LT",
            BindingGlyphId.TriggerRight => "RT",
            BindingGlyphId.StickLeft => "LS",
            BindingGlyphId.StickRight => "RS",
            _ => "",
        };
}
