using System.Buffers.Binary;
using System.Numerics;

using Puck.Maths;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Cli.Mesh;

/// <summary>
/// Bakes a world's groups offline, with no device: the baked-mesh path's own bake (<see cref="SdfMeshPathBake"/>) takes
/// its lattices as a World's background GPU work would, and each is answered on the CPU through the group's field
/// (<see cref="SdfMeshAudit.Field"/>, read by <see cref="SdfFieldEvaluator"/>) as the bake kernel
/// (<c>sdf-mesh-bake.comp.hlsl</c>) answers it on the GPU: a value, a material and a tetrahedral gradient a sample. A group
/// not selected is outside the bake's selection (<see cref="SdfMeshPathBake.Selection"/>) and bakes nothing, so a selected
/// group sharing its geometry bakes it itself. The planning, refinement and contouring are the
/// bake's own, so a mesh baked here differs from a World's only where the fixed-point evaluator's field differs from the
/// kernel's and in the gradient's stencil, which spans at least <see cref="MinimumSpacing"/> since the evaluator answers in
/// steps of 2⁻¹⁶ m.
/// </summary>
public static class MeshCpuBake {
    /// <summary>The narrowest spacing, in metres, of a gradient tap from its sample.</summary>
    public const float MinimumSpacing = 0.0005f;

    // The value a group not selected answers at every sample: far outside any surface.
    private const float Empty = 1_000f;

    private static readonly Vector3[] Taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

    /// <summary>Bakes the groups of a rest scene whose field the evaluator reads; a group it refuses is left out.</summary>
    /// <param name="scene">The rest scene the groups were planned from.</param>
    /// <param name="farDistance">The far distance the world renders to.</param>
    /// <param name="groups">The groups to bake.</param>
    /// <returns>Each baked group's mesh by its selector; a group with no surface is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    public static Dictionary<int, SdfMesh> Bake(RestScene scene, float farDistance, IEnumerable<SdfMeshBakeGroup> groups) {
        ArgumentNullException.ThrowIfNull(argument: scene);
        ArgumentNullException.ThrowIfNull(argument: groups);

        return Bake(
            fields: groups
                .Select(selector: group => (Group: group, Field: SdfMeshAudit.Field(group: group, program: scene.Program)))
                .Where(predicate: static entry => SdfMeshAudit.TryEvaluator(field: out _, program: entry.Field, refusal: out _))
                .ToDictionary(keySelector: static entry => entry.Group.Selector, elementSelector: static entry => entry.Field),
            frame: new SdfFrame(Program: scene.Program, ProgramChanged: true, Time: 0f, Views: []) { DynamicTransforms = scene.Transforms, FarDistance = farDistance }
        );
    }
    /// <summary>Bakes the selected groups of a program.</summary>
    /// <param name="frame">The frame the bake takes its program, transforms and far distance from.</param>
    /// <param name="fields">Each selected group's selector and its field (<see cref="SdfMeshAudit.Field"/>).</param>
    /// <returns>Each selected group's mesh by its selector; a group whose bake found no surface is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="fields"/> is
    /// <see langword="null"/>.</exception>
    public static Dictionary<int, SdfMesh> Bake(SdfFrame frame, IReadOnlyDictionary<int, SdfProgram> fields) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: fields);

        using var library = new SdfMeshLibrary();
        var bake = new SdfMeshPathBake { Library = library, Residency = "cpu", Selection = fields.Keys.ToHashSet() };
        using var dresser = new SdfMeshDresser(bake);
        try {
            while (true) {
                // Read before the bake is asked what it owes, so a pool completion after this read ends the wait below.
                var seen = SdfMeshPathBake.Progress;

                _ = dresser.Dress(frame: frame);
                if (bake.TryTake(batch: out var batch, field: bake.Program)) {
                    bake.Complete(batch: batch, samples: Answer(batch: batch, fields: fields));
                    continue;
                }
                if (!bake.Pending) { break; }
                // The contour runs on the pool; its completion is what changes what the next take asks for. The timeout
                // only bounds a state no completion explains.
                _ = SdfMeshPathBake.WaitForProgress(since: seen, timeout: TimeSpan.FromMilliseconds(250));
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
    private static byte[] Answer(SdfMeshBakeBatch batch, IReadOnlyDictionary<int, SdfProgram> fields) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var first = 0;

        foreach (var lattice in batch.Lattices) {
            var start = first;
            var request = lattice;

            first += lattice.Samples;
            if (!fields.TryGetValue(key: lattice.Selector, value: out var program)) {
                for (var sample = start; (sample < first); sample++) {
                    BinaryPrimitives.WriteSingleLittleEndian(destination: bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))), value: Empty);
                }
                continue;
            }

            var spacing = MathF.Max(MinimumSpacing, (0.015625f * MathF.Min(lattice.Cell.X, MathF.Min(lattice.Cell.Y, lattice.Cell.Z))));
            var slot = lattice.FrameSlot;

            _ = Parallel.For(
                fromInclusive: 0,
                toExclusive: lattice.Samples,
                localInit: () => new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render),
                body: (local, state, field) => {
                    var position = request.SampleAt(local: local);
                    var value = Sample(field: field, material: out var material, point: position);
                    var record = bytes.AsSpan(start: (((start + local) * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                    BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: value);
                    BinaryPrimitives.WriteInt32LittleEndian(destination: record[4..], value: material);
                    if (request.Gradients) {
                        var gradient = Vector3.Zero;

                        foreach (var tap in Taps) {
                            gradient += (tap * Sample(field: field, material: out _, point: (position + (tap * spacing))));
                        }
                        // The four taps sum to four times the spacing times the gradient.
                        gradient *= (0.25f / spacing);
                        BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                        BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                        BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
                    } else if (!request.Points.IsEmpty) {
                        BinaryPrimitives.WriteInt32LittleEndian(destination: record[8..], value: slot);
                    }

                    return field;
                },
                localFinally: static _ => { }
            );
        }

        return bytes;
    }
    // The field's value and material at a point; empty space where the field answers nothing.
    private static float Sample(SdfFieldEvaluator field, Vector3 point, out int material) =>
        (field.TryDistance(distance: out var distance, material: out material, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))) ? (float)((double)distance) : Empty);
}
