using System.Numerics;

namespace Puck.Overlays;

/// <summary>A diagnostic pin and its conservative evidence footprint, in normalized frame coordinates.</summary>
/// <param name="Id">Finding id, shared with the report.</param>
/// <param name="Anchor">Projected witness.</param>
/// <param name="Evidence">Protected x, y, width, height.</param>
/// <param name="Amber">Whether the suspicion uses amber instead of red.</param>
/// <param name="Occluded">Whether depth proves the witness hidden.</param>
/// <param name="Offscreen">Whether the witness docks at the viewport edge.</param>
/// <param name="DepthDelta">Occluding depth difference, metres.</param>
/// <param name="CaliperEnd">Optional support caliper endpoint.</param>
/// <param name="NormalDirection">Projected contact normal, displayed outside the evidence.</param>
public sealed record DiagnosticOverlayPin(string Id, Vector2 Anchor, Vector4 Evidence, bool Amber, bool Occluded,
    bool Offscreen, float DepthDelta, Vector2? CaliperEnd = null, Vector2 NormalDirection = default);

/// <summary>One retained frame's diagnostic presentation.</summary>
/// <param name="Pins">Pins in severity order, including overflow for the banner.</param>
/// <param name="Protected">All evidence footprints, never only those whose pins fit.</param>
/// <param name="Viewport">The explicitly selected primary or isolated view.</param>
/// <param name="Summary">Counts and coverage summary.</param>
/// <param name="Focus">Whether non-evidence context is muted without changing luminance.</param>
/// <param name="PinsOnly">Whether exterior contours and calipers are hidden.</param>
public sealed record DiagnosticOverlayFrame(IReadOnlyList<DiagnosticOverlayPin> Pins, IReadOnlyList<Vector4> Protected,
    Vector4 Viewport, string Summary, bool Focus, bool PinsOnly);

/// <summary>The host supplies diagnostics captured for the frame being composed.</summary>
public interface IDiagnosticOverlaySource {
    /// <summary>Returns the frame's immutable diagnostic presentation, or null while off/pending.</summary>
    /// <returns>The retained frame.</returns>
    DiagnosticOverlayFrame? Snapshot();
}

public sealed partial class AnnotationWriter {
    /// <summary>The visible pin limit; overflow remains in the report.</summary>
    public const int MaxDiagnosticPins = 16;
    /// <summary>Maximum separately protected rectangles; overflow coalesces within spatial bins.</summary>
    public const int MaxDiagnosticRegions = 64;
    /// <summary>Independent element reservation including masks, contours, leaders and calipers.</summary>
    public const int MaxDiagnosticElements = MaxDiagnosticRegions + 1 + MaxDiagnosticPins * 18 + 2;
    /// <summary>Independent glyph reservation.</summary>
    public const int MaxDiagnosticTextWords = MaxDiagnosticPins * 40 + 160;

