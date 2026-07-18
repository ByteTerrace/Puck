#:project ../../Puck.Overlays/Puck.Overlays.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
// overlay-envelope — the UIE-8 record-envelope proof, one .NET 10 file-based app:
//
//   dotnet run src/Puck.World/scripts/overlay-envelope.cs
//
// Drives Puck.Overlays' OverlayFrameBuilder contract in-process at its DECLARED maxima (no GPU, no window):
//   (a) saturation — writes past every capacity and asserts each drop is COUNTED (never silent) while the packed
//       counts pin at the capacity minus the tail reservation;
//   (b) the tail-reservation guarantee — with console/bar/HUD-class writers saturating the frame, the REAL
//       ToastWriter still lands its whole record shape after ReleaseTail (writer order can never starve the toast);
//   (c) the clip contract at the writer level — the REAL EditorHudWriter emits four clip-scoped seat HUDs with zero
//       drops, and clip-table overflow DROPS records (counted) instead of letting them bleed unclipped.
//
// The pixel half of the envelope story (records actually confined to seat viewports) is editor-edit's narrow
// four-seat session; this proof owns the CPU-side capacity/priority policy.
using Puck.Compositing;
using Puck.Overlays;

var failures = 0;

void Check(string name, bool ok, string detail) {
    Console.WriteLine($"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

    if (!ok) {
        failures++;
    }
}

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));
var fontsDirectory = Path.Combine(repoRoot, "src", "Puck.Overlays", "Assets", "Fonts");

if (!Directory.Exists(fontsDirectory)) {
    // Fallback for a relocated bin layout: resolve from the current directory (proof runs land at the repo root).
    fontsDirectory = Path.GetFullPath(Path.Combine("src", "Puck.Overlays", "Assets", "Fonts"));
}

var glyphs = new OverlayGlyphAtlasSet(fontsDirectory: fontsDirectory).LoadOverlayPack();

if (glyphs is null) {
    Console.Error.WriteLine($"[proof] FAIL: no usable glyph atlas under '{fontsDirectory}'");

    return 1;
}

var builder = new OverlayFrameBuilder(glyphs: glyphs, width: 1280, height: 800);

// ---- (a) saturation: every capacity drop is counted, the tail reservation holds capacity back -------------------
Console.WriteLine("[proof] === overlay-envelope (a): counted overflow under saturation ===");
builder.BeginFrame();
builder.ReserveTail(panels: 1, elements: 4, textWords: 46);

for (var i = 0; (i < (OverlayFrameBuilder.MaxPanels + 2)); i++) {
    builder.WritePanel(x: 8, y: (8 + (i * 20)), w: 200, h: 16, titleBand: false, bandHeight: 0f, style: OverlayPanelStyle.Strip, ringRole: null, alpha: 1f);
}

// Text saturation FIRST (while element capacity remains): 64-char runs until the shared word budget (minus the
// tail reservation) refuses one.
var refusedTextRun = false;

for (var i = 0; (i < ((OverlayFrameBuilder.TextWordCapacity / 64) + 2)); i++) {
    var dropsBefore = builder.DroppedTextWords;

    builder.WriteText(x: 4, y: 4, text: new string('m', 64), cellHeight: 18, role: OverlayColorRole.TextPrimary, alpha: 1f);
    refusedTextRun |= (builder.DroppedTextWords > dropsBefore);
}

for (var i = 0; (i < OverlayFrameBuilder.MaxElements); i++) {
    builder.WriteRect(x: 4, y: 4, w: 8, h: 8, role: OverlayColorRole.Accent, radius: 1f, alpha: 1f);
}

Check(
    name: "panels-pin-at-reserved-capacity",
    ok: ((builder.PanelCount == (OverlayFrameBuilder.MaxPanels - 1)) && (builder.DroppedPanels == 3)),
    detail: $"packed {builder.PanelCount}/{OverlayFrameBuilder.MaxPanels} (1 reserved), dropped {builder.DroppedPanels}"
);
Check(
    name: "elements-pin-at-reserved-capacity",
    ok: ((builder.ElementCount <= (OverlayFrameBuilder.MaxElements - 4)) && (builder.DroppedElements > 0)),
    detail: $"packed {builder.ElementCount}/{OverlayFrameBuilder.MaxElements} (4 reserved), dropped {builder.DroppedElements}"
);
Check(
    name: "text-words-pin-at-reserved-capacity",
    ok: (refusedTextRun && (builder.DroppedTextWords > 0)),
    detail: $"dropped {builder.DroppedTextWords} glyph words at the shared budget"
);
Check(name: "overflow-is-observable", ok: builder.HasOverflow, detail: "HasOverflow reports the saturated frame");

