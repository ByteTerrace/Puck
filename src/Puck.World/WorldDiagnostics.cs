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
    private const string AttachedMeshReason = "attached solid draws are dynamic, outside the retained static trace scene";
    private const string HeadlessMeshReason = "rendered-mesh comparisons: this host composes no renderer, so it draws no mesh to compare; a windowed or offscreen World makes them";
    private const string MeshUnsettledReason = "field-versus-baked-mesh requires a settled matching rendered mesh revision and its trace scene";
    private const string UncorrespondedReason = "mesh correspondence uncertified: no nearest triangle within precision search radius";

    private readonly WorldDiagnosticTracker m_tracker = new();
    private readonly Dictionary<string, WorldDiagnosticReport> m_reports = new(comparer: StringComparer.Ordinal);

    private WorldDefinition? m_definition;
    private WorldSolidField? m_field;

    // The attached solid rows at the pose the authored field and m_rows were built for.
    private IReadOnlyList<WorldAttachedDiagnostic> m_attached = [];

    // The solver's static colliders m_rows was built from, and m_rows itself: those colliders followed by every attached
    // row's at its sampled pose, one stable list per collider set and pose so a resumed report reuses its readings.
    private IReadOnlyList<WorldDiagnosticCollider>? m_staticRows;

    private IReadOnlyList<WorldDiagnosticCollider> m_rows = [];

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

    private sealed record ContactInput(int Index, ContactSupportWitness? DiagnosticSupport, bool IsRigid,
        double DiagnosticSnapReach, bool DiagnosticSettled);

    internal sealed record Evaluation(WorldDiagnosticReport Report, Func<WorldDiagnosticFinding, bool> Evaluated) {
        public WorldDiagnosticReport? Published { get; set; }
    }

    private readonly Dictionary<string, Task<Evaluation>> m_pending = new(comparer: StringComparer.Ordinal);

    internal static readonly TimeSpan ReportBudget = TimeSpan.FromSeconds(seconds: 5);

    public bool HasPending(string view) => m_pending.ContainsKey(key: view);
    // Only this host-thread door captures live state. Workers receive values, immutable collider/field inputs and
    // an acquired mesh-scene claim; they never read a server, body, renderer or the retained findings tracker.
    public Task<Evaluation> BeginReport(string view, TextAnswerDeadline? deadline = null) {
        if (m_pending.TryGetValue(key: view, value: out var pending)) { return pending; }
        deadline ??= new TextAnswerDeadline(TimeProvider.System, ReportBudget);
        PrepareField();
        var field = m_field;
        var fieldRefusal = m_fieldRefusal;
        var rows = m_rows;
        var staticRows = (m_staticRows ?? []);
        var cache = (((field is null) || (view != "colliders")) ? null : WitnessesFor(field: field, rows: rows));
        var tick = Tick;
        var snapshot = ++m_snapshot;
        var revision = Revision;
        var instance = Instance;
        var sourcePath = sourceDocument?.SourcePath.Replace(newChar: '/', oldChar: '\\');
        var collision = server.Definition.Collision;
        var contacts = new List<ContactInput>();

        if (view == "contacts") {
            for (var index = 0; (index < server.Population.Capacity); index++) {
                if (server.Population.IsActive(index: index) && (server.Population.EntryBody(index: index) is { Grounded: true, Collider: not null } body)) {
                    contacts.Add(item: new(index, body.DiagnosticSupport, body.IsRigid, body.DiagnosticSnapReach, body.DiagnosticSettled));
                }
            }
        }
        var frame = render?.Residency?.Frame;
        var scene = (((frame is not null) && Equals(objA: frame.DiagnosticSource, objB: server.DocumentVersion) &&
            (render?.Residency?.MeshPathBake.Pending == false)) ? render.Residency.MeshIrradiance.DiagnosticScene(frame: frame) : null);
        var bakeError = ((scene is null) ? 0 : frame!.MeshDraws.Where(predicate: static d => SdfMeshIrradianceScene.IsStatic(draw: d))
            .Select(selector: static d => (Vector3.TransformNormal(d.Mesh.BakeCell, d.ObjectToWorld).Length() * 0.5)).DefaultIfEmpty(defaultValue: 0).Max());
        var frameNumber = render?.Root?.FramesProduced;
        var meshRevision = frame?.MeshDrawsRevision;
        var exactPose = ((frame is not null) && (frame.Clock.Whole == server.CompletedEngineTicks) && (frame.Clock.Fraction == 0));
        var rendered = (render is not null);
        var job = Task.Run(function: () => {
            using (scene) {
                return Evaluate(bakeError: bakeError, cache: cache, collision: collision, contacts: contacts, deadline: deadline, exactPose: exactPose, field: field, fieldRefusal: fieldRefusal, frameNumber: frameNumber,
                    instance: instance, meshRevision: meshRevision, rendered: rendered, revision: revision, rows: rows, scene: scene, snapshot: snapshot, sourcePath: sourcePath, staticRows: staticRows, tick: tick, view: view);
            }
        });

        m_pending.Add(key: view, value: job);
        return job;
    }
    // Polling and publication stay on the host. At most one worker per view may own a witness cache.
    public WorldDiagnosticReport Report(string view, TextAnswerDeadline? deadline = null) {
        var task = BeginReport(deadline: deadline, view: view);

        if (!task.IsCompleted) { return Unfinished(view, m_snapshot, Tick, Instance, Revision, "analysis pending"); }
        return CompleteReport(task: task, view: view);
    }
    public WorldDiagnosticReport CompleteReport(string view, Task<Evaluation> task) {
        if (!task.IsCompleted) { throw new InvalidOperationException(message: "The diagnostic report is still running."); }
        if (m_pending.GetValueOrDefault(key: view) == task) { m_pending.Remove(key: view); }
        var result = task.GetAwaiter().GetResult();

        if (result.Published is { } published) { return published; }
        var report = result.Report;

        if (report.Coverage.Pending > 0) { return report; }
        report = report with {
            Findings = m_tracker.Update(view, report.Tick, report.Findings,
            complete: report.Coverage.Complete, evaluated: result.Evaluated),
        };
        m_reports[view] = report;
        result.Published = report;
        LogChanged(report: report);
        return report;
    }

    private static WorldDiagnosticReport Unfinished(string view, long snapshot, ulong tick, string instance, long revision, string reason, int pending = 1) {
        reason = $"report unfinished: {reason}; ask again";
        var coverage = new WorldDiagnosticCoverage(0, 0, new Dictionary<string, int> { [reason] = 1 }, false) { Pending = pending };
        var identity = new WorldDiagnosticIdentity(Creation: "", Frame: null, Instance: instance, Node: "", Operation: "", Revision: revision, Snapshot: snapshot, Stamp: "coverage", Subject: "coverage", Tick: tick);

        return new(snapshot, view, tick, [new("", view, "unknown", reason, "not-evaluated", reason, null,
            new(0, 0, 0), default, default, identity) { Coverage = coverage }], coverage);
    }
    private static Evaluation Evaluate(string view, TextAnswerDeadline deadline, ulong tick, long snapshot, long revision,
        string instance, string? sourcePath, WorldCollision collision, WorldSolidField? field, string fieldRefusal,
        IReadOnlyList<WorldDiagnosticCollider> rows, IReadOnlyList<WorldDiagnosticCollider> staticRows, WitnessCache? cache,
        IReadOnlyList<ContactInput> contacts, SdfMeshIrradianceScene? scene, double bakeError, long? frameNumber,
        long? meshRevision, bool exactPose, bool rendered) {
        using var cancel = new CancellationTokenSource();
        var remaining = deadline.Remaining;

        if (remaining <= TimeSpan.Zero) { cancel.Cancel(); } else { cancel.CancelAfter(delay: remaining); }
        var token = cancel.Token;

        try {
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
            // What this host cannot compare by construction (a headless host draws no mesh), by reason and how many witnesses
            // or bodies it would have covered: reported apart from skipped inputs and never an unknown.
            var excluded = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
            var excludedMesh = 0;

            void Skip(string reason) => skipped[reason] = (skipped.GetValueOrDefault(key: reason) + 1);
            void Exclude(string reason, int count) {
                if (count > 0) { excluded[reason] = (excluded.GetValueOrDefault(key: reason) + count); }
            }
            WorldDiagnosticIdentity Identity(string subject, string stamp, string creation, string node, string operation) =>
                new(Creation: creation, Frame: null, Instance: instance, Node: node, Operation: operation, Revision: revision, Snapshot: snapshot, Stamp: stamp, Subject: subject, Tick: tick) {
                    SourceFile = sourcePath,
                };
            WorldDiagnosticFinding Unknown(string subject, string reason) => new("", view, "unknown", "coverage",
                "not-evaluated", reason, null, new(0, 0, 0), default, default, Identity(creation: "", node: "", operation: "", stamp: subject, subject: subject));

            if (view == "colliders") {
                if (WorldContactSelection.RequiresField(collision: collision)) {
                    Skip(reason: "field provider: analytic hull is not the effective collision surface");
                } else if (field is null) {
                    Skip(reason: $"authored field unavailable: {fieldRefusal}");
                } else {
                    var staticCount = staticRows.Count;
                    // Each collider's bounding radius, once: a witness beyond a neighbour's is neither inside it nor on its face.
                    var bounds = rows.Select(selector: static row => WorldDiagnosticGeometry.Bound(collider: row.Collider)).ToArray();

                    ReadWitnesses(bounds: bounds, cache: cache!, token: token);

                    if (cache!.Pending > 0) {
                        return new(Unfinished(view, snapshot, tick, instance, revision, "TextAnswerDeadline reached while reading witnesses", cache.Pending), static _ => false);
                    }

                    var readings = cache.Readings;

                    for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
                        token.ThrowIfCancellationRequested();
                        var row = rows[rowIndex];
                        var budget = new WorldDiagnosticBudget(row.Margin, 0, WorldDiagnosticGeometry.Numerical(collider: row.Collider)) {
                            ConservativeInset = row.ConservativeInset,
                        };
                        var source = $"analytic/{row.Collider.Kind}/{row.Node}";
                        var scope = $"{row.Stamp}|{source}";

                        evaluated.Add(item: scope);
                        foreach (var reading in readings[rowIndex]!) {
                            token.ThrowIfCancellationRequested();
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
                            if (row.Opaque && !rendered) {
                                excludedMesh++;
                            } else if (row.Opaque && (rowIndex >= staticCount)) {
                                Count(comparison: "collision-versus-baked-mesh", red: null);
                                Count(comparison: "field-versus-baked-mesh", red: null);
                                Skip(reason: AttachedMeshReason);
                            } else if (row.Opaque) {
                                meshEvaluated.Add(item: scope);
                                var normal = gradient.ToVector3();

                                if ((scene is not null) && (WorldDiagnosticMeshQuery.Measure(budget: budget, point: point, scene: scene, sceneBakeError: bakeError) is { } sample)) {
                                    var drawn = sample.Surface;
                                    var drawnNormal = sample.Normal;
                                    var hitError = sample.Budget.BakeHalfDiagonal;
                                    var enclosed = !WorldDiagnosticGeometry.OpensOnto(rows.Select(selector: static c => c.Collider),
                                        point, normal, hitError, token);

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
                                            Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = frameNumber },
                                            Details: new Dictionary<string, string> { ["comparison"] = "collision-versus-baked-mesh", ["meshNormal"] = drawnNormal.ToString(), ["correspondence"] = "bounded nearest triangle; unsigned Euclidean distance" }) {
                                            MeshRevision = meshRevision,
                                            SurfaceWitness = drawn,
                                        });
                                    }
                                    var fieldMeshBudget = new WorldDiagnosticBudget(0, 0, budget.Numerical, hitError);
                                    var fieldAnswered = field.Read(FixedVector3.FromVector3(value: drawn), out var fieldError, out _, out _);

                                    Count(comparison: "field-versus-baked-mesh", red: (fieldAnswered ? (Math.Abs(value: ((double)fieldError)) > fieldMeshBudget.Total) : null));
                                    if (!fieldAnswered) { Skip(reason: "authored field does not answer certified mesh witness"); meshFailed.Add(item: scope); }
                                    if (fieldAnswered && (Math.Abs(value: ((double)fieldError)) > fieldMeshBudget.Total)) {
                                        findings.Add(item: new("", view, "red", source, "field-versus-baked-mesh",
                                            "retained mesh lies on authored field within the actual bake cell budget", ((double)fieldError),
                                            fieldMeshBudget, drawn, drawnNormal, Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = frameNumber }) {
                                            MeshRevision = meshRevision,
                                        });
                                    }
                                } else if (scene is null) {
                                    Count(comparison: "collision-versus-baked-mesh", red: null);
                                    Count(comparison: "field-versus-baked-mesh", red: null);
                                    Skip(reason: MeshUnsettledReason);
                                    meshFailed.Add(item: scope);
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
                if (!rendered) {
                    Exclude(count: excludedMesh, reason: HeadlessMeshReason);
                } else if ((scene is null) && !skipped.ContainsKey(key: MeshUnsettledReason)) {
                    Skip(reason: MeshUnsettledReason);
                }
            } else if (view == "contacts") {
                foreach (var body in contacts) {
                    token.ThrowIfCancellationRequested();
                    var index = body.Index;

                    eligible++;
                    if (body.DiagnosticSupport is not { } support) {
                        Skip(reason: $"body:{index}: solver support witness unavailable");
                        continue;
                    }
                    if (field is null) {
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
                        !field.Read(surface.Point, out var distance, out _, out var gradient)) {
                        Skip(reason: $"body:{index}: support surface query unavailable");
                        continue;
                    }
                    tested++;
                    var budget = new WorldDiagnosticBudget(0, collision.ContactSkin, WorldDiagnosticGeometry.Numerical(collider: support.Collider)) {
                        RestBand = (body.IsRigid ? ((double)WorldPopulation.RigidRestBand) : 0),
                    };
                    var gap = ((double)FixedVector3.Dot(left: (foot - surface.Point), right: support.Push.Normal));
                    var row = (((support.Source == FixedSurfaceColliderSource.Static) && (support.ColliderIndex < staticRows.Count))
                        ? staticRows[support.ColliderIndex] : null);

                    if ((row is null) || !row.Collider.AnswersWith(effective: support.Collider)) {
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
                    if ((scene is not null) && (row?.Opaque == true) && exactPose) {
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
                                    drawnGap, drawnBudget, footPoint, normal, identity with { Frame = frameNumber }, Details: details) {
                                    MeshRevision = meshRevision,
                                    SurfaceWitness = sample.Surface,
                                });
                            }
                            if ((drawnGap > drawnBudget.Total) && body.DiagnosticSettled) {
                                findings.Add(item: new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "hovering-over-drawn-surface",
                                    "settled support gap <= contact skin plus collision and bake precision; snap reach is informational",
                                    drawnGap, drawnBudget, footPoint, normal, identity with { Frame = frameNumber }, Details: details) {
                                    MeshRevision = meshRevision,
                                    SurfaceWitness = sample.Surface,
                                });
                            }
                        } else {
                            Count(comparison: "solver-versus-baked-mesh", red: null);
                            Skip(reason: $"body:{index}: mesh support correspondence or walkability uncertified");
                            meshFailed.Add(item: $"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                        }
                    } else if (scene is not null) {
                        Skip(reason: $"body:{index}: interpolated presentation has no exact solver pose witness");
                    } else if (!rendered && (row?.Opaque == true)) {
                        excludedMesh++;
                    }
                }
                if (!rendered) {
                    Exclude(count: excludedMesh, reason: HeadlessMeshReason);
                } else if (scene is null) {
                    Skip(reason: "drawn walkable surface requires a settled matching rendered mesh revision and its trace scene");
                }
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
            var coverage = new WorldDiagnosticCoverage(tested, eligible, skipped, (skipped.Count == 0)) { Comparisons = comparisons, Excluded = excluded };
            var report = new WorldDiagnosticReport(snapshot, view, tick, findings.Select(selector: f => f with { Coverage = coverage }).ToArray(), coverage);

            return new(report, f => (evaluated.Contains(item: $"{f.Identity.Subject}|{f.Source}") && !failed.Contains(item: $"{f.Identity.Subject}|{f.Source}") &&
                ((f.MeshRevision is null) || (meshEvaluated.Contains(item: $"{f.Identity.Subject}|{f.Source}") && !meshFailed.Contains(item: $"{f.Identity.Subject}|{f.Source}")))));
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            return new(Unfinished(view, snapshot, tick, instance, revision, "TextAnswerDeadline reached"), static _ => false);
        }
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
    // Builds the authored field for the current definition with every attached solid row at the pose the solver last
    // sampled it, and the colliders compared against it: the solver's static colliders, then each attached row's at that
    // same pose. Both are rebuilt only when the definition, the collider set or an attached pose changes.
    private void PrepareField() {
        var attached = server.Population.DiagnosticAttached();
        var staticRows = server.Population.DiagnosticColliders;
        var samePoses = ((attached.Count == m_attached.Count) && attached.Zip(second: m_attached).All(predicate: static pair => (pair.First.Pose == pair.Second.Pose)));

        if (!ReferenceEquals(objA: m_staticRows, objB: staticRows) || !samePoses) {
            m_staticRows = staticRows;
            m_rows = [.. staticRows, .. attached.SelectMany(selector: static row => row.Colliders)];
        }
        if (ReferenceEquals(objA: m_definition, objB: server.Definition) && samePoses) {
            m_attached = attached;
            return;
        }
        m_definition = server.Definition;
        m_attached = attached;
        // Remove contact expansion: this independent field represents authored geometry, never the analytic fallback.
        static WorldPlacement Unexpanded(WorldPlacement placement) => ((placement.Solid is { } solid) ? placement with { Solid = solid with { Margin = 0 } } : placement);
        var definition = m_definition with {
            PlacementRowsRaw = m_definition.Placements.Where(predicate: p => (p.Attach is null)).Select(selector: Unexpanded).ToArray(),
            ScreensRaw = m_definition.Screens.Select(selector: s => ((s.Solid is { } solid) ? s with { Solid = solid with { Margin = 0 } } : s)).ToArray(),
            CollisionRaw = m_definition.Collision with { GridCellSize = 0 },
        };

        _ = WorldSolidField.TryBuild(definition, out m_field, out m_fieldRefusal,
            posed: attached.Select(selector: static row => row.Pose with { Placement = Unexpanded(placement: row.Pose.Placement) }).ToArray());
    }

    // One boundary witness and what the authored field says there: buried in another collider (no reading taken), or the
    // field's signed reading (exact in sign, a lower bound on the distance to the authored surface) and outward normal,
    // when it answers. A surface solve gives an upper bound instead, which at a crease of touching solids or inside a union
    // of overlapping ones runs through other solids and is no measure of how far the authored solid reaches.
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
    // buried test and a field reading per witness) reads only the immutable colliders and field, and every collider's
    // readings land in their own slot, so the answers are the same as one after another. Half the processors serve it: a
    // diagnostic never takes the whole machine.
    private static void ReadWitnesses(WitnessCache cache, float?[] bounds, CancellationToken token) {
        var rows = cache.Rows;

        _ = Parallel.For(
            body: (index, state) => {
                if (cache.Readings[index] is not null) {
                    return;
                }
                if (token.IsCancellationRequested) {
                    state.Stop();

                    return;
                }

                var row = rows[index];
                var numerical = WorldDiagnosticGeometry.Numerical(collider: row.Collider);
                var points = WorldDiagnosticGeometry.SurfaceSamples(collider: row.Collider, token: token).ToArray();
                var own = new WitnessReading[points.Length];

                for (var witness = 0; (witness < points.Length); witness++) {
                    if (token.IsCancellationRequested) {
                        return;
                    }

                    var point = points[witness];

                    // A buried union boundary is not an effective outer collision surface: it is settled before the field
                    // is read.
                    if (InsideAnother(bounds: bounds, epsilon: numerical, point: point, rows: rows, subject: row, token: token)) {
                        own[witness] = new WitnessReading(Answered: false, Buried: true, Distance: default, Gradient: default, Point: point);

                        continue;
                    }

                    var fixedPoint = FixedVector3.FromVector3(value: point);
                    var answered = cache.Field.Read(distance: out var distance, gradient: out var gradient, material: out _, position: in fixedPoint);

                    own[witness] = new WitnessReading(Answered: answered, Buried: false, Distance: distance, Gradient: gradient, Point: point);
                }

                cache.Readings[index] = own;
            },
            fromInclusive: 0,
            parallelOptions: new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 2)) },
            toExclusive: rows.Count
        );
    }
    // Whether another collider covers the witness: it lies inside it, or on a face it shares with it. Only a collider
    // whose bounding radius, grown by the uncertainty, holds the witness can; the exact test (a field collider's is a
    // surface solve) runs for those alone, so the cost follows the witness's neighbours, never every collider.
    private static bool InsideAnother(WorldDiagnosticCollider subject, IReadOnlyList<WorldDiagnosticCollider> rows, float?[] bounds, Vector3 point, double epsilon, CancellationToken token) {
        for (var index = 0; (index < rows.Count); index++) {
            token.ThrowIfCancellationRequested();
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
