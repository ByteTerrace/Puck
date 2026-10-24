using System.Globalization;
using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World;

// Presentation-only analysis. The authoritative provider inputs are copied on the host thread; no result feeds a solve.
internal sealed class WorldDiagnostics(WorldServer server, WorldRenderProbe? render = null, WorldDefinitionSource? sourceDocument = null) {
    private readonly WorldDiagnosticTracker m_tracker = new();
    private readonly Dictionary<string, WorldDiagnosticReport> m_reports = new(StringComparer.Ordinal);
    private WorldDefinition? m_definition;
    private WorldSolidField? m_field;
    private string m_fieldRefusal = "not evaluated";
    private long m_snapshot;
    private readonly Dictionary<string, string> m_logSignatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> m_logTicks = new(StringComparer.Ordinal);
    public string Mode { get; set; } = "off";
    public string Presentation { get; set; } = "default";
    public string Instance { get; set; } = "primary";
    public bool Amber { get; set; }
    public IReadOnlyList<WorldDiagnosticCollider> Colliders => server.Population.DiagnosticColliders;
    public WorldDiagnosticReport? Latest => m_reports.GetValueOrDefault(Mode);
    public ulong Tick => server.NextInputTick - 1;
    public long Revision => server.SolidRevision;
    public bool DrawnReady => render?.Residency is { Frame: { } frame } residency && !residency.MeshPathBake.Pending &&
        ReferenceEquals(frame.DiagnosticSource, server.Definition) && residency.MeshIrradiance.DiagnosticScene(frame) is not null;
    public WorldDiagnosticFinding? Find(string id) => m_reports.Values.SelectMany(r => r.Findings).FirstOrDefault(f => f.Id == id);
    public void Retain(WorldDiagnosticReport report) => m_reports[report.View] = report;

