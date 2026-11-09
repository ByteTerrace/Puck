using System.Globalization;
using System.Numerics;
using System.Text;

using Puck.Cli.Host;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh audit --cpu --field-levels</c>: for each selected group the offline bake meshed, measures its collapse
/// levels by the error contract, then contours the field levels that follow the collapse's coarsest level
/// (<see cref="SdfMeshFieldLevels.Contour"/>, per member for a group whose shapes ride several slots) and judges each by
/// the plan's clauses against the level it follows: no more than the kept share of its triangles, no more open edges,
/// flips or missing crossings than level zero and no surface outside it, every witness bracketed, and a recorded error
/// below the group's half diagonal, where the terminal empty level would be drawn instead. It prints the chain each
/// judgement builds (with the normal component and without it), the field evaluations each path takes (the bake's
/// lattices, the levels' contours and the oracle apart) and the processor work of each path's own CPU stages on the
/// thread that runs them (<see cref="ThreadCycles"/>), field evaluations excluded since a World answers them on the GPU.
/// </summary>
internal static class MeshFieldLevels {
    /// <summary>Measures one group's collapse levels and the field levels that follow them.</summary>
    /// <param name="group">The group.</param>
    /// <param name="scene">The rest scene it was planned from.</param>
    /// <param name="mesh">The offline bake's landed mesh of the group.</param>
    /// <param name="reference">Level zero's audit report.</param>
    /// <param name="bakeEvaluations">The field evaluations the offline bake's lattices of the group took.</param>
    /// <param name="options">The levels' options.</param>
    /// <returns>The group's measurement, or <see langword="null"/> for a glass group, whose pass draws level zero
    /// alone.</returns>
    public static Group? Measure(SdfMeshBakeGroup group, RestScene scene, SdfMesh mesh, SdfMeshAuditReport reference, long bakeEvaluations, SdfMeshFieldLevelOptions options) {
        if (group.Glass) { return null; }

        var preserve = group.Geometry.ContainsGlyph;
        var cell = MathF.Max(x: mesh.BakeCell.X, y: MathF.Max(x: mesh.BakeCell.Y, y: mesh.BakeCell.Z));
        var program = SdfMeshAudit.Field(group: group, program: scene.Program);

        if (!(cell > 0f) || !SdfMeshAudit.TryEvaluator(field: out var auditField, program: program, refusal: out _)) { return null; }

        var work = new Work();
        using var union = new Field(program: program, work: work);
        var members = SdfMeshAudit.MemberFields(group: group, program: scene.Program);
        var pieceFields = new List<Field>();
        var pieces = new List<(ISdfMeshLevelField Field, byte Member)>();

        if (members.Count > 1) {
            for (var index = 0; (index < members.Count); index++) {
                var piece = new Field(program: members[index].Field, work: work);

                pieceFields.Add(item: piece);
                pieces.Add(item: (piece, ((index == 0) ? SdfMesh.NoMember : (byte)(index - 1))));
            }
        }

        var level0 = SdfMeshAudit.Surface(level: 0, mesh: mesh);

        // The oracle's own work: the surface witnesses and every level's measurement.
        var started = ThreadCycles.Now();
        var inside = work.Inside;
        var levels = new SdfMeshFieldLevels(cell: cell, field: union, options: options, reference: level0);
        var oracleCycles = ((ThreadCycles.Now() - started) - (work.Inside - inside));

        // The collapse's own work: each piece's collapse over its merged mesh, as Land runs it.
        started = ThreadCycles.Now();
        foreach (var piece in ((mesh.Chunks.Count > 0) ? mesh.Chunks : [mesh])) {
            _ = SdfMeshCollapse.Levels(mesh: (piece with { Levels = [] }), preserveFeatures: preserve);
        }
        var collapseCycles = (ThreadCycles.Now() - started);

        var collapseCount = ((mesh.Chunks.Count > 0) ? mesh.Chunks.Max(selector: static chunk => chunk.Levels.Count) : mesh.Levels.Count);
        var collapse = new List<Row>();
        var finer = 0f;

        started = ThreadCycles.Now();
        inside = work.Inside;
        for (var level = 1; (level <= collapseCount); level++) {
            var surface = SdfMeshAudit.Surface(level: level, mesh: mesh);
            var recorded = ((mesh.Chunks.Count > 0) ? mesh.Chunks.Where(predicate: chunk => (chunk.Levels.Count > 0)).Max(selector: chunk => chunk.Levels[(Math.Min(val1: level, val2: chunk.Levels.Count) - 1)].Error) : mesh.Levels[(level - 1)].Error);
            var error = levels.Measure(budget: recorded, cell: cell, finer: finer, level: surface, members: null, pieces: []);

            finer = error.Recorded;
            collapse.Add(item: new Row(Audit: SdfMeshAudit.Audit(field: auditField, mesh: surface, options: new SdfMeshAuditOptions { Clusters = 0 }, shapes: []), Cell: cell, Error: error, Level: level, Offset: recorded, Skipped: null, Triangles: surface.TriangleCount, Vanished: 0, Whole: surface.TriangleCount));
        }
        oracleCycles += ((ThreadCycles.Now() - started) - (work.Inside - inside));

        var follow = ((collapse.Count > 0) ? (Triangles: collapse[^1].Triangles, Error: collapse[^1].Error!.Recorded) : (Triangles: level0.TriangleCount, Error: 0f));

        // The levels' contours: the work a bake appending them pays.
        started = ThreadCycles.Now();
        inside = work.Inside;
        var contoured = levels.Contour(allowance: (reference.Flipped, reference.OpenEdges), most: ((int)(SdfMeshCollapse.KeptShare * follow.Triangles)), pieces: pieces, preserveFeatures: preserve);
        var contourCycles = ((ThreadCycles.Now() - started) - (work.Inside - inside));
        var contourEvaluations = work.Contour;

        started = ThreadCycles.Now();
        inside = work.Inside;
        var field = new List<Row>();

        foreach (var level in contoured) {
            if (level.Mesh is not { } kept) {
                field.Add(item: new Row(Audit: null, Cell: level.Cell, Error: null, Level: level.Level, Offset: level.Offset, Skipped: (level.Skipped ?? ((level.Whole is null) ? "empty" : "every component vanished")), Triangles: 0, Vanished: level.VanishedComponents, Whole: (level.Whole?.TriangleCount ?? 0)));
                continue;
            }

            var error = levels.Measure(budget: level.Offset, cell: level.Cell, finer: 0f, level: kept, members: level.Members, pieces: pieces, vanished: level.Vanished, whole: level.Whole);

            field.Add(item: new Row(Audit: SdfMeshAudit.Audit(accounted: Accounted(level0: level0, vanished: error.VanishedTriangles), field: auditField, mesh: kept, options: new SdfMeshAuditOptions { Clusters = 0 }, shapes: []), Cell: level.Cell, Error: error, Level: level.Level, Offset: level.Offset, Skipped: null, Triangles: kept.TriangleCount, Vanished: level.VanishedComponents, Whole: (level.Whole?.TriangleCount ?? kept.TriangleCount)));
        }
        oracleCycles += ((ThreadCycles.Now() - started) - (work.Inside - inside));

        foreach (var piece in pieceFields) { piece.Dispose(); }

        var measured = new Group(
            BakeEvaluations: bakeEvaluations,
            Cell: cell,
            Collapse: collapse,
            CollapseCycles: collapseCycles,
            ContourCycles: contourCycles,
            ContourEvaluations: contourEvaluations,
            Field: field,
            Follow: follow,
            HalfDiagonal: levels.HalfDiagonal,
            Lipschitz: string.Create(provider: CultureInfo.InvariantCulture, handler: $"step scale {union.StepScale:0.###}, lower/upper {levels.LowerOverUpper:0.###} at ({levels.LowerOverUpperWitness.At.X:0.####}, {levels.LowerOverUpperWitness.At.Y:0.####}, {levels.LowerOverUpperWitness.At.Z:0.####}) value {Mm(metres: levels.LowerOverUpperWitness.Value)} mm upper {Mm(metres: levels.LowerOverUpperWitness.Upper)} mm{(levels.Certified ? ", certified" : ", uncertified")}"),
            Members: Math.Max(val1: 1, val2: members.Count),
            OracleCycles: oracleCycles,
            OracleEvaluations: work.Oracle,
            Reference: reference,
            SurfaceRefused: levels.SurfaceRefused,
            SurfaceWitnesses: levels.SurfaceWitnesses,
            Triangles: level0.TriangleCount
        ) { Shaded = [], Unshaded = [] };

        return measured with { Shaded = Judge(group: measured, normals: true), Unshaded = Judge(group: measured, normals: false) };
    }
    /// <summary>Judges a group's field levels in order against the level each follows, the collapse's coarsest first:
    /// each level is kept or refused by the plan's clauses, and the chain ends at the first empty one.</summary>
    /// <param name="group">The measurement.</param>
    /// <param name="normals">Whether a level's error counts its normal component; without it a level's error is the
    /// largest of its geometric, material and vanished components.</param>
    /// <param name="waived">The clauses left out (<c>share</c>, <c>open</c>, <c>flips</c>, <c>missing</c>, <c>outside</c>,
    /// <c>refused</c>, <c>dominated</c>), to show what each rejects; none for the plan's judgement.</param>
    /// <returns>Each field level's verdict, finest first, up to the first empty one.</returns>
    public static IReadOnlyList<Verdict> Judge(Group group, bool normals, params IReadOnlyList<string> waived) {
        var verdicts = new List<Verdict>();
        var last = group.Follow;

        foreach (var row in group.Field) {
            if ((row.Error is not { } error) || (row.Audit is not { } audit)) {
                verdicts.Add(item: new Verdict(Error: last.Error, Kept: false, Level: row.Level, Measured: false, Reasons: (row.Skipped ?? "empty"), Triangles: 0));
                if (row.Skipped is "empty") { break; }
                continue;
            }

            var own = (normals ? error.Recorded : MathF.Max(x: error.Geometric, y: MathF.Max(x: error.Material.Largest, y: error.Vanished.Largest)));
            var raised = MathF.Max(x: last.Error, y: own);
            var reasons = new List<string>();

            if (row.Triangles > (SdfMeshCollapse.KeptShare * last.Triangles)) { reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"share {row.Triangles}>{SdfMeshCollapse.KeptShare}x{last.Triangles}")); }
            if (audit.OpenEdges > group.Reference.OpenEdges) { reasons.Add(item: $"open {audit.OpenEdges}"); }
            if (audit.Flipped > group.Reference.Flipped) { reasons.Add(item: $"flips {audit.Flipped}"); }
            if (audit.MissingCrossings > group.Reference.MissingCrossings) { reasons.Add(item: $"missing {audit.MissingCrossings}"); }
            if (error.Outside > 0) { reasons.Add(item: $"outside {error.Outside}"); }
            if (error.Refused > 0) { reasons.Add(item: $"refused {error.Refused}"); }
            if (raised >= group.HalfDiagonal) { reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"dominated {Mm(metres: raised)}>={Mm(metres: group.HalfDiagonal)} mm")); }

            reasons.RemoveAll(match: reason => waived.Contains(value: reason[..reason.IndexOf(comparisonType: StringComparison.Ordinal, value: ' ')]));

            var kept = (reasons.Count == 0);

            verdicts.Add(item: new Verdict(Error: raised, Kept: kept, Level: row.Level, Measured: true, Reasons: (kept ? "kept" : string.Join(separator: " ", values: reasons)), Triangles: row.Triangles));
            if (kept) { last = (row.Triangles, raised); }
        }

        return verdicts;
    }
    /// <summary>Returns which component of a level's error is largest: <c>geometric</c>, <c>material</c>,
    /// <c>normal</c> or <c>vanished</c>.</summary>
    /// <param name="error">The level's measurement.</param>
    /// <returns>The binding component.</returns>
    public static string Binds(SdfMeshLevelError error) =>
        new (string Name, float Value)[] { ("geometric", error.Geometric), ("material", error.Material.Largest), ("normal", error.Normal.Largest), ("vanished", error.Vanished.Largest) }.MaxBy(keySelector: static component => component.Value).Name;
    /// <summary>Writes one group's table.</summary>
    /// <param name="label">The group's label.</param>
    /// <param name="group">The measurement.</param>
    /// <param name="output">The report.</param>
    public static void Write(string label, Group group, StringBuilder output) {
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels {label}: cell {Mm(metres: group.Cell)} mm, half-diagonal {Mm(metres: group.HalfDiagonal)} mm, members {group.Members}, surface witnesses {group.SurfaceWitnesses} ({group.SurfaceRefused} refused), {group.Lipschitz}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  L0 merged: tris {group.Triangles} open {group.Reference.OpenEdges} flips {group.Reference.Flipped} missing {group.Reference.MissingCrossings}\n");
        foreach (var row in group.Collapse) { WriteRow(kind: "collapse", output: output, row: row, shaded: null, unshaded: null); }
        foreach (var row in group.Field) {
            WriteRow(kind: "field", output: output, row: row, shaded: group.Shaded.FirstOrDefault(predicate: verdict => (verdict.Level == row.Level)), unshaded: group.Unshaded.FirstOrDefault(predicate: verdict => (verdict.Level == row.Level)));
        }
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  chain: {Chain(group: group, verdicts: group.Shaded)} | without normals: {Chain(group: group, verdicts: group.Unshaded)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  cost: bake-evals {group.BakeEvaluations} contour-evals {group.ContourEvaluations} oracle-evals {group.OracleEvaluations}; {ThreadCycles.Unit} contour {group.ContourCycles} oracle {group.OracleCycles} collapse {group.CollapseCycles}\n");
    }
    /// <summary>Returns a group's coarsest nonempty level under a judgement: its last kept field level, or the
    /// collapse's coarsest (level zero's where it has none).</summary>
    /// <param name="group">The measurement.</param>
    /// <param name="verdicts">The judgement (<see cref="Judge"/>).</param>
    /// <returns>The level's triangles and recorded error.</returns>
    public static (int Triangles, float Error) Floor(Group group, IReadOnlyList<Verdict> verdicts) =>
        ((verdicts.LastOrDefault(predicate: static verdict => verdict.Kept) is { } last) ? (last.Triangles, last.Error) : group.Follow);
    /// <summary>Returns whether a group's judged chain runs to where the field gives out: its coarsest contoured level
    /// that holds a mesh is kept, and only an empty lattice or the group's extent follows it, so the terminal level
    /// follows a kept one directly.</summary>
    /// <param name="verdicts">The judgement.</param>
    /// <returns><see langword="true"/> when the chain reaches the empty level unbroken.</returns>
    public static bool ReachesEmpty(IReadOnlyList<Verdict> verdicts) {
        var last = -1;

        for (var at = 0; (at < verdicts.Count); at++) {
            if (verdicts[at].Measured) { last = at; }
        }

        return ((last >= 0) && verdicts[last].Kept && verdicts.Skip(count: (last + 1)).All(predicate: static verdict => (verdict.Reasons == "empty")));
    }

    private static string Chain(Group group, IReadOnlyList<Verdict> verdicts) {
        var text = new StringBuilder();

        text.Append(provider: CultureInfo.InvariantCulture, handler: $"{((group.Collapse.Count > 0) ? $"C{group.Collapse.Count}" : "L0")} {group.Follow.Triangles} ({Mm(metres: group.Follow.Error)} mm)");
        foreach (var verdict in verdicts.Where(predicate: static verdict => verdict.Kept)) {
            text.Append(provider: CultureInfo.InvariantCulture, handler: $" > F{verdict.Level} {verdict.Triangles} ({Mm(metres: verdict.Error)} mm)");
        }
        text.Append(provider: CultureInfo.InvariantCulture, handler: $" > empty ({Mm(metres: group.HalfDiagonal)} mm)");

        return text.ToString();
    }
    // The level-zero triangles a level accounts for by their run's extent, as a mesh the audit excuses missing crossings
    // near; null when there are none.
    private static SdfMesh? Accounted(SdfMesh level0, IReadOnlyList<bool> vanished) {
        var indices = level0.Indices.Span;
        var materials = level0.TriangleMaterials.Span;
        var kept = new List<uint>();
        var keptMaterials = new List<uint>();

        for (var triangle = 0; (triangle < vanished.Count); triangle++) {
            if (!vanished[triangle]) { continue; }
            kept.Add(item: indices[(3 * triangle)]);
            kept.Add(item: indices[((3 * triangle) + 1)]);
            kept.Add(item: indices[((3 * triangle) + 2)]);
            if (!materials.IsEmpty) { keptMaterials.Add(item: materials[triangle]); }
        }

        return ((kept.Count == 0) ? null : SdfMeshAudit.Subset(indices: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: kept), materials: keptMaterials.ToArray(), mesh: level0, sources: out _));
    }
    private static void WriteRow(string kind, Row row, StringBuilder output, Verdict? shaded, Verdict? unshaded) {
        var name = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{((kind == "field") ? "F" : "C")}{row.Level}");

        if (row.Error is not { } error) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {name} {kind}: cell {Mm(metres: row.Cell)} tris 0 ({row.Skipped}){((row.Vanished > 0) ? $", {row.Vanished} components of {row.Whole} triangles vanished" : string.Empty)}\n");
            return;
        }

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {name} {kind}: cell {Mm(metres: row.Cell)} budget {Mm(metres: row.Offset)} tris {row.Triangles} of {row.Whole} ({row.Vanished} vanished) error {Mm(metres: error.Recorded)} binds {Binds(error: error)}");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | m>s max {Mm(metres: error.MeshToSurface.Max)} p99 {Mm(metres: error.MeshToSurface.P99)} lower {Mm(metres: error.LowerMax)} (n {error.MeshToSurface.Count} refused {error.Refused} interior {error.Interior} lower>upper {error.LowerOverUpper})");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | s>m max {Mm(metres: error.SurfaceToMesh.Max)} p99 {Mm(metres: error.SurfaceToMesh.P99)} outside {error.Outside} ({Mm(metres: error.OutsideMax)})");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | material {error.Material.Count}/{error.Material.Triangles} {Mm(metres: error.Material.Largest)} normal {error.Normal.Count}/{error.Normal.Triangles} {Mm(metres: error.Normal.Largest)} (nearest-only {error.NormalNearest.Count}/{error.NormalNearest.Triangles} {Mm(metres: error.NormalNearest.Largest)}) vanished {error.Vanished.Count} {Mm(metres: error.Vanished.Largest)}");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | open {row.Audit?.OpenEdges} flips {row.Audit?.Flipped} folds {row.Audit?.Folds} missing {row.Audit?.MissingCrossings}/{row.Audit?.Crossings}");
        if (shaded is not null) { output.Append(provider: CultureInfo.InvariantCulture, handler: $" | (a,b) {shaded.Reasons} | without normals {unshaded?.Reasons}"); }
        output.Append(value: '\n');
    }
    private static string Mm(float metres) => MeshCommand.Millimetres(format: "0.###", metres: metres);

    /// <summary>One level of a group, field or collapse.</summary>
    /// <param name="Level">The level: a collapse level's ordinal, or <c>k</c> for a field level of cell <c>2^k</c> times
    /// level zero's.</param>
    /// <param name="Cell">The level's cell (a collapse level's contour cell), in metres.</param>
    /// <param name="Offset">The level's budget: a field level's offset, a collapse level's own recorded error, in
    /// metres.</param>
    /// <param name="Triangles">The level's triangles.</param>
    /// <param name="Whole">A field level's triangles before components vanished.</param>
    /// <param name="Vanished">The components a field level vanished.</param>
    /// <param name="Error">The measurement (a field level's unraised), or <see langword="null"/> where the level holds no
    /// mesh.</param>
    /// <param name="Audit">The audit of the level as a mesh of its own, or <see langword="null"/>.</param>
    /// <param name="Skipped">Why the level holds no mesh, or <see langword="null"/>.</param>
    public sealed record Row(int Level, float Cell, float Offset, int Triangles, int Whole, int Vanished, SdfMeshLevelError? Error, SdfMeshAuditReport? Audit, string? Skipped);
    /// <summary>A field level's verdict under a judgement.</summary>
    /// <param name="Level">The field level.</param>
    /// <param name="Triangles">Its triangles.</param>
    /// <param name="Error">The error it records, raised to the level it follows.</param>
    /// <param name="Measured">Whether it held a mesh the clauses judged.</param>
    /// <param name="Kept">Whether it is kept.</param>
    /// <param name="Reasons"><c>kept</c>, or why it is refused or holds nothing.</param>
    public sealed record Verdict(int Level, int Triangles, float Error, bool Measured, bool Kept, string Reasons);
    /// <summary>One group's measurement.</summary>
    /// <param name="Cell">Level zero's contour cell, in metres.</param>
    /// <param name="HalfDiagonal">The group's half box diagonal, in metres: the terminal empty level's error.</param>
    /// <param name="Members">The members its levels are contoured per: one for a closed group.</param>
    /// <param name="Triangles">Level zero's triangles.</param>
    /// <param name="Reference">Level zero's audit.</param>
    /// <param name="SurfaceWitnesses">Level zero's surface witnesses the oracle measures.</param>
    /// <param name="SurfaceRefused">Level zero's witnesses no bracket reached.</param>
    /// <param name="Collapse">The collapse's levels.</param>
    /// <param name="Follow">The triangles and recorded error of the level the field levels follow: the collapse's
    /// coarsest, or level zero.</param>
    /// <param name="Field">The field levels, finest first.</param>
    /// <param name="BakeEvaluations">The field evaluations the offline bake's lattices of the group took.</param>
    /// <param name="ContourEvaluations">The field levels' contour evaluations, facing included.</param>
    /// <param name="OracleEvaluations">The oracle's evaluations.</param>
    /// <param name="ContourCycles">The field levels' contours' own processor work, field evaluations excluded.</param>
    /// <param name="OracleCycles">The oracle's and the audits' own processor work.</param>
    /// <param name="CollapseCycles">The collapse's processor work.</param>
    /// <param name="Lipschitz">The whole field's step scale and how it held against level zero's brackets.</param>
    public sealed record Group(float Cell, float HalfDiagonal, string Lipschitz, int Members, int Triangles, SdfMeshAuditReport Reference, int SurfaceWitnesses, int SurfaceRefused, IReadOnlyList<Row> Collapse, (int Triangles, float Error) Follow, IReadOnlyList<Row> Field, long BakeEvaluations, long ContourEvaluations, long OracleEvaluations, ulong ContourCycles, ulong OracleCycles, ulong CollapseCycles) {
        /// <summary>Gets the field levels' verdicts with the normal component counted.</summary>
        public required IReadOnlyList<Verdict> Shaded { get; init; }
        /// <summary>Gets the field levels' verdicts without the normal component.</summary>
        public required IReadOnlyList<Verdict> Unshaded { get; init; }
    }

    // One group's tally of field evaluations by what they serve, and the measuring thread's work spent inside them.
    private sealed class Work {
        public long Contour;
        public long Oracle;
        public ulong Inside;

        public void Count(SdfMeshLevelWork work, long evaluations, ulong cycles) {
            if (work == SdfMeshLevelWork.Contour) { Contour += evaluations; } else { Oracle += evaluations; }
            Inside += cycles;
        }
    }
    // CLI-only attribution around the engine's shared CPU sampler.
    private sealed class Field(SdfProgram program, Work work) : ISdfMeshLevelField, IDisposable {
        private readonly SdfMeshCpuField m_field = new(program: program);

        public float StepScale => m_field.StepScale;

        public void Dispose() => m_field.Dispose();
        public void Values(ReadOnlySpan<Vector3> points, Span<float> values, Span<int> materials, SdfMeshLevelWork kind) {
            var started = ThreadCycles.Now();

            m_field.Values(materials: materials, points: points, values: values, work: kind);
            work.Count(kind, points.Length, (ThreadCycles.Now() - started));
        }
        public void Gradients(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<Vector3> gradients, SdfMeshLevelWork kind) {
            var started = ThreadCycles.Now();

            m_field.Gradients(gradients: gradients, points: points, spacing: spacing, values: values, work: kind);
            work.Count(kind, (5L * points.Length), (ThreadCycles.Now() - started));
        }
    }


}