// ---- (b) the tail reservation: the LAST writer (the toast) always lands whole --------------------------------
Console.WriteLine("[proof] === overlay-envelope (b): the toast can never be starved by writer order ===");

var toasts = new OverlayToastStore();

toasts.Publish(message: "UpsertSceneRow scene 'boulder-1' rejected: capacity", isError: true);

var toastWriter = new ToastWriter(source: toasts);
var panelsBeforeToast = builder.PanelCount;
var elementsBeforeToast = builder.ElementCount;
var panelDropsBeforeToast = builder.DroppedPanels;
var elementDropsBeforeToast = builder.DroppedElements;
var textDropsBeforeToast = builder.DroppedTextWords;

builder.ReleaseTail();
toastWriter.Emit(builder: builder, renderTicks: 0UL);
Check(
    name: "toast-lands-whole-in-a-saturated-frame",
    ok: ((builder.PanelCount == (panelsBeforeToast + 1)) &&
        (builder.ElementCount == (elementsBeforeToast + 4)) &&
        (builder.DroppedPanels == panelDropsBeforeToast) &&
        (builder.DroppedElements == elementDropsBeforeToast) &&
        (builder.DroppedTextWords == textDropsBeforeToast)),
    detail: $"toast added 1 panel + {(builder.ElementCount - elementsBeforeToast)} elements with zero new drops"
);

// ---- (c) the clip contract at the writer level ----------------------------------------------------------------
Console.WriteLine("[proof] === overlay-envelope (c): four clip-scoped seat HUDs, and clip overflow drops (never bleeds) ===");

var hudStore = new EditorHudStore();
var seats = new OverlayEditorSeat[4];

for (var seat = 0; (seat < 4); seat++) {
    seats[seat] = new OverlayEditorSeat(
        Viewport: new NormalizedRect(X: (0.5f * (seat % 2)), Y: (0.5f * (seat / 2)), Width: 0.5f, Height: 0.5f),
        SelectionLine: $"sel spawns 'seat-{seat + 1}' (12.0, 0.0, -3.5)",
        ContextLine: "rows 12 | snap on 0.5",
        SessionLine: "act live (save folds) | drift render",
        DragLine: $"scene 'boulder-{seat + 1}' at (1.00, 0.72, -0.30)",
        DragActive: true
    );
}

hudStore.Publish(frame: new OverlayEditorHudFrame(Seats: seats));

var hudWriter = new EditorHudWriter(source: hudStore);

builder.BeginFrame();
hudWriter.Emit(builder: builder);
Check(
    name: "four-seat-hud-fits-with-clips",
    ok: ((builder.PanelCount == 4) && (builder.ElementCount == 20) && !builder.HasOverflow && (builder.DroppedClips == 0)),
    detail: $"4 panels + {builder.ElementCount} elements (title + 4 lines per seat), zero drops"
);

builder.BeginFrame();

for (var i = 0; (i < (OverlayFrameBuilder.MaxClips + 2)); i++) {
    builder.BeginClip(x: (i * 10), y: 0, w: 100, h: 100);
    builder.WriteRect(x: (i * 10), y: 4, w: 8, h: 8, role: OverlayColorRole.Accent, radius: 1f, alpha: 1f);
    builder.EndClip();
}

Check(
    name: "clip-overflow-drops-instead-of-bleeding",
    ok: ((builder.DroppedClips == 2) && (builder.ElementCount == OverlayFrameBuilder.MaxClips) && (builder.DroppedElements == 2)),
    detail: $"clips dropped {builder.DroppedClips}, elements packed {builder.ElementCount} + dropped {builder.DroppedElements}"
);

Console.WriteLine($"[proof] overlay-envelope {((failures == 0) ? "PASS" : $"FAIL ({failures})")}");

return ((failures == 0) ? 0 : 1);