    public WorldDiagnosticReport Report(string view) {
        var tick = server.NextInputTick - 1;
        var snapshot = ++m_snapshot;
        var findings = new List<WorldDiagnosticFinding>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var tested = 0;
        var eligible = 0;
        var evaluated = new HashSet<string>(StringComparer.Ordinal);
        var meshEvaluated = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var meshFailed = new HashSet<string>(StringComparer.Ordinal);
        var frame = render?.Residency?.Frame;
        var scene = frame is not null && ReferenceEquals(frame.DiagnosticSource, server.Definition) &&
            render?.Residency?.MeshPathBake.Pending == false ? render?.Residency.MeshIrradiance.DiagnosticScene(frame) : null;
        // A conservative scene-wide bound is still derived from the cells actually drawn, never a recorded allowance.
        var bakeError = scene is null ? 0 : frame!.MeshDraws.Where(d => SdfMeshIrradianceScene.IsStatic(d))
            .Select(d => Vector3.TransformNormal(d.Mesh.BakeCell, d.ObjectToWorld).Length() * 0.5).DefaultIfEmpty(0).Max();
        PrepareField();
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        WorldDiagnosticIdentity Identity(string subject, string stamp, string creation, string node, string operation) =>
            new(snapshot, tick, null, Instance, subject, server.SolidRevision, stamp, creation, node, operation) {
                SourceFile = sourceDocument?.SourcePath.Replace('\\', '/'),
            };
        WorldDiagnosticFinding Unknown(string subject, string reason) => new("", view, "unknown", "coverage",
            "not-evaluated", reason, null, new(0, 0, 0), default, default, Identity(subject, subject, "", "", ""));

        if (view == "colliders") {
            if (WorldContactSelection.RequiresField(server.Definition.Collision)) {
                Skip("field provider: analytic hull is not the effective collision surface");
            } else if (m_field is null) {
                Skip($"authored field unavailable: {m_fieldRefusal}");
            } else {
                foreach (var row in server.Population.DiagnosticColliders) {
                    var budget = new WorldDiagnosticBudget(row.Margin, 0, WorldDiagnosticGeometry.Numerical(row.Collider));
                    var source = $"analytic/{row.Collider.Kind}/{row.Node}";
                    var scope = $"{row.Stamp}|{source}";
                    evaluated.Add(scope);
                    foreach (var point in WorldDiagnosticGeometry.SurfaceSamples(row.Collider)) {
                        eligible++;
                        var fixedPoint = FixedVector3.FromVector3(point);
                        if (!m_field.Probe(in fixedPoint, out var distance, out _, out var gradient)) {
                            Skip("field does not answer witness");
                            failed.Add(scope);
                            continue;
                        }
                        tested++;
                        var observed = (double)distance;
                        if (scene is not null && row.Opaque && !InsideAnother(row, point, budget.Numerical)) {
                            meshEvaluated.Add(scope);
                            var normal = gradient.ToVector3();
                            if (TryDrawnSurface(scene, point, normal, row.Collider.Extent.ToVector3().Length() + 1, out var drawn, out var drawnNormal, out var hitError)) {
                                var meshBudget = budget with { BakeHalfDiagonal = hitError };
                                var separation = Vector3.Dot(point - drawn, normal);
                                if (Math.Abs(separation) > meshBudget.Total) {
                                    findings.Add(new("", view, "red", source, separation > 0 ? "collider-in-drawn-air" : "drawn-solid-outside-collider",
                                        "effective collision boundary agrees with retained opaque mesh within margin and bake precision",
                                        separation, meshBudget, point, normal,
                                        Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = render?.Root?.FramesProduced },
                                        Details: new Dictionary<string, string> { ["comparison"] = "collision-versus-baked-mesh", ["meshNormal"] = drawnNormal.ToString() }) {
                                        MeshRevision = frame!.MeshDrawsRevision, SurfaceWitness = drawn,
                                    });
                                }
                                var fieldMeshBudget = new WorldDiagnosticBudget(0, 0, budget.Numerical, hitError);
                                if (m_field.Probe(FixedVector3.FromVector3(drawn), out var fieldError, out _, out _) && Math.Abs((double)fieldError) > fieldMeshBudget.Total) {
                                    findings.Add(new("", view, "red", source, "field-versus-baked-mesh",
                                        "retained mesh lies on authored field within the actual bake cell budget", (double)fieldError,
                                        fieldMeshBudget, drawn, drawnNormal, Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation) with { Frame = render?.Root?.FramesProduced }) {
                                        MeshRevision = frame!.MeshDrawsRevision,
                                    });
                                }
                            } else { Skip("mesh surface search has no witness"); meshFailed.Add(scope); }
                        }
                        if (WorldDiagnosticGeometry.ClassifyBoundary(observed, budget) is not { } contradiction) {
                            continue;
                        }
                        // Buried union boundaries are not effective outer collision surfaces.
                        if (InsideAnother(row, point, budget.Numerical)) {
                            continue;
                        }
                        findings.Add(new WorldDiagnosticFinding("", view, "red", source,
                            contradiction,
                            "effective collision boundary agrees with authored solid within collision margin and arithmetic uncertainty",
                            observed, budget, point, gradient.ToVector3(),
                            Identity(row.Stamp, row.Stamp, row.Creation, row.Node, row.Operation),
                            Details: new Dictionary<string, string> {
                                ["comparison"] = "collision-versus-authored-field",
                                ["colliderCenter"] = row.Collider.Center.ToVector3().ToString(),
                                ["colliderExtent"] = row.Collider.Extent.ToVector3().ToString(),
                                ["colliderRotation"] = row.Collider.Rotation.ToString(),
                                ["sampling"] = "finite boundary witnesses; not a complete surface proof",
                            }));
                    }
                }
            }
            if (scene is null) { Skip("field-versus-baked-mesh requires a settled matching rendered mesh revision and its trace scene"); }
            foreach (var placement in server.Definition.Placements.Where(p => p.Solid is not null && p.Attach is not null)) {
                Skip($"attached pose not retained: {placement.Id}");
            }
        } else if (view == "contacts") {
            for (var index = 0; index < server.Population.Capacity; index++) {
                if (!server.Population.IsActive(index) || server.Population.EntryBody(index) is not { Grounded: true } body) {
                    continue;
                }
                eligible++;
                if (body.DiagnosticSupport is not { } support) {
                    Skip($"body:{index}: solver support witness unavailable");
                    continue;
                }
                if (m_field is null) {
                    Skip($"body:{index}: authored surface query unavailable");
                    continue;
                }
                var normal = support.Push.Normal.ToVector3();
                if (support.Source != FixedSurfaceColliderSource.Static) {
                    Skip($"body:{index}: dynamic support has no retained authored/rendered pose pair");
                    continue;
                }
                var foot = support.BodyPoint;
                if (!FixedSurfaceQuery.TryNearest([support.Collider], [], in foot, FixedQ4816.MaxValue, out var surface) ||
                    !m_field.Probe(surface.Point, out var distance, out _, out var gradient)) {
                    Skip($"body:{index}: support surface query unavailable");
                    continue;
                }
                tested++;
                var budget = new WorldDiagnosticBudget(0, server.Definition.Collision.ContactSkin, WorldDiagnosticGeometry.Numerical(support.Collider));
                var gap = (double)FixedVector3.Dot(foot - surface.Point, support.Push.Normal);
                var row = support.Source == FixedSurfaceColliderSource.Static && support.ColliderIndex < server.Population.DiagnosticColliders.Count
                    ? server.Population.DiagnosticColliders[support.ColliderIndex] : null;
                if (row is null || row.Collider != support.Collider) {
                    Skip($"body:{index}: retained support collider predates the current collision revision");
                    continue;
                }
                var identity = Identity($"body:{index}", row?.Stamp ?? "attached", row?.Creation ?? "", row?.Node ?? "", row?.Operation ?? "");
                evaluated.Add($"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                var details = new Dictionary<string, string> {
                    ["supportPoint"] = surface.Point.ToVector3().ToString(), ["bodySupport"] = foot.ToVector3().ToString(),
                    ["normal"] = normal.ToString(), ["contactSkin"] = budget.ContactSkin.ToString(CultureInfo.InvariantCulture),
                    ["snapReachInformationOnly"] = body.DiagnosticSnapReach.ToString(CultureInfo.InvariantCulture),
                    ["provider"] = $"analytic/{support.Collider.Kind}",
                    ["comparison"] = "solver-versus-authored-field; rendered mesh not evaluated",
                };
                if (gap > budget.Total && body.DiagnosticSettled) {
                    findings.Add(new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "hovering",
                        "grounded support clearance <= contact skin plus numerical error", gap, budget, foot.ToVector3(), normal,
                        identity, Details: details) { SurfaceWitness = surface.Point.ToVector3() });
                }
                var fieldBudget = budget with { CollisionMargin = row?.Margin ?? 0 };
                if ((double)distance > fieldBudget.Total) {
                    findings.Add(new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "grounded-on-authored-air",
                        "actual solver support lies on the authored solid within the support budget", (double)distance,
                        fieldBudget, surface.Point.ToVector3(), gradient.ToVector3(), identity, Details: details));
                }
                if (scene is not null && row?.Opaque == true && frame!.Clock.Whole == server.CompletedEngineTicks && frame.Clock.Fraction == 0) {
                    meshEvaluated.Add($"body:{index}|analytic/{support.Source}/{support.ColliderIndex}");
                    var drawnBudget = fieldBudget with { BakeHalfDiagonal = bakeError };
                    var footPoint = foot.ToVector3();
                    var reach = Math.Max((float)drawnBudget.Total * 4, (float)(double)support.Volume.Radius + 1);
                    if (scene.Intersect(footPoint + normal * (float)drawnBudget.Total, -normal, reach,
                        out var depth, out var drawnNormal, out _, out var hitError) &&
                        FixedVector3.Dot(FixedVector3.FromVector3(drawnNormal), support.Up) >= support.WalkableThreshold) {
                        var drawnGap = depth - drawnBudget.Total;
                        drawnBudget = drawnBudget with { BakeHalfDiagonal = hitError };
                        if (drawnGap > drawnBudget.Total) {
                            findings.Add(new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "grounded-on-air",
                                "actual grounded support has a walkable retained opaque surface within its precision budget",
                                drawnGap, drawnBudget, footPoint, normal, identity with { Frame = render?.Root?.FramesProduced }, Details: details) {
                                MeshRevision = frame!.MeshDrawsRevision, SurfaceWitness = footPoint - normal * (float)drawnGap,
                            });
                        }
                        if (drawnGap > drawnBudget.Total && body.DiagnosticSettled) {
                            findings.Add(new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "hovering-over-drawn-surface",
                                "settled support gap <= contact skin plus collision and bake precision; snap reach is informational",
                                drawnGap, drawnBudget, footPoint, normal, identity with { Frame = render?.Root?.FramesProduced }, Details: details) {
                                MeshRevision = frame!.MeshDrawsRevision, SurfaceWitness = footPoint - normal * (float)drawnGap,
                            });
                        }
                    } else {
                        findings.Add(new("", view, "red", $"analytic/{support.Source}/{support.ColliderIndex}", "grounded-on-air",
                            "actual grounded support has a walkable retained opaque surface within its precision budget",
                            reach, drawnBudget, footPoint, normal, identity with { Frame = render?.Root?.FramesProduced }, Details: details) {
                            MeshRevision = frame!.MeshDrawsRevision,
                        });
                    }
                } else if (scene is not null) { Skip($"body:{index}: interpolated presentation has no exact solver pose witness"); }
            }
            if (scene is null) { Skip("drawn walkable surface requires a settled matching rendered mesh revision and its trace scene"); }
        } else {
            Skip("view analysis is not implemented");
        }
        foreach (var reason in skipped.Keys) {
            findings.Add(Unknown("coverage", reason) with { Source = reason });
        }
        var coverage = new WorldDiagnosticCoverage(tested, eligible, skipped, skipped.Count == 0);
        var report = new WorldDiagnosticReport(snapshot, view, tick, m_tracker.Update(view, tick, findings.Select(f => f with { Coverage = coverage }), complete: skipped.Count == 0,
            evaluated: f => evaluated.Contains($"{f.Identity.Subject}|{f.Source}") && !failed.Contains($"{f.Identity.Subject}|{f.Source}") &&
                (f.MeshRevision is null || meshEvaluated.Contains($"{f.Identity.Subject}|{f.Source}") && !meshFailed.Contains($"{f.Identity.Subject}|{f.Source}"))),
            coverage);
        m_reports[view] = report;
        LogChanged(report);
        return report;
    }

    private void LogChanged(WorldDiagnosticReport report) {
        var signature = string.Join("|", report.Findings.Select(f => $"{f.Id}:{f.Severity}:{f.Resolved}:{Math.Round((f.Observed ?? 0) / Math.Max(f.Budget.Total, 0.00001))}"));
        if (m_logSignatures.GetValueOrDefault(report.View) == signature ||
            m_logTicks.TryGetValue(report.View, out var last) && report.Tick - Math.Min(report.Tick, last) < 30) { return; }
        m_logSignatures[report.View] = signature;
        m_logTicks[report.View] = report.Tick;
        using var parsed = System.Text.Json.JsonDocument.Parse(report.ToJson());
        Console.Error.WriteLine($"[world.debug-view.{report.View}] {System.Text.Json.JsonSerializer.Serialize(parsed.RootElement)}");
    }

    private static bool TryDrawnSurface(SdfMeshIrradianceScene scene, Vector3 point, Vector3 normal, float reach,
        out Vector3 surface, out Vector3 surfaceNormal, out float bakeHalfDiagonal) {
        surface = default;
        surfaceNormal = default;
        bakeHalfDiagonal = 0;
        if (normal.LengthSquared() < 0.01f) { return false; }
        normal = Vector3.Normalize(normal);
        var outward = scene.Intersect(point, normal, reach, out var outside, out var outsideNormal, out _, out var outsideError);
        var inward = scene.Intersect(point, -normal, reach, out var inside, out var insideNormal, out _, out var insideError);
        if (outward && Vector3.Dot(outsideNormal, normal) > 0 && (!inward || outside < inside)) {
            surface = point + normal * outside;
            surfaceNormal = outsideNormal;
            bakeHalfDiagonal = outsideError;
            return true;
        }
        if (!inward || Vector3.Dot(insideNormal, normal) <= 0) { return false; }
        surface = point - normal * inside;
        surfaceNormal = insideNormal;
        bakeHalfDiagonal = insideError;
        return true;
    }

    private void PrepareField() {
        if (ReferenceEquals(m_definition, server.Definition)) {
            return;
        }
        m_definition = server.Definition;
        // Remove contact expansion: this independent field represents authored geometry, never the analytic fallback.
        var definition = m_definition with {
            PlacementRowsRaw = m_definition.Placements.Where(p => p.Attach is null).Select(p =>
                p.Solid is { } solid ? p with { Solid = solid with { Margin = 0 } } : p).ToArray(),
            ScreensRaw = m_definition.Screens.Select(s => s.Solid is { } solid ? s with { Solid = solid with { Margin = 0 } } : s).ToArray(),
            CollisionRaw = m_definition.Collision with { GridCellSize = 0 },
        };
        _ = WorldSolidField.TryBuild(definition, out m_field, out m_fieldRefusal);
    }

    private bool InsideAnother(WorldDiagnosticCollider subject, Vector3 point, double epsilon) {
        foreach (var other in server.Population.DiagnosticColliders) {
            if (!ReferenceEquals(subject, other) && WorldDiagnosticGeometry.SignedDistance(other.Collider, point) < -epsilon) {
                return true;
            }
        }
        return false;
    }

}
