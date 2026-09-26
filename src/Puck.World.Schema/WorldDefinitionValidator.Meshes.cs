using System.Numerics;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The prototype rows: every creation document, then every inline mesh, and every placement that would carry a mesh
    // where only a static stamp draws one.
    private static HashSet<string> ValidatePrototypes(WorldDefinition definition, IReadOnlyList<WorldPrototype> creations, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
        var ids = ValidateCreations(
            creations: creations,
            definition: definition,
            errors: errors,
            fontNames: fontNames,
            hasTextCatalog: hasTextCatalog
        );

        if (creations is null) {
            return ids;
        }

        var meshPrototypes = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < creations.Count); index++) {
            if (
                (creations[index] is not { Mesh: { } mesh } creation) ||
                (creation.Document is null)
            ) {
                continue;
            }

            var path = $"prototypes[{index}]";

            ValidatePrototypeMesh(
                errors: errors,
                mesh: mesh,
                paletteCount: (creation.Document.Palette?.Count ?? 0),
                path: $"{path}.mesh"
            );

            // An animated creation's placements ride the stamp pool, which draws its shapes and no mesh.
            if ((creation.Document.Frames is { Count: > 0 }) || (creation.Document.Drivers is { Count: > 0 })) {
                errors.Add(item: $"{path}.mesh is refused on an animated creation — a mesh is a static-stamp facet.");
            }

            _ = meshPrototypes.Add(item: creation.Id.ToString());
        }

        if (meshPrototypes.Count == 0) {
            return ids;
        }

        var placements = definition.Placements;

        for (var index = 0; (index < placements.Count); index++) {
            if (
                (placements[index] is not { } placement) ||
                !meshPrototypes.Contains(item: placement.PrototypeId)
            ) {
                continue;
            }

            // An inhabited or attached placement rides a body's or a parent's pose through the stamp pool, which
            // draws no mesh.
            if (placement.Inhabit is not null) {
                errors.Add(item: $"placements.rows[{index}] inhabits prototype '{placement.PrototypeId}', whose mesh only a static placement draws.");
            } else if (placement.Attach is not null) {
                errors.Add(item: $"placements.rows[{index}] attaches prototype '{placement.PrototypeId}', whose mesh only a static placement draws.");
            }
        }

        return ids;
    }
    // One inline mesh: at least one triangle, whole triangles, every index naming a vertex, finite vertices, no
    // degenerate triangle (a repeated index or zero area), and a material the creation's palette declares.
    private static void ValidatePrototypeMesh(WorldPrototypeMesh mesh, int paletteCount, string path, List<string> errors) {
        var vertices = (mesh.Vertices ?? []);
        var indices = (mesh.Indices ?? []);

        if ((vertices.Count == 0) || (indices.Count == 0)) {
            errors.Add(item: $"{path} is empty — a mesh needs at least one triangle.");

            return;
        }
        if ((indices.Count % 3) != 0) {
            errors.Add(item: $"{path}.indices holds {indices.Count} entries, not a whole number of triangles.");

            return;
        }
        if ((mesh.Material < 0) || (mesh.Material >= paletteCount)) {
            errors.Add(item: $"{path}.material {mesh.Material} names no entry of the creation's {paletteCount}-entry palette.");
        }

        var finite = true;

        for (var vertex = 0; (vertex < vertices.Count); vertex++) {
            var position = vertices[vertex];

            if (!float.IsFinite(f: position.X) || !float.IsFinite(f: position.Y) || !float.IsFinite(f: position.Z)) {
                errors.Add(item: $"{path}.vertices[{vertex}] is not finite.");
                finite = false;
            }
        }

        var inRange = true;

        for (var index = 0; (index < indices.Count); index++) {
            if (indices[index] >= ((uint)vertices.Count)) {
                errors.Add(item: $"{path}.indices[{index}] names vertex {indices[index]}, past the mesh's {vertices.Count} vertices.");
                inRange = false;
            }
        }
        if (!finite || !inRange) {
            return;
        }

        for (var triangle = 0; (triangle < (indices.Count / 3)); triangle++) {
            var (a, b, c) = (indices[(triangle * 3)], indices[((triangle * 3) + 1)], indices[((triangle * 3) + 2)]);

            if ((a == b) || (b == c) || (a == c)) {
                errors.Add(item: $"{path} triangle {triangle} is degenerate: it names vertex {((a == b) ? a : c)} twice.");

                continue;
            }

            var area = Vector3.Cross(
                vector1: (vertices[((int)b)] - vertices[((int)a)]),
                vector2: (vertices[((int)c)] - vertices[((int)a)])
            );

            if (area.LengthSquared() == 0f) {
                errors.Add(item: $"{path} triangle {triangle} is degenerate: its vertices {a}, {b} and {c} enclose no area.");
            }
        }
    }
}
