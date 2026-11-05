using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World;

// Presentation-only analysis. The authoritative provider inputs are copied on the host thread; no result feeds a solve.
internal sealed class WorldDiagnostics(WorldServer server, WorldRenderProbe? render = null, WorldDefinitionSource? sourceDocument = null) {
    private const string UncorrespondedReason = "mesh correspondence uncertified: no nearest triangle within precision search radius or no matching scene";

    private readonly WorldDiagnosticTracker m_tracker = new();
    private readonly Dictionary<string, WorldDiagnosticReport> m_reports = new(comparer: StringComparer.Ordinal);

    private WorldDefinition? m_definition;
    private WorldSolidField? m_field;
    private WitnessCache? m_witnesses;

    private string m_fieldRefusal = "not evaluated";

    private long m_snapshot;

    private readonly Dictionary<string, string> m_logSignatures = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> m_logTicks = new(comparer: StringComparer.Ordinal);

    public string Mode { get; set; } = "off";
    public string Presentation { get; set; } = "default";
    public string Instance { get; set; } = "world";

    public bool Amber { get; set; }
    public IReadOnlyList<WorldDiagnosticCollider> Colliders => server.Population.DiagnosticColliders;
    public bool DrawnReady {
        get {
            if ((render?.Residency is not { Frame: { } frame } residency) || residency.MeshPathBake.Pending || !Equals(objA: frame.DiagnosticSource, objB: server.DocumentVersion)) { return false; }
            using var scene = residency.MeshIrradiance.DiagnosticScene(frame: frame);

            return (scene is not null);
        }
    }
    public WorldDiagnosticReport? Latest => m_reports.GetValueOrDefault(key: Mode);
    public long Revision => server.SolidRevision;
    public ulong Tick => (server.NextInputTick - 1);

