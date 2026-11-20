using System.Buffers.Binary;
using System.Collections.Concurrent;

using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.SignedDistance.Queries;

namespace Puck.Cli.Mesh;

/// <summary>How a CPU bake answered one group's gradient taps against where the bake kernel places them.</summary>
/// <param name="Samples">The gradient samples the group's lattices asked.</param>
/// <param name="Widened">The samples answered at <see cref="SdfMeshCpuField.ResolutionSpacing"/> because their lattice's
/// taps lie closer (<see cref="SdfMeshCpuField.TapSpacing"/>).</param>
/// <param name="Narrowest">The narrowest tap spacing any of the group's lattices asked, in metres; infinity for none.</param>
public readonly record struct MeshCpuTaps(long Samples, long Widened, float Narrowest) {
    /// <summary>Gets the tally of no samples.</summary>
    public static MeshCpuTaps None => new(Samples: 0L, Widened: 0L, Narrowest: float.PositiveInfinity);
}

/// <summary>
/// Bakes a world's groups offline, with no device: the baked-mesh path's own bake (<see cref="SdfMeshPathBake"/>) takes
/// its lattices as a World's background GPU work would, and each is answered on the CPU through the group's field
/// (<see cref="SdfMeshAudit.Field"/>, read by <see cref="SdfFieldEvaluator"/>) as the bake kernel
/// (<c>sdf-mesh-bake.comp.hlsl</c>) answers it on the GPU: a value, a material and a tetrahedral gradient
/// (<see cref="SdfMeshBakeTaps"/>) a sample, its taps where the lattice places them (<see cref="SdfMeshBakeRequest.TapSpacing"/>).
/// A group not selected is outside the bake's selection (<see cref="SdfMeshPathBake.Selection"/>) and bakes nothing, so a
/// selected group sharing its geometry bakes it itself. The planning, refinement and contouring are the bake's own, so a
/// mesh baked here differs from a World's only where the fixed-point evaluator's field differs from the kernel's and where
/// a lattice's taps lie closer than the evaluator resolves (<see cref="SdfMeshCpuField.TapSpacing"/>), which
/// <see cref="MeshCpuTaps"/> counts.
/// </summary>
public static class MeshCpuBake {
    // The value a group not selected answers at every sample: far outside any surface.
    private const float Empty = 1_000f;

    /// <summary>Bakes the groups of a rest scene whose field the evaluator reads; a group it refuses is left out.</summary>
    /// <param name="scene">The rest scene the groups were planned from.</param>
    /// <param name="farDistance">The far distance the world renders to.</param>
    /// <param name="groups">The groups to bake.</param>
    /// <param name="evaluations">Receives the field evaluations each group's lattices took by its selector (a gradient
    /// sample counting five, as the bake's taps do), or <see langword="null"/> to count none.</param>
    /// <param name="taps">Receives how each group's gradient taps were answered by its selector, or
    /// <see langword="null"/> to count none.</param>
    /// <param name="tier">The tier each group bakes at (<see cref="SdfMeshPathBake.PlannedTier"/>); <see langword="null"/>
    /// bakes each at its finest, the geometry's own detail.</param>
    /// <param name="refinements">Receives each group's refinement map by its selector, or <see langword="null"/> to keep
    /// none.</param>
    /// <returns>Each baked group's mesh by its selector; a group with no surface is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    public static Dictionary<int, SdfMesh> Bake(RestScene scene, float farDistance, IEnumerable<SdfMeshBakeGroup> groups, Dictionary<int, long>? evaluations = null, Dictionary<int, MeshCpuTaps>? taps = null, Func<SdfMeshBakeGroup, int>? tier = null, ConcurrentDictionary<int, SdfRefinementMap>? refinements = null) {
        ArgumentNullException.ThrowIfNull(argument: scene);
        ArgumentNullException.ThrowIfNull(argument: groups);

