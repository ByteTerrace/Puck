using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>The read seams the unified overlay consumes, each optional (an absent source simply contributes no
/// records), bundled so the constructor arity stays small.</summary>
/// <param name="Console">The console-panel source, or <see langword="null"/>.</param>
/// <param name="BindingBar">The per-seat binding-bar source, or <see langword="null"/>.</param>
/// <param name="Toast">The transient-echo source, or <see langword="null"/>.</param>
/// <param name="FeedTick">Invoked once per produced frame, before the sources are snapshotted — the host's hook to
/// freshen pull-model feeds (e.g. recomposing the per-seat binding frame). Runs on the render thread.</param>
/// <param name="Markers">The per-seat marker source (projected chips for authored <c>markers</c> rows), or
/// <see langword="null"/>.</param>
/// <param name="Hud">The authored world-scope and player-scope (per-seat) HUD structure source, or
/// <see langword="null"/>.</param>
/// <param name="HudBindings">The authored HUD's live binding resolver — required alongside <paramref name="Hud"/>
/// for either scope to draw anything (a <see langword="null"/> pairing on either side draws nothing).</param>
/// <param name="Cursor">The per-seat drawn-cursor source, or <see langword="null"/>.</param>
/// <param name="Wheel">The per-seat radial-action-menu source, or <see langword="null"/>.</param>
public sealed record UnifiedOverlaySources(
    IConsoleTapeSource? Console,
    IBindingBarSource? BindingBar,
    IOverlayToastSource? Toast,
    Action? FeedTick,
    IMarkerSource? Markers = null,
    IHudSource? Hud = null,
    IHudBindingResolver? HudBindings = null,
    ICursorSource? Cursor = null,
    IWheelSource? Wheel = null
);
