using System.Numerics;

// The mesh's own namespace, so every law that names a mesh reads its planes the same way.
namespace Puck.SdfVm;

/// <summary>
/// A law's read of a mesh's planes: each a copy taken through the mesh's hold (<see cref="SdfMesh.Hold"/>), the one door
/// to a mesh's positions, indices, normals and triangle materials, so a law reads a published mesh's planes whether or
/// not they are resident and keeps no view of them past the hold.
/// </summary>
internal static class SdfMeshPlanes {
    extension(SdfMesh mesh) {
        /// <summary>Gets a copy of the mesh's vertex positions.</summary>
        public ReadOnlyMemory<Vector3> Positions {
            get {
                using var held = mesh.Hold();

                return held.Positions.ToArray();
            }
        }
        /// <summary>Gets a copy of the mesh's own triangles' indices.</summary>
        public ReadOnlyMemory<uint> Indices {
            get {
                using var held = mesh.Hold();

                return held.Indices.ToArray();
            }
        }
        /// <summary>Gets a copy of the mesh's vertex normals, or none.</summary>
        public ReadOnlyMemory<Vector3> Normals {
            get {
                using var held = mesh.Hold();

                return held.Normals.ToArray();
            }
        }
        /// <summary>Gets a copy of the mesh's triangle materials, or none.</summary>
        public ReadOnlyMemory<uint> TriangleMaterials {
            get {
                using var held = mesh.Hold();

                return held.TriangleMaterials.ToArray();
            }
        }

        /// <summary>Returns a copy of a level's indices, zero naming the mesh's own triangles.</summary>
        /// <param name="level">The level.</param>
        /// <returns>The copy.</returns>
        public ReadOnlyMemory<uint> LevelIndices(int level) {
            using var held = mesh.Hold();

            return held.LevelIndices(level: level).ToArray();
        }
        /// <summary>Returns a copy of a level's triangle materials, zero naming the mesh's own triangles.</summary>
        /// <param name="level">The level.</param>
        /// <returns>The copy.</returns>
        public ReadOnlyMemory<uint> LevelMaterials(int level) {
            using var held = mesh.Hold();

            return held.LevelMaterials(level: level).ToArray();
        }
    }
}