    public WorldDiagnosticFinding? Find(string id) => m_reports.Values.SelectMany(selector: r => r.Findings).FirstOrDefault(predicate: f => (f.Id == id));
    public void Retain(WorldDiagnosticReport report) => m_reports[report.View] = report;
    /// <summary>Evaluates a view on demand.</summary>
    /// <param name="view">The view: <c>colliders</c> or <c>contacts</c>.</param>
    /// <param name="deadline">When the caller's answer is owed, or <see langword="null"/> for a caller that waits for the
    /// whole report. A colliders report whose witnesses are not all read by then answers a report that says how many
    /// colliders remain, keeps what it has read, and finishes on a later call; the report it finally gives is the one
    /// a single call would have.</param>
    /// <returns>The report, retained as the view's latest only when it is whole.</returns>
    public WorldDiagnosticReport Report(string view, TextAnswerDeadline? deadline = null) {
        var tick = (server.NextInputTick - 1);
        var snapshot = ++m_snapshot;
        var findings = new List<WorldDiagnosticFinding>();
        var skipped = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var comparisons = new Dictionary<string, WorldDiagnosticSampleCounts>(comparer: StringComparer.Ordinal);

        void Count(string comparison, bool? red) {
            var counts = (comparisons.GetValueOrDefault(key: comparison) ?? new(Clean: 0, Red: 0, Unknown: 0));

            comparisons[comparison] = red switch {
                true => counts with { Red = (counts.Red + 1) },
                false => counts with { Clean = (counts.Clean + 1) },
                null => counts with { Unknown = (counts.Unknown + 1) },
            };
        }
        var tested = 0;
        var eligible = 0;
        var evaluated = new HashSet<string>(comparer: StringComparer.Ordinal);
        var meshEvaluated = new HashSet<string>(comparer: StringComparer.Ordinal);
        var failed = new HashSet<string>(comparer: StringComparer.Ordinal);
        var meshFailed = new HashSet<string>(comparer: StringComparer.Ordinal);
        // Per collider whose witnesses found no retained triangle, how many and the first of them: the coverage finding
        // names each, so an unknown is a placement to look at rather than a count.
        var uncorresponded = new SortedDictionary<string, (int Count, Vector3 Witness)>(comparer: StringComparer.Ordinal);
        var frame = render?.Residency?.Frame;
        using var scene = (((frame is not null) && Equals(objA: frame.DiagnosticSource, objB: server.DocumentVersion) &&
            (render?.Residency?.MeshPathBake.Pending == false)) ? render?.Residency.MeshIrradiance.DiagnosticScene(frame: frame) : null);
        // A conservative scene-wide bound is still derived from the cells actually drawn, never a recorded allowance.
        var bakeError = ((scene is null) ? 0 : frame!.MeshDraws.Where(predicate: d => SdfMeshIrradianceScene.IsStatic(draw: d))
            .Select(selector: d => (Vector3.TransformNormal(d.Mesh.BakeCell, d.ObjectToWorld).Length() * 0.5)).DefaultIfEmpty(defaultValue: 0).Max());

        PrepareField();
        void Skip(string reason) => skipped[reason] = (skipped.GetValueOrDefault(key: reason) + 1);
        WorldDiagnosticIdentity Identity(string subject, string stamp, string creation, string node, string operation) =>
            new(snapshot, tick, null, Instance, subject, server.SolidRevision, stamp, creation, node, operation) {
                SourceFile = sourceDocument?.SourcePath.Replace(newChar: '/', oldChar: '\\'),
            };
        WorldDiagnosticFinding Unknown(string subject, string reason) => new("", view, "unknown", "coverage",
            "not-evaluated", reason, null, new(0, 0, 0), default, default, Identity(creation: "", node: "", operation: "", stamp: subject, subject: subject));

        if (view == "colliders") {
            if (WorldContactSelection.RequiresField(collision: server.Definition.Collision)) {
                Skip(reason: "field provider: analytic hull is not the effective collision surface");
            } else if (m_field is null) {
                Skip(reason: $"authored field unavailable: {m_fieldRefusal}");
            } else {
                var rows = server.Population.DiagnosticColliders;
                // Each collider's bounding radius, once: a witness beyond a neighbour's is neither inside it nor on its face.
                var bounds = rows.Select(selector: static row => WorldDiagnosticGeometry.Bound(collider: row.Collider)).ToArray();

                var cache = WitnessesFor(field: m_field, rows: rows);

                ReadWitnesses(bounds: bounds, cache: cache, deadline: deadline);

                if (cache.Pending > 0) {
                    var reason = $"report unfinished: {cache.Pending} of {rows.Count} colliders still to read; ask again";
                    var unfinished = new WorldDiagnosticCoverage(0, 0, new Dictionary<string, int> { [reason] = 1 }, false) { Pending = cache.Pending };

                    return new WorldDiagnosticReport(snapshot, view, tick, [Unknown(reason: reason, subject: "coverage") with { Source = reason, Coverage = unfinished }], unfinished);
                }

                var readings = cache.Readings;

                m_witnesses = null;
                for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
                    var row = rows[rowIndex];
                    var budget = new WorldDiagnosticBudget(row.Margin, 0, WorldDiagnosticGeometry.Numerical(collider: row.Collider)) {
                        ConservativeInset = row.ConservativeInset,
                    };
                    var source = $"analytic/{row.Collider.Kind}/{row.Node}";
                    var scope = $"{row.Stamp}|{source}";

                    evaluated.Add(item: scope);
                    foreach (var reading in readings[rowIndex]!) {
                        var point = reading.Point;

                        eligible++;
                        if (reading.Buried) {
                            tested++;
                            continue;
                        }
                        if (!reading.Answered) {
                            Skip(reason: "field does not answer witness");
                            failed.Add(item: scope);
                            continue;
                        }
                        tested++;
                        var gradient = reading.Gradient;
                        var observed = ((double)reading.Distance);

                        Count(comparison: "collision-versus-authored-field", red: (WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: observed) is not (null or "known-approximation")));
                        if (row.Opaque) {
                            meshEvaluated.Add(item: scope);
                            var normal = gradient.ToVector3();

                            if ((scene is not null) && (WorldDiagnosticMeshQuery.Measure(budget: budget, point: point, scene: scene, sceneBakeError: bakeError) is { } sample)) {
                                var drawn = sample.Surface;
                                var drawnNormal = sample.Normal;
                                var hitError = sample.Budget.BakeHalfDiagonal;
                                var enclosed = !WorldDiagnosticGeometry.OpensOnto(server.Population.DiagnosticColliders.Select(selector: static c => c.Collider),
                                    point, normal, hitError);

                                if (enclosed) {
                                    var counts = (comparisons.GetValueOrDefault(key: "collision-versus-baked-mesh") ?? new(Clean: 0, Red: 0, Unknown: 0));

                                    comparisons["collision-versus-baked-mesh"] = counts with { Enclosed = (counts.Enclosed + 1) };
                                } else {
                                    Count(comparison: "collision-versus-baked-mesh", red: sample.IsRed);
                                }
                                var meshBudget = budget with { BakeHalfDiagonal = hitError };
                                var separation = sample.Distance;

                                if (!enclosed && sample.IsRed) {
                                    findings.Add(item: new("", view, "red", source, "collision-versus-baked-mesh",
                                        "effective collision boundary agrees with retained opaque mesh within margin and bake precision",
                                        separation, meshBudget, point, normal,
                                        Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = render?.Root?.FramesProduced },
                                        Details: new Dictionary<string, string> { ["comparison"] = "collision-versus-baked-mesh", ["meshNormal"] = drawnNormal.ToString(), ["correspondence"] = "bounded nearest triangle; unsigned Euclidean distance" }) {
                                        MeshRevision = frame!.MeshDrawsRevision,
                                        SurfaceWitness = drawn,
                                    });
                                }
                                var fieldMeshBudget = new WorldDiagnosticBudget(0, 0, budget.Numerical, hitError);
                                var fieldAnswered = m_field.Probe(FixedVector3.FromVector3(value: drawn), out var fieldError, out _, out _);

                                Count(comparison: "field-versus-baked-mesh", red: (fieldAnswered ? (Math.Abs(value: ((double)fieldError)) > fieldMeshBudget.Total) : null));
                                if (!fieldAnswered) { Skip(reason: "authored field does not answer certified mesh witness"); meshFailed.Add(item: scope); }
                                if (fieldAnswered && (Math.Abs(value: ((double)fieldError)) > fieldMeshBudget.Total)) {
                                    findings.Add(item: new("", view, "red", source, "field-versus-baked-mesh",
                                        "retained mesh lies on authored field within the actual bake cell budget", ((double)fieldError),
                                        fieldMeshBudget, drawn, drawnNormal, Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = render?.Root?.FramesProduced }) {
                                        MeshRevision = frame!.MeshDrawsRevision,
                                    });
                                }
                            } else {
                                Count(comparison: "collision-versus-baked-mesh", red: null);
                                Count(comparison: "field-versus-baked-mesh", red: null);
                                Skip(reason: UncorrespondedReason);
                                meshFailed.Add(item: scope);
                                uncorresponded[scope] = (uncorresponded.TryGetValue(key: scope, value: out var seen) ? ((seen.Count + 1), seen.Witness) : (1, point));
                            }
                        }
                        if (WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: observed) is not { } contradiction) {
                            continue;
                        }
                        findings.Add(item: new WorldDiagnosticFinding("", view, ((contradiction == "known-approximation") ? "amber" : "red"), source,
                            contradiction,
                            ((contradiction == "known-approximation") ? "conservative collider lies inside the authored solid within its computed shape bound" :
                            "effective collision boundary agrees with authored solid within collision margin and arithmetic uncertainty"),
                            observed, budget, point, gradient.ToVector3(),
                            Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation),
                            Details: new Dictionary<string, string> {
                                ["comparison"] = "collision-versus-authored-field",
                                ["colliderCenter"] = row.Collider.Center.ToVector3().ToString(),
                                ["colliderExtent"] = row.Collider.Extent.ToVector3().ToString(),
                                ["colliderRadius"] = ((double)row.Collider.Radius).ToString(format: "R", provider: CultureInfo.InvariantCulture),
                                ["colliderRotation"] = row.Collider.Rotation.ToString(),
                                ["sampling"] = "finite boundary witnesses; not a complete surface proof",
                            }));
                    }
                }
            }
            if (scene is null) { Skip(reason: "field-versus-baked-mesh requires a settled matching rendered mesh revision and its trace scene"); }
            foreach (var placement in server.Definition.Placements.Where(predicate: p => ((p.Solid is not null) && (p.Attach is not null)))) {
                Skip(reason: $"attached pose not retained: {placement.Id}");
            }
        } else if (view == "contacts") {
            for (var index = 0; (index < server.Population.Capacity); index++) {
                if (!server.Population.IsActive(index: index) || (server.Population.EntryBody(index: index) is not { Grounded: true } body)) {
                    continue;
                }
                eligible++;
                if (body.DiagnosticSupport is not { } support) {
                    Skip(reason: $"body:{index}: solver support witness unavailable");
                    continue;
                }
                if (m_field is null) {
                    Skip(reason: $"body:{index}: authored surface query unavailable");
                    continue;
                }
                var normal = support.Push.Normal.ToVector3();

                if (support.Source != FixedSurfaceColliderSource.Static) {
                    Skip(reason: $"body:{index}: dynamic support has no retained authored/rendered pose pair");
                    continue;
                }
                var foot = support.BodyPoint;

                if (!FixedSurfaceQuery.TryNearest([support.Collider], [], in foot, FixedQ4816.MaxValue, out var surface) ||
                    !m_field.Probe(surface.Point, out var distance, out _, out var gradient)) {
                    Skip(reason: $"body:{index}: support surface query unavailable");
                    continue;
                }
                tested++;
                var budget = new WorldDiagnosticBudget(0, server.Definition.Collision.ContactSkin, WorldDiagnosticGeometry.Numerical(collider: support.Collider));
                var gap = ((double)FixedVector3.Dot(left: (foot - surface.Point), right: support.Push.Normal));
                var row = (((support.Source == FixedSurfaceColliderSource.Static) && (support.ColliderIndex < server.Population.DiagnosticColliders.Count))
                    ? server.Population.DiagnosticColliders[support.ColliderIndex] : null);

                if ((row is null) || (row.Collider != support.Collider)) {
                    Skip(reason: $"body:{index}: retained support collider predates the current collision revision");
                    continue;
                }
                var identity = Identity($"body:{index}", (row?.Stamp ?? "attached"), (row?.Creation ?? ""), (row?.Node ?? ""), (row?.Operation ?? ""));

                evaluated.Add(item: $"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                var details = new Dictionary<string, string> {
                    ["supportPoint"] = surface.Point.ToVector3().ToString(),
                    ["bodySupport"] = foot.ToVector3().ToString(),
                    ["normal"] = normal.ToString(),
                    ["contactSkin"] = budget.ContactSkin.ToString(provider: CultureInfo.InvariantCulture),
                    ["snapReachInformationOnly"] = body.DiagnosticSnapReach.ToString(provider: CultureInfo.InvariantCulture),
                    ["provider"] = $"analytic/{support.Collider.Kind}",
                    ["comparison"] = "solver-versus-authored-field; rendered mesh not evaluated",
                };

                if ((gap > budget.Total) && body.DiagnosticSettled) {
                    findings.Add(item: new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "hovering",
                        "grounded support clearance <= contact skin plus numerical error", gap, budget, foot.ToVector3(), normal,
                        identity, Details: details) { SurfaceWitness = surface.Point.ToVector3() });
                }
                var fieldBudget = budget with { CollisionMargin = (row?.Margin ?? 0) };

                if (((double)distance) > fieldBudget.Total) {
                    findings.Add(item: new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "grounded-on-authored-air",
                        "actual solver support lies on the authored solid within the support budget", ((double)distance),
                        fieldBudget, surface.Point.ToVector3(), gradient.ToVector3(), identity, Details: details));
                }
                if ((scene is not null) && (row?.Opaque == true) && (frame!.Clock.Whole == server.CompletedEngineTicks) && (frame.Clock.Fraction == 0)) {
                    meshEvaluated.Add(item: $"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                    var footPoint = foot.ToVector3();

                    if ((WorldDiagnosticMeshQuery.Measure(scene, surface.Point.ToVector3(), fieldBudget, bakeError) is { } sample) &&
                        (FixedVector3.Dot(left: FixedVector3.FromVector3(value: sample.Normal), right: support.Up) >= support.WalkableThreshold)) {
                        var drawnBudget = sample.Budget;
                        var drawnGap = Vector3.Dot(vector1: (footPoint - sample.Surface), vector2: normal);

                        Count(comparison: "solver-versus-baked-mesh", red: (drawnGap > drawnBudget.Total));
                        details["comparison"] = "solver-versus-baked-mesh";
                        details["correspondence"] = "bounded nearest triangle at retained solver support";
                        if (drawnGap > drawnBudget.Total) {
                            findings.Add(item: new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "grounded-on-air",
                                "actual grounded support has a walkable retained opaque surface within its precision budget",
                                drawnGap, drawnBudget, footPoint, normal, identity with { Frame = render?.Root?.FramesProduced }, Details: details) {
                                MeshRevision = frame!.MeshDrawsRevision,
                                SurfaceWitness = sample.Surface,
                            });
                        }
                        if ((drawnGap > drawnBudget.Total) && body.DiagnosticSettled) {
                            findings.Add(item: new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "hovering-over-drawn-surface",
                                "settled support gap <= contact skin plus collision and bake precision; snap reach is informational",
                                drawnGap, drawnBudget, footPoint, normal, identity with { Frame = render?.Root?.FramesProduced }, Details: details) {
                                MeshRevision = frame!.MeshDrawsRevision,
                                SurfaceWitness = sample.Surface,
                            });
                        }
                    } else {
                        Count(comparison: "solver-versus-baked-mesh", red: null);
                        Skip(reason: $"body:{index}: mesh support correspondence or walkability uncertified");
                        meshFailed.Add(item: $"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                    }
                } else if (scene is not null) { Skip(reason: $"body:{index}: interpolated presentation has no exact solver pose witness"); }
            }
            if (scene is null) { Skip(reason: "drawn walkable surface requires a settled matching rendered mesh revision and its trace scene"); }
        } else {
            Skip(reason: "view analysis is not implemented");
        }
        foreach (var reason in skipped.Keys) {
            findings.Add(item: Unknown(reason: reason, subject: "coverage") with {
                Source = reason,
                Details = (((reason == UncorrespondedReason) && (uncorresponded.Count > 0))
                    ? uncorresponded.ToDictionary(elementSelector: static entry => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{entry.Value.Count} witnesses, first at {entry.Value.Witness}"), keySelector: static entry => entry.Key)
                    : null),
            });
        }
        var coverage = new WorldDiagnosticCoverage(tested, eligible, skipped, (skipped.Count == 0)) { Comparisons = comparisons };
        var report = new WorldDiagnosticReport(snapshot, view, tick, m_tracker.Update(view, tick, findings.Select(selector: f => f with { Coverage = coverage }), complete: (skipped.Count == 0),
            evaluated: f => (evaluated.Contains(item: $"{f.Identity.Subject}|{f.Source}") && !failed.Contains(item: $"{f.Identity.Subject}|{f.Source}") &&
                ((f.MeshRevision is null) || (meshEvaluated.Contains(item: $"{f.Identity.Subject}|{f.Source}") && !meshFailed.Contains(item: $"{f.Identity.Subject}|{f.Source}"))))),
            coverage);

        m_reports[view] = report;
        LogChanged(report: report);
        return report;
    }

    private void LogChanged(WorldDiagnosticReport report) {
        var signature = string.Join(separator: "|", values: report.Findings.Select(selector: f => $"{f.Id}:{f.Severity}:{f.Resolved}:{Math.Round(a: ((f.Observed ?? 0) / Math.Max(val1: f.Budget.Total, val2: 0.00001)))}"));

        if ((m_logSignatures.GetValueOrDefault(key: report.View) == signature) ||
            (m_logTicks.TryGetValue(key: report.View, value: out var last) && ((report.Tick - Math.Min(val1: report.Tick, val2: last)) < 30))) { return; }
        m_logSignatures[report.View] = signature;
        m_logTicks[report.View] = report.Tick;
        using var parsed = System.Text.Json.JsonDocument.Parse(report.ToJson());

        Console.Error.WriteLine(value: $"[world.debug-view.{report.View}] {System.Text.Json.JsonSerializer.Serialize(parsed.RootElement)}");
    }
    private void PrepareField() {
        if (ReferenceEquals(objA: m_definition, objB: server.Definition)) {
            return;
        }
        m_definition = server.Definition;
        // Remove contact expansion: this independent field represents authored geometry, never the analytic fallback.
        var definition = m_definition with {
            PlacementRowsRaw = m_definition.Placements.Where(predicate: p => (p.Attach is null)).Select(selector: p =>
                ((p.Solid is { } solid) ? p with { Solid = solid with { Margin = 0 } } : p)).ToArray(),
            ScreensRaw = m_definition.Screens.Select(selector: s => ((s.Solid is { } solid) ? s with { Solid = solid with { Margin = 0 } } : s)).ToArray(),
            CollisionRaw = m_definition.Collision with { GridCellSize = 0 },
        };

        _ = WorldSolidField.TryBuild(definition, out m_field, out m_fieldRefusal);
    }
    // One boundary witness and what the authored field says there: buried in another collider (no reading taken), or the
    // field's signed distance and outward normal, when it answers.
    private readonly record struct WitnessReading(Vector3 Point, bool Buried, bool Answered, FixedQ4816 Distance, FixedVector3 Gradient);

    // The readings of one colliders report as far as they are taken, one slot per collider, null until read. A reading is
    // a function of the immutable collider set and field alone, so a cache for the same two instances answers a later call
    // with exactly what it would have computed; WitnessesFor is the one door that hands one out.
    private sealed class WitnessCache(IReadOnlyList<WorldDiagnosticCollider> rows, WorldSolidField field) {
        public WorldSolidField Field { get; } = field;
        public WitnessReading[]?[] Readings { get; } = new WitnessReading[]?[rows.Count];
        public IReadOnlyList<WorldDiagnosticCollider> Rows { get; } = rows;

        public int Pending => Readings.Count(predicate: static reading => (reading is null));
    }

    private WitnessCache WitnessesFor(IReadOnlyList<WorldDiagnosticCollider> rows, WorldSolidField field) =>
        (m_witnesses = (((m_witnesses is { } kept) && ReferenceEquals(objA: kept.Rows, objB: rows) && ReferenceEquals(objA: kept.Field, objB: field)) ? kept : new WitnessCache(field: field, rows: rows)));
    // Reads each unread collider's boundary witnesses in parallel, stopping when the caller's answer is owed. The work (a
    // buried test and a surface solve per witness) reads only the immutable colliders and field, and every collider's
    // readings land in their own slot, so the answers are the same as one after another. Half the processors serve it: a
    // diagnostic never takes the whole machine.
    private static void ReadWitnesses(WitnessCache cache, float?[] bounds, TextAnswerDeadline? deadline) {
        var rows = cache.Rows;

        _ = Parallel.For(
            body: (index, state) => {
                if (cache.Readings[index] is not null) {
                    return;
                }
                if (deadline?.HasPassed == true) {
                    state.Stop();

                    return;
                }

                var row = rows[index];
                var numerical = WorldDiagnosticGeometry.Numerical(collider: row.Collider);
                var points = WorldDiagnosticGeometry.SurfaceSamples(collider: row.Collider).ToArray();
                var own = new WitnessReading[points.Length];

                for (var witness = 0; (witness < points.Length); witness++) {
                    if (((witness % 16) == 15) && (deadline?.HasPassed == true)) {
                        return;
                    }

                    var point = points[witness];

                    // A buried union boundary is not an effective outer collision surface: it is settled without the
                    // field's surface solve, whose exit search from deep inside a solid is the costliest.
                    if (InsideAnother(row, rows, bounds, point, numerical)) {
                        own[witness] = new WitnessReading(Answered: false, Buried: true, Distance: default, Gradient: default, Point: point);

                        continue;
                    }

                    var fixedPoint = FixedVector3.FromVector3(value: point);
                    var answered = cache.Field.Probe(distance: out var distance, gradient: out var gradient, material: out _, position: in fixedPoint);

                    own[witness] = new WitnessReading(Answered: answered, Buried: false, Distance: distance, Gradient: gradient, Point: point);
                }

                cache.Readings[index] = own;
            },
            fromInclusive: 0,
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 2)) },
            toExclusive: rows.Count
        );
    }
    // Whether another collider covers the witness: it lies inside it, or on a face it shares with it. Only a collider
    // whose bounding radius, grown by the uncertainty, holds the witness can; the exact test (a field collider's is a
    // surface solve) runs for those alone, so the cost follows the witness's neighbours, never every collider.
    private static bool InsideAnother(WorldDiagnosticCollider subject, IReadOnlyList<WorldDiagnosticCollider> rows, float?[] bounds, Vector3 point, double epsilon) {
        for (var index = 0; (index < rows.Count); index++) {
            var other = rows[index];

            if (ReferenceEquals(objA: subject, objB: other)) {
                continue;
            }
            if ((bounds[index] is { } reach) && (Vector3.Distance(value1: point, value2: other.Collider.Center.ToVector3()) > (reach + epsilon))) {
                continue;
            }
            if ((WorldDiagnosticGeometry.SignedDistance(collider: other.Collider, point: point) < -epsilon) ||
                WorldDiagnosticGeometry.SharedInteriorFace(subject.Collider, other.Collider, point, epsilon)) {
                return true;
            }
        }
        return false;
    }

}
