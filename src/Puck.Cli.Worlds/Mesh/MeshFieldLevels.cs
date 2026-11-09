using System.Globalization;
using System.Numerics;
using System.Text;

using Puck.Cli.Host;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh audit --cpu --field-levels</c>: for each selected group the offline bake meshed, builds its field-levels
/// chain (<see cref="SdfMeshFieldLevels"/>, per member for a group whose shapes ride several slots) and measures both it
/// and the collapse's levels of the same mesh by the error contract, with the audit's open edges, flips and missing
/// crossings on each level, the field evaluations each path takes (the bake's lattices, the chain's contours and its
/// oracle apart) and the processor work of each path's own CPU stages on the thread that runs them
/// (<see cref="ThreadCycles"/>), field evaluations excluded since a World answers them on the GPU.
/// </summary>
internal static class MeshFieldLevels {
    /// <summary>Measures one group's chain and collapse levels.</summary>
    /// <param name="group">The group.</param>
    /// <param name="scene">The rest scene it was planned from.</param>
    /// <param name="mesh">The offline bake's landed mesh of the group.</param>
    /// <param name="reference">Level zero's audit report.</param>
    /// <param name="bakeEvaluations">The field evaluations the offline bake's lattices of the group took.</param>
    /// <param name="options">The chain's options.</param>
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

        var level0 = mesh with { Levels = [] };

        // The chain's own work, its contours apart from its oracle (the surface witnesses and each level's measurement).
        var started = ThreadCycles.Now();
        var inside = work.Inside;
        var levels = new SdfMeshFieldLevels(cell: cell, field: union, options: options, reference: SdfMeshAudit.Surface(level: 0, mesh: mesh));
        var oracleCycles = ((ThreadCycles.Now() - started) - (work.Inside - inside));

        started = ThreadCycles.Now();
        inside = work.Inside;
        var contoured = levels.Contour(pieces: pieces, preserveFeatures: preserve);
        var contourCycles = ((ThreadCycles.Now() - started) - (work.Inside - inside));

        started = ThreadCycles.Now();
        inside = work.Inside;
        var chain = levels.Chain(contoured: contoured, pieces: pieces);
        oracleCycles += ((ThreadCycles.Now() - started) - (work.Inside - inside));
        var chainCycles = (contourCycles + oracleCycles);
        var chainContour = work.Contour;
        var chainOracle = work.Oracle;

        // The collapse's own work: each piece's collapse over its merged mesh, as Land runs it.
        started = ThreadCycles.Now();
        foreach (var piece in ((mesh.Chunks.Count > 0) ? mesh.Chunks : [mesh])) {
            _ = SdfMeshCollapse.Levels(mesh: (piece with { Levels = [] }), preserveFeatures: preserve);
        }
        var collapseCycles = (ThreadCycles.Now() - started);

        var collapseCount = ((mesh.Chunks.Count > 0) ? mesh.Chunks.Max(selector: static chunk => chunk.Levels.Count) : mesh.Levels.Count);
        var collapse = new List<Row>();
        var finer = 0f;

        for (var level = 1; (level <= collapseCount); level++) {
            var surface = SdfMeshAudit.Surface(level: level, mesh: mesh);
            var recorded = ((mesh.Chunks.Count > 0) ? mesh.Chunks.Where(predicate: chunk => (chunk.Levels.Count > 0)).Max(selector: chunk => chunk.Levels[Math.Min(val1: level, val2: chunk.Levels.Count) - 1].Error) : mesh.Levels[(level - 1)].Error);
            var error = levels.Measure(budget: recorded, cell: cell, finer: finer, level: surface, members: null, pieces: []);

            finer = error.Recorded;
            collapse.Add(item: new Row(Audit: SdfMeshAudit.Audit(field: auditField, mesh: surface, options: new SdfMeshAuditOptions { Clusters = 0 }, shapes: []), Cell: cell, Error: error, Level: level, Offset: recorded, Recorded: error.Recorded, Skipped: null, Triangles: surface.TriangleCount));
        }

        var field = chain.Select(selector: level => new Row(
            Audit: ((level.Mesh is null) ? null : SdfMeshAudit.Audit(field: auditField, mesh: level.Mesh, options: new SdfMeshAuditOptions { Clusters = 0 }, shapes: [])),
            Cell: level.Cell,
            Error: level.Error,
            Level: level.Level,
            Offset: level.Offset,
            Recorded: level.Recorded,
            Skipped: level.Skipped,
            Triangles: level.Triangles
        )).ToList();

