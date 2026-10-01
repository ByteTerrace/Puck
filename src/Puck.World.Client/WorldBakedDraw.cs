using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>What a presentation draws of a ready bake: its mesh, and the impostor a view draws in its place when the
/// placement is small on screen (<see cref="SdfMeshLod"/>).</summary>
/// <param name="Mesh">The baked mesh, in the creation's engine frame like an inline mesh, with its normals, texture
/// coordinates, triangle materials and surface textures.</param>
/// <param name="Impostor">The bake's impostor, or <see langword="null"/> when its textures cannot be drawn, in which case
/// the mesh draws at every size.</param>
public sealed record WorldBakedDraw(SdfMesh Mesh, SdfMeshImpostor? Impostor);