        return Bake(
            evaluations: evaluations,
            fields: groups
                .Select(selector: group => (Group: group, Field: SdfMeshAudit.Field(group: group, program: scene.Program)))
                .Where(predicate: static entry => SdfMeshAudit.TryEvaluator(field: out _, program: entry.Field, refusal: out _))
                .ToDictionary(keySelector: static entry => entry.Group.Selector, elementSelector: static entry => entry.Field),
            frame: new SdfFrame(Program: scene.Program, ProgramChanged: true, Time: 0f, Views: []) { DynamicTransforms = scene.Transforms, FarDistance = farDistance },
            refinements: refinements,
            taps: taps,
            tier: tier
        );
    }
    /// <summary>Bakes the selected groups of a program.</summary>
    /// <param name="frame">The frame the bake takes its program, transforms and far distance from.</param>
    /// <param name="fields">Each selected group's selector and its field (<see cref="SdfMeshAudit.Field"/>).</param>
    /// <param name="evaluations">Receives the field evaluations each group's lattices took by its selector, or
    /// <see langword="null"/> to count none.</param>
    /// <param name="taps">Receives how each group's gradient taps were answered by its selector, or
    /// <see langword="null"/> to count none.</param>
    /// <param name="tier">The tier each group bakes at; <see langword="null"/> bakes each at its finest.</param>
    /// <param name="refinements">Receives the map of which cells each group's final lattice flagged for finer sampling
    /// (<see cref="SdfRefinementMap"/>) by its selector, or <see langword="null"/> to keep none. A group taking another
    /// selected group's mesh (shared geometry) has none.</param>
    /// <returns>Each selected group's mesh by its selector; a group whose bake found no surface is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="fields"/> is
    /// <see langword="null"/>.</exception>
    public static Dictionary<int, SdfMesh> Bake(SdfFrame frame, IReadOnlyDictionary<int, SdfProgram> fields, Dictionary<int, long>? evaluations = null, Dictionary<int, MeshCpuTaps>? taps = null, Func<SdfMeshBakeGroup, int>? tier = null, ConcurrentDictionary<int, SdfRefinementMap>? refinements = null) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: fields);

        using var library = new SdfMeshLibrary();
        // With no view the bake would plan every group at its coarsest tier; offline it bakes the tier asked, else the finest.
        var bake = new SdfMeshPathBake {
            Library = library,
            PlannedTier = (tier ?? (static _ => 0)),
            RefinementObserver = ((refinements is null) ? null : ((selector, map) => refinements[selector] = map)),
            Residency = "cpu",
            Selection = fields.Keys.ToHashSet(),
        };
        using var dresser = new SdfMeshDresser(bake: bake);

        try {
            while (true) {
                // Read before the bake is asked what it owes, so a pool completion after this read ends the wait below.
                var seen = SdfMeshPathBake.Progress;

                _ = dresser.Dress(frame: frame);
                if (bake.TryTake(batch: out var batch, field: bake.Program)) {
                    bake.Complete(batch: batch, samples: Answer(batch: batch, evaluations: evaluations, fields: fields, taps: taps));
                    continue;
                }
                if (!bake.Pending) { break; }
                // The contour runs on the pool; its completion is what changes what the next take asks for. The timeout
                // only bounds a state no completion explains.
                _ = SdfMeshPathBake.WaitForProgress(since: seen, timeout: TimeSpan.FromMilliseconds(milliseconds: 250));
            }

            var meshes = new Dictionary<int, SdfMesh>();

            // The landed mesh, not the frame's draws: a chunked group draws each chunk under the chunk's own identity, and
            // its parent holds the whole surface the audit measures.
            foreach (var selector in fields.Keys) {
                if (bake.TryLandedMesh(mesh: out var mesh, selector: selector)) {
                    meshes[selector] = mesh;
                }
            }

            return meshes;
        } finally {
            dresser.Dispose();
            bake.Retire();
        }
    }

    // The bake kernel's answer to a batch: each lattice's samples, x fastest, grid or points.
    private static byte[] Answer(SdfMeshBakeBatch batch, IReadOnlyDictionary<int, SdfProgram> fields, Dictionary<int, long>? evaluations, Dictionary<int, MeshCpuTaps>? taps) {
        var bytes = new byte[batch.OutputBytes];
        var first = 0;

        foreach (var lattice in batch.Lattices) {
            var start = first;
            var request = lattice;

            first += lattice.OutputBytes;
            if (!fields.TryGetValue(key: lattice.Selector, value: out var program)) {
                for (var local = 0; local < lattice.Samples; local++) {
                    lattice.WriteSample(bytes.AsSpan(start, lattice.OutputBytes), local, Empty, 0, default);
                }
                continue;
            }
            if (evaluations is not null) { evaluations[lattice.Selector] = (evaluations.GetValueOrDefault(key: lattice.Selector) + lattice.Taps); }

            var spacing = SdfMeshCpuField.TapSpacing(requested: lattice.TapSpacing);

            if ((taps is not null) && lattice.Gradients) {
                var tally = taps.GetValueOrDefault(key: lattice.Selector, defaultValue: MeshCpuTaps.None);

                taps[lattice.Selector] = new MeshCpuTaps(
                    Narrowest: MathF.Min(x: tally.Narrowest, y: lattice.TapSpacing),
                    Samples: (tally.Samples + lattice.Samples),
                    Widened: (tally.Widened + ((spacing > lattice.TapSpacing) ? lattice.Samples : 0))
                );
            }

            _ = Parallel.For(
                fromInclusive: 0,
                toExclusive: lattice.Samples,
                localInit: () => SdfMeshCpuField.Tapped(field: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render)),
                body: (local, state, tapped) => {
                    var position = request.SampleAt(local: local);
                    var value = SdfMeshCpuField.Sample(field: tapped.Field, material: out var material, point: position);
                    var gradient = request.Gradients && !request.Points.IsEmpty
                        ? SdfMeshBakeTaps.Gradient(field: tapped.Read, point: position, spacing: spacing) : Vector3.Zero;
                    request.WriteSample(bytes.AsSpan(start, request.OutputBytes), local, value, material, gradient);

                    return tapped;
                },
                localFinally: static _ => { }
            );
        }

        return bytes;
    }
}