        foreach (var piece in pieceFields) { piece.Dispose(); }

        return new Group(
            BakeEvaluations: bakeEvaluations,
            Cell: cell,
            ChainContour: chainContour,
            ChainCycles: chainCycles,
            ContourCycles: contourCycles,
            ChainOracle: chainOracle,
            Collapse: collapse,
            CollapseCycles: collapseCycles,
            Field: field,
            HalfDiagonal: levels.HalfDiagonal,
            Lipschitz: string.Create(provider: CultureInfo.InvariantCulture, handler: $"step scale {union.StepScale:0.###}, lower/upper {levels.LowerOverUpper:0.###}{(levels.Certified ? ", certified" : ", uncertified")}"),
            Members: Math.Max(val1: 1, val2: members.Count),
            Reference: reference,
            SurfaceRefused: levels.SurfaceRefused,
            SurfaceWitnesses: levels.SurfaceWitnesses,
            Triangles: level0.TriangleCount
        );
    }

    /// <summary>Writes one group's table.</summary>
    /// <param name="label">The group's label.</param>
    /// <param name="group">The measurement.</param>
    /// <param name="output">The report.</param>
    public static void Write(string label, Group group, StringBuilder output) {
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels {label}: cell {Mm(group.Cell)} mm, half-diagonal {Mm(group.HalfDiagonal)} mm, members {group.Members}, surface witnesses {group.SurfaceWitnesses} ({group.SurfaceRefused} refused), {group.Lipschitz}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  L0 merged: tris {group.Triangles} open {group.Reference.OpenEdges} flips {group.Reference.Flipped} missing {group.Reference.MissingCrossings}\n");
        foreach (var row in group.Collapse) { WriteRow(kind: "collapse", output: output, row: row); }
        foreach (var row in group.Field) { WriteRow(kind: "field", output: output, row: row); }

        var a = ClauseA(group: group);
        var unshaded = ClauseA(group: group, normals: false);
        var b = ClauseB(group: group);

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  cost: bake-evals {group.BakeEvaluations} chain-evals contour {group.ChainContour} oracle {group.ChainOracle}; {ThreadCycles.Unit} chain contour {group.ContourCycles} oracle {(group.ChainCycles - group.ContourCycles)} collapse {group.CollapseCycles}; (a) {(a ?? "pass")} (a without normals) {(unshaded ?? "pass")} (b) {(b ?? "pass")}\n");
    }

    /// <summary>Returns why a group fails clause (a): some collapse level whose measured error the finest field level
    /// at no more triangles does not meet, or a chain with fewer than two nonempty levels.</summary>
    /// <param name="group">The measurement.</param>
    /// <returns>The failure, or <see langword="null"/> when the group passes.</returns>
    /// <param name="normals">Whether the normal component counts; without it each level's error is the largest of its
    /// geometric, material and vanished components, raised to the finer level's, on both sides.</param>
    public static string? ClauseA(Group group, bool normals = true) {
        var field = Errors(normals: normals, rows: group.Field);
        var collapse = Errors(normals: normals, rows: group.Collapse);

        for (var index = 0; (index < group.Collapse.Count); index++) {
            var level = group.Collapse[index];
            var match = Enumerable.Range(start: 0, count: group.Field.Count).FirstOrDefault(predicate: at => ((group.Field[at].Triangles > 0) && (group.Field[at].Triangles <= level.Triangles)), defaultValue: (group.Field.Count - 1));

            if (field[match] > collapse[index]) {
                return string.Create(provider: CultureInfo.InvariantCulture, handler: $"C{level.Level} {level.Triangles} tris {Mm(collapse[index])} mm vs F{group.Field[match].Level} {group.Field[match].Triangles} tris {Mm(field[match])} mm");
            }
        }

        return null;
    }
    // Each level's error, with or without its normal component, raised to the finer level's; a level with no
    // measurement keeps its recorded error.
    private static float[] Errors(IReadOnlyList<Row> rows, bool normals) {
        var errors = new float[rows.Count];
        var finer = 0f;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            finer = ((row.Error is not { } error) ? MathF.Max(x: finer, y: row.Recorded) : (normals ? row.Recorded : MathF.Max(x: finer, y: MathF.Max(x: error.Geometric, y: MathF.Max(x: error.Material.Largest, y: error.Vanished.Largest)))));
            errors[index] = finer;
        }

        return errors;
    }
    /// <summary>Returns why a group fails clause (b): a field level with more open edges, flips or missing crossings
    /// than level zero, or a surface witness outside it.</summary>
    /// <param name="group">The measurement.</param>
    /// <returns>The failure, or <see langword="null"/> when the group passes.</returns>
    public static string? ClauseB(Group group) {
        foreach (var row in group.Field) {
            if ((row.Audit is not { } audit) || (row.Error is not { } error)) { continue; }

            var reasons = new List<string>();

            if (audit.OpenEdges > group.Reference.OpenEdges) { reasons.Add(item: $"open {audit.OpenEdges}"); }
            if (audit.Flipped > group.Reference.Flipped) { reasons.Add(item: $"flips {audit.Flipped}"); }
            if (audit.MissingCrossings > group.Reference.MissingCrossings) { reasons.Add(item: $"missing {audit.MissingCrossings}"); }
            if (error.Outside > 0) { reasons.Add(item: $"outside {error.Outside}"); }
            if (reasons.Count > 0) { return $"F{row.Level} {string.Join(separator: " ", values: reasons)}"; }
        }

        return null;
    }
    /// <summary>Returns which component of a level's error is largest: <c>geometric</c>, <c>material</c>,
    /// <c>normal</c>, <c>vanished</c>, or <c>finer</c> where the finer level's error was raised to.</summary>
    /// <param name="row">The level.</param>
    /// <returns>The binding component.</returns>
    public static string Binds(Row row) {
        if (row.Error is not { } error) { return "-"; }

        var components = new (string Name, float Value)[] { ("geometric", error.Geometric), ("material", error.Material.Largest), ("normal", error.Normal.Largest), ("vanished", error.Vanished.Largest) };
        var largest = components.MaxBy(keySelector: static component => component.Value);

        return ((largest.Value < error.Recorded) ? "finer" : largest.Name);
    }

    private static void WriteRow(string kind, Row row, StringBuilder output) {
        if (row.Error is not { } error) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {((kind == "field") ? "F" : "C")}{row.Level} {kind}: cell {Mm(row.Cell)} tris 0 recorded {Mm(row.Recorded)} mm ({row.Skipped})\n");
            return;
        }

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {((kind == "field") ? "F" : "C")}{row.Level} {kind}: cell {Mm(row.Cell)} budget {Mm(row.Offset)} tris {row.Triangles} recorded {Mm(row.Recorded)} binds {Binds(row: row)}");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | m>s max {Mm(error.MeshToSurface.Max)} p99 {Mm(error.MeshToSurface.P99)} lower {Mm(error.LowerMax)} (n {error.MeshToSurface.Count} refused {error.Refused} interior {error.Interior} lower>upper {error.LowerOverUpper})");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | s>m max {Mm(error.SurfaceToMesh.Max)} p99 {Mm(error.SurfaceToMesh.P99)} outside {error.Outside} ({Mm(error.OutsideMax)})");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | material {error.Material.Count}/{error.Material.Triangles} {Mm(error.Material.Largest)} normal {error.Normal.Count}/{error.Normal.Triangles} {Mm(error.Normal.Largest)} (nearest-only {error.NormalNearest.Count}/{error.NormalNearest.Triangles} {Mm(error.NormalNearest.Largest)}) vanished {error.Vanished.Count} {Mm(error.Vanished.Largest)}");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $" | open {row.Audit?.OpenEdges} flips {row.Audit?.Flipped} folds {row.Audit?.Folds} missing {row.Audit?.MissingCrossings}/{row.Audit?.Crossings}\n");
    }
    private static string Mm(float metres) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(metres * 1000f):0.###}");

    /// <summary>One level of a group, field or collapse.</summary>
    /// <param name="Level">The level, one for the first coarser.</param>
    /// <param name="Cell">The level's cell (a collapse level's contour cell), in metres.</param>
    /// <param name="Offset">The level's budget: a field level's offset, a collapse level's own recorded error, in
    /// metres.</param>
    /// <param name="Triangles">The level's triangles.</param>
    /// <param name="Recorded">The error the contract records for it, in metres.</param>
    /// <param name="Error">The measurement, or <see langword="null"/> where the level holds no mesh.</param>
    /// <param name="Audit">The audit of the level as a mesh of its own, or <see langword="null"/>.</param>
    /// <param name="Skipped">Why the level holds no mesh, or <see langword="null"/>.</param>
    public sealed record Row(int Level, float Cell, float Offset, int Triangles, float Recorded, SdfMeshLevelError? Error, SdfMeshAuditReport? Audit, string? Skipped);

    /// <summary>One group's measurement.</summary>
    /// <param name="Cell">Level zero's contour cell, in metres.</param>
    /// <param name="HalfDiagonal">The group's half box diagonal, in metres.</param>
    /// <param name="Members">The members its levels are contoured per: one for a closed group.</param>
    /// <param name="Triangles">Level zero's triangles.</param>
    /// <param name="Reference">Level zero's audit.</param>
    /// <param name="SurfaceWitnesses">Level zero's surface witnesses the oracle measures.</param>
    /// <param name="SurfaceRefused">Level zero's witnesses no bracket reached.</param>
    /// <param name="Collapse">The collapse's levels.</param>
    /// <param name="Field">The field chain's levels, the terminal empty level last.</param>
    /// <param name="BakeEvaluations">The field evaluations the offline bake's lattices of the group took.</param>
    /// <param name="ChainContour">The chain's contour evaluations.</param>
    /// <param name="ChainOracle">The chain's oracle evaluations.</param>
    /// <param name="ChainCycles">The chain's own processor work, field evaluations excluded.</param>
    /// <param name="ContourCycles">The part of it its contours took; the rest is its oracle's.</param>
    /// <param name="CollapseCycles">The collapse's processor work.</param>
    /// <param name="Lipschitz">The whole field's step scale and how it held against level zero's brackets.</param>
    public sealed record Group(float Cell, float HalfDiagonal, string Lipschitz, int Members, int Triangles, SdfMeshAuditReport Reference, int SurfaceWitnesses, int SurfaceRefused, IReadOnlyList<Row> Collapse, IReadOnlyList<Row> Field, long BakeEvaluations, long ChainContour, long ChainOracle, ulong ChainCycles, ulong ContourCycles, ulong CollapseCycles);

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

    // A group's field (or a member's) answered through the bake's CPU stand-in (MeshCpuBake), one evaluator a thread.
    private sealed class Field : ISdfMeshLevelField, IDisposable {
        private const int Serial = 256;

        private readonly ThreadLocal<(SdfFieldEvaluator Field, Func<Vector3, float> Read)> m_evaluators;
        private readonly Work m_work;

        public Field(SdfProgram program, Work work) {
            m_work = work;
            m_evaluators = new(valueFactory: () => MeshCpuBake.Tapped(field: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render)));
            StepScale = (float)(double)m_evaluators.Value.Field.StepScale;
        }

        public float StepScale { get; }

        public void Dispose() => m_evaluators.Dispose();

        public void Values(ReadOnlySpan<Vector3> points, Span<float> values, Span<int> materials, SdfMeshLevelWork work) {
            var started = ThreadCycles.Now();
            var at = points.ToArray();
            var answers = new float[at.Length];
            var found = new int[at.Length];

            void One(int index) {
                answers[index] = MeshCpuBake.Sample(field: m_evaluators.Value.Field, material: out found[index], point: at[index]);
            }

            if (at.Length < Serial) {
                for (var index = 0; (index < at.Length); index++) { One(index: index); }
            } else {
                _ = Parallel.For(fromInclusive: 0, toExclusive: at.Length, body: One);
            }
            answers.CopyTo(destination: values);
            found.CopyTo(destination: materials);
            m_work.Count(cycles: (ThreadCycles.Now() - started), evaluations: at.Length, work: work);
        }

        public void Gradients(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<Vector3> gradients, SdfMeshLevelWork work) {
            var started = ThreadCycles.Now();
            var at = points.ToArray();
            var answers = new float[at.Length];
            var slopes = new Vector3[at.Length];
            var step = MathF.Max(x: MeshCpuBake.MinimumSpacing, y: spacing);

            void One(int index) {
                var (field, read) = m_evaluators.Value;

                answers[index] = MeshCpuBake.Sample(field: field, material: out _, point: at[index]);
                slopes[index] = SdfMeshBakeTaps.Gradient(field: read, point: at[index], spacing: step);
            }

            if (at.Length < Serial) {
                for (var index = 0; (index < at.Length); index++) { One(index: index); }
            } else {
                _ = Parallel.For(fromInclusive: 0, toExclusive: at.Length, body: One);
            }
            answers.CopyTo(destination: values);
            slopes.CopyTo(destination: gradients);
            // A gradient sample is its value and four taps, as the bake counts a gradient lattice's taps.
            m_work.Count(cycles: (ThreadCycles.Now() - started), evaluations: (5L * at.Length), work: work);
        }
    }
}
