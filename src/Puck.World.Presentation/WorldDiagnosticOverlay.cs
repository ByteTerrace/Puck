using System.Numerics;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.Assets;
using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// Draws the published diagnostic copy (<see cref="WorldPresentationFeed.Diagnostics"/>) over the first seat's view: a pin
/// per finding the report holds, its occlusion sampled one finding a round trip, and the report a capture of the frame
/// writes beside it. It projects the report the collect last made and never starts one, so it reads no authority value;
/// a consumer of a retained scene, never a producer of simulation or lighting input.
/// </summary>
/// <param name="feed">The published values whose diagnostic copy the overlay draws.</param>
/// <param name="viewports">The seats' viewports, the first of which the pins are projected into.</param>
/// <param name="probe">The render probe holding the world residency whose frame the pins are drawn over.</param>
/// <param name="graphs">The view graph host whose pickers sample a pin's occlusion.</param>
public sealed class WorldDiagnosticOverlay(WorldPresentationFeed feed, WorldSeatViewports viewports,
    WorldRenderProbe probe, WorldViewGraphHost graphs) : IDiagnosticOverlaySource {
    private SdfWorldPicker? m_sampler;
    private long m_request;
    private string? m_requestedFinding;

    private long m_meshRevision = -1;
    private readonly Dictionary<string, (bool Hidden, float Delta)> m_depth = new(comparer: StringComparer.Ordinal);

    private int m_nextSample;
    private Puck.Abstractions.Cameras.CameraSnapshot? m_camera;

    /// <summary>Gets the overlay frame the latest snapshot drew, or <see langword="null"/> while no diagnostic view is
    /// drawn.</summary>
    public DiagnosticOverlayFrame? Retained { get; private set; }
    /// <summary>Gets the report the latest snapshot drew, each finding marked with its visibility in the first seat's
    /// view, or <see langword="null"/> while no diagnostic view is drawn.</summary>
    public WorldDiagnosticReport? RetainedReport { get; private set; }

    /// <summary>Returns whether a capture of the frame can be served: no diagnostic view is drawn, or the overlay has drawn
    /// a report and the scene's beauty node can read its presented surface.</summary>
    /// <param name="runtime">The render graph runtime.</param>
    /// <param name="scene">The scene instance the capture reads.</param>
    /// <returns><see langword="true"/> when the capture can be served.</returns>
    public bool CaptureReady(RenderGraphRuntime runtime, string scene) =>
        (!feed.Diagnostics.Drawn || ((RetainedReport is not null) &&
        (runtime.NodeOf(instance: scene) is { } node) && node.CanReadPresented(image: node.PublishedSurface)));
    /// <summary>Returns what a capture of the frame writes beside its image while a diagnostic view is drawn: the raw
    /// beauty frame and the drawn report, each finding naming the frame.</summary>
    /// <param name="runtime">The render graph runtime.</param>
    /// <param name="scene">The scene instance the capture reads.</param>
    /// <returns>The companion writer, given the capture's path, or <see langword="null"/> while no view is drawn.</returns>
    /// <exception cref="InvalidOperationException">The scene has no retained beauty node, or its frame is not ready.</exception>
    public Action<string>? CaptureCompanion(RenderGraphRuntime runtime, string scene) {
        if ((RetainedReport is not { } report) || !feed.Diagnostics.Drawn) { return null; }
        var node = (runtime.NodeOf(instance: scene) ?? throw new InvalidOperationException(message: "The diagnostic frame has no retained beauty node."));
        var image = node.PublishedSurface;

        if (!node.TryReadPresented(image: image, layout: node.PublishedLayout, rgba: out var raw)) {
            throw new InvalidOperationException(message: "The retained beauty frame is not ready for its diagnostic capture.");
        }
        var width = ((int)image.Width);
        var height = ((int)image.Height);
        var json = (report with {
            Findings = report.Findings.Select(selector: f => f with {
                Identity = f.Identity with { Frame = ((long)node.FrameCounter) },
            }).ToArray(),
        }).ToJson();

        return path => {
            var stem = Path.Combine(path1: (Path.GetDirectoryName(path: path) ?? "."), path2: Path.GetFileNameWithoutExtension(path: path));

            PngEncoder.Write(height: height, path: (stem + ".raw.png"), rgba: raw, width: width);
            File.WriteAllText(contents: json, path: (stem + ".report.json"));
        };
    }
    /// <inheritdoc/>
    public DiagnosticOverlayFrame? Snapshot() {
        var copy = feed.Diagnostics;

        if (!copy.Drawn || (copy.Report is null) || (probe.Residency?.Frame is not { } frame)) {
            Retained = null;
            RetainedReport = null;
            return null;
        }
        // Apart from the check, since the projection's closure is made on entry to the method holding it, and every
        // frame with the view off returns above.
        return Snapshot(copy: copy, frame: frame);
    }

    private DiagnosticOverlayFrame? Snapshot(WorldDiagnosticCopy copy, SdfFrame frame) {
        var view = viewports.Seat(slot: 0);

        if (!view.Present) { return null; }
        var report = copy.Report!;
        var instance = view.RenderInstance;

        if (instance is null) { return null; }
        m_sampler = graphs.Pickers?.DiagnosticPickerOf(instance: instance);
        if ((frame.DrawRowsRevision != m_meshRevision) || (m_camera != view.Camera)) {
            m_depth.Clear();
            m_meshRevision = frame.DrawRowsRevision;
            m_camera = view.Camera;
        }
        if ((m_sampler?.Result is { } answer) && (answer.Request == m_request) && (m_requestedFinding is { } id) &&
            (answer.MeshRevision == frame.DrawRowsRevision) && (copy.Find(id: id) is { } sampled) && (answer.Sample is { } sample) && (sample.Camera == view.Camera)) {
            var witnessDistance = Vector3.Distance(value1: sample.Camera.Position, value2: (sampled.PinAnchor ?? sampled.Witness));
            var difference = (witnessDistance - answer.Distance);

            m_depth[id] = ((answer.Hit && (difference > sampled.Budget.Total)), Math.Max(val1: 0, val2: difference));
            m_requestedFinding = null;
        }
        var region = view.Region;
        var viewport = new Vector4(region.X, region.Y, region.Width, region.Height);

        Vector2 Project(Vector3 point, out bool offscreen) {
            var front = view.Camera.TryProject(depth: out _, ndc: out var ndc, world: point);

            if (!front) {
                var direction = (point - view.Camera.Position);

                ndc = new Vector2(x: Vector3.Dot(vector1: direction, vector2: view.Camera.Right), y: -Vector3.Dot(vector1: direction, vector2: view.Camera.Up));
                if (ndc.LengthSquared() < 0.0001f) { ndc = new Vector2(x: 0, y: 2); }
                ndc = (Vector2.Normalize(value: ndc) * 2);
            }
            offscreen = (!front || (Math.Abs(value: ndc.X) > 1) || (Math.Abs(value: ndc.Y) > 1));
            return new Vector2(x: (region.X + (((ndc.X + 1) * 0.5f) * region.Width)), y: (region.Y + (((1 - ndc.Y) * 0.5f) * region.Height)));
        }
        var pins = new List<DiagnosticOverlayPin>();
        var protectedRegions = new List<Vector4>();
        var visibleFindings = new List<WorldDiagnosticFinding>();

        foreach (var finding in report.Findings.Where(predicate: f => (!f.Resolved && ((f.Severity == "red") || (copy.Amber && (f.Severity == "amber")))))) {
            var anchor = Project((finding.PinAnchor ?? finding.Witness), out var offscreen);
            var low = anchor;
            var high = anchor;

            if (view.Camera.TryProject(finding.Witness, out _, out _)) {
                var witness = Project(finding.Witness, out _);

                low = Vector2.Min(value1: low, value2: witness);
                high = Vector2.Max(value1: high, value2: witness);
            }
            // A finite witness audit flags the measured locus, not every pixel of the containing wall or room.
            // Protect both ends of the discrepancy and their moat. A whole-room envelope would erase every pin.
            var comparison = (finding.SurfaceWitness ?? (finding.Witness - (finding.Normal * ((float)(finding.Observed ?? 0)))));

            if (!offscreen && view.Camera.TryProject(depth: out _, ndc: out _, world: comparison)) {
                var end = Project(offscreen: out _, point: comparison);

                low = Vector2.Min(value1: low, value2: end);
                high = Vector2.Max(value1: high, value2: end);
            }
            var pad = new Vector2(x: (4f / view.Width), y: (4f / view.Height));
            var evidence = new Vector4(w: ((high.Y - low.Y) + (2 * pad.Y)), x: (low.X - pad.X), y: (low.Y - pad.Y), z: ((high.X - low.X) + (2 * pad.X)));

            if ((evidence.X < (region.X + region.Width)) && (evidence.Y < (region.Y + region.Height)) &&
                ((evidence.X + evidence.Z) > region.X) && ((evidence.Y + evidence.W) > region.Y)) { protectedRegions.Add(item: evidence); }
            var depth = m_depth.GetValueOrDefault(key: finding.Id);
            Vector2? caliper = ((finding.SurfaceWitness is { } surface) ? Project(offscreen: out _, point: surface) : null);
            var normalDirection = ((copy.Mode == "contacts") ? (Project((finding.Witness + (finding.Normal * 0.2f)), out _) - Project(finding.Witness, out _)) : Vector2.Zero);

            pins.Add(item: new(finding.Id, anchor, evidence, (finding.Severity == "amber"), depth.Hidden, offscreen, depth.Delta, caliper, normalDirection));
            visibleFindings.Add(item: finding with {
                Visibility = (offscreen ? "off-screen" : (depth.Hidden ? "occluded" : (m_depth.ContainsKey(key: finding.Id) ? "in-view" : "unknown"))),
                Identity = finding.Identity with { Instance = instance },
            });
        }
        // One cluster witness per asynchronous round trip; pointer/console picking owns a separate sampler.
        var inView = pins.Where(predicate: p => !p.Offscreen).ToArray();

        if ((m_sampler is { InFlight: false, Pending: false }) && (inView.Length > 0)) {
            var pin = inView[(m_nextSample++ % inView.Length)];
            var x = ((pin.Anchor.X - region.X) / region.Width);
            var y = ((pin.Anchor.Y - region.Y) / region.Height);

            m_request = m_sampler.Request(Math.Clamp(max: 0.999999f, min: 0, value: x), Math.Clamp(max: 0.999999f, min: 0, value: y), surface: true);
            m_requestedFinding = pin.Id;
        }
        RetainedReport = report with { Findings = visibleFindings.Concat(second: report.Findings.Where(predicate: f => ((f.Severity == "unknown") || f.Resolved || ((f.Severity == "amber") && !copy.Amber)))).ToArray() };
        Retained = new(pins.OrderBy(keySelector: p => p.Amber).ThenBy(keySelector: p => p.Offscreen).ToArray(), protectedRegions, viewport, report.Summary(), (copy.Presentation == "focus"), (copy.Presentation == "pins-only"));
        return Retained;
    }
}