    /// <summary>Emits only the diagnostic channel, after scene composition.</summary>
    /// <param name="builder">The open diagnostic channel's builder.</param>
    /// <param name="source">The retained diagnostic frame.</param>
    public static void EmitDiagnostics(OverlayFrameBuilder builder, IDiagnosticOverlaySource source) {
        ArgumentNullException.ThrowIfNull(builder);
        var frame = source.Snapshot();
        if (frame is null) { return; }
        var dims = new Vector2(builder.Width, builder.Height);
        var moat = new Vector2(2) / dims;
        var regions = frame.Protected;
        if (regions.Count > MaxDiagnosticRegions) {
            // Eight-by-eight bins preserve the union without replacing a scattered set by one screen-sized box.
            var bins = new Dictionary<int, Vector4>();
            foreach (var r in regions) {
                var key = Math.Clamp((int)((r.X + r.Z * 0.5f) * 8), 0, 7) + 8 * Math.Clamp((int)((r.Y + r.W * 0.5f) * 8), 0, 7);
                if (bins.TryGetValue(key, out var previous)) {
                    var low = Vector2.Min(new(previous.X, previous.Y), new(r.X, r.Y));
                    var high = Vector2.Max(new(previous.X + previous.Z, previous.Y + previous.W), new(r.X + r.Z, r.Y + r.W));
                    bins[key] = new(low.X, low.Y, high.X - low.X, high.Y - low.Y);
                } else { bins[key] = r; }
            }
            regions = bins.Values.ToArray();
        }
        foreach (var r in regions) {
            builder.WriteDiagnosticMask(new(r.X - moat.X, r.Y - moat.Y, r.Z + 2 * moat.X, r.W + 2 * moat.Y), false);
        }
        if (frame.Focus) { builder.WriteDiagnosticMask(frame.Viewport, true); }
        var count = Math.Min(MaxDiagnosticPins, frame.Pins.Count);
        var viewport = frame.Viewport;
        for (var index = 0; index < count; index++) {
            var pin = frame.Pins[index];
            var role = pin.Amber ? OverlayColorRole.Warning : OverlayColorRole.Danger;
            var minimum = new Vector2(viewport.X, viewport.Y) + new Vector2(18) / dims;
            var maximum = Vector2.Max(minimum, new Vector2(viewport.X + viewport.Z, viewport.Y + viewport.W) - new Vector2(18) / dims);
            var badge = pin.Offscreen ? Vector2.Clamp(pin.Anchor, minimum, maximum)
                : new Vector2(minimum.X, Math.Clamp(minimum.Y + (38 + index * 25) / dims.Y, minimum.Y, maximum.Y));
            var radius = 7f;
            builder.WriteRing(badge.X * dims.X, badge.Y * dims.Y, radius, role, 1);
            if (!pin.Occluded) { builder.WriteRing(badge.X * dims.X, badge.Y * dims.Y, 2, role, 1); }
            var label = pin.Occluded ? $"{pin.Id} +{pin.DepthDelta:0.00}m behind" : pin.Id;
            builder.WriteText(badge.X * dims.X + 10, badge.Y * dims.Y - 6, label, 12, role, 1, maxChars: 40);
            if (pin.Offscreen) {
                var direction = Vector2.Normalize(pin.Anchor - new Vector2(viewport.X + viewport.Z / 2, viewport.Y + viewport.W / 2));
                var end = badge + direction * new Vector2(8) / dims;
                var perpendicular = new Vector2(-direction.Y, direction.X) * new Vector2(4) / dims;
                builder.WriteDiagnosticLine(badge + perpendicular, end, role);
                builder.WriteDiagnosticLine(badge - perpendicular, end, role);
            } else {
                // The final leader ends four pixels short; the evidence mask clips every earlier segment too.
                var delta = (pin.Anchor - badge) * dims;
                if (delta.LengthSquared() > 16) {
                    var end = pin.Anchor - Vector2.Normalize(delta) * new Vector2(4) / dims;
                    builder.WriteDiagnosticLine(badge, new Vector2(end.X, badge.Y), role);
                    builder.WriteDiagnosticLine(new Vector2(end.X, badge.Y), end, role);
                }
            }
            if (!frame.PinsOnly && !pin.Offscreen) {
                var r = pin.Evidence;
                var low = new Vector2(r.X, r.Y) - new Vector2(4) / dims;
                var high = new Vector2(r.X + r.Z, r.Y + r.W) + new Vector2(4) / dims;
                builder.WriteDiagnosticLine(low, new(high.X, low.Y), role);
                builder.WriteDiagnosticLine(new(low.X, high.Y), low, role);
                if (Math.Max(r.Z * dims.X, r.W * dims.Y) > 8) {
                    builder.WriteDiagnosticLine(new(high.X, low.Y), high, role);
                    builder.WriteDiagnosticLine(high, new(low.X, high.Y), role);
                }
                if (pin.CaliperEnd is { } support) {
                    var top = new Vector2(high.X + 6 / dims.X, pin.Anchor.Y);
                    var bottom = new Vector2(top.X, support.Y);
                    builder.WriteDiagnosticLine(top, bottom, role);
                    var tick = new Vector2(4 / dims.X, 0);
                    builder.WriteDiagnosticLine(bottom - tick, bottom + tick, role);
                    builder.WriteDiagnosticLine(top - tick, top + tick, role);
                }
                if (pin.NormalDirection.LengthSquared() > 0.000001f) {
                    var direction = Vector2.Normalize(pin.NormalDirection * dims);
                    var start = high + new Vector2(10) / dims;
                    var end = start + direction * new Vector2(18) / dims;
                    var wing = new Vector2(-direction.Y, direction.X) * new Vector2(3) / dims;
                    builder.WriteDiagnosticLine(start, end, role);
                    builder.WriteDiagnosticLine(end, end - direction * new Vector2(5) / dims + wing, role);
                    builder.WriteDiagnosticLine(end, end - direction * new Vector2(5) / dims - wing, role);
                }
            }
        }
        var banner = $"{frame.Summary} +{Math.Max(0, frame.Pins.Count - count)} in report";
        builder.WriteText(viewport.X * dims.X + 8, viewport.Y * dims.Y + 8, banner, 12, OverlayColorRole.TextPrimary, 1, maxChars: 160);
    }
}
