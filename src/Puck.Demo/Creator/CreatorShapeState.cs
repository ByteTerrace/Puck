using System.Numerics;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>
/// One authored shape in the creator scene — the richer successor of the frame source's private CreatorShape. Position
/// and Rotation ride the shape's per-frame dynamic-transform slot (a move or a spin never rebuilds the program); Scale,
/// MaterialIndex, Blend, Smooth, and GroupId are BAKED into the program, so changing any of them flags a rebuild (safe
/// under the worst-case word envelope the renderer probes at construction — see CreatorSceneRenderer).
/// </summary>
/// <param name="Id">The shape's stable id (unique within the scene; survives deletes/reorders — names and console
/// selection key on it).</param>
/// <param name="Name">An optional player-given name (console <c>creator.name</c>); null until named.</param>
/// <param name="Type">The primitive this shape draws (the canonical dimensions live in <see cref="AvatarDefinition"/>).</param>
/// <param name="Position">The shape's render-relative position (its dynamic slot's position).</param>
/// <param name="Rotation">The shape's orientation (its dynamic slot's quaternion).</param>
/// <param name="Scale">The shape's per-axis scale (baked via <see cref="SdfOp.Scale"/>).</param>
/// <param name="MaterialIndex">The scene-palette slot this shape colors from (0..CreatorScene.PaletteSize-1).</param>
/// <param name="Blend">How this shape combines with the shapes before it IN ITS GROUP; non-Union blends are honored
/// only within a group (group id != 0) — the instance-cull contract (never smooth-blend across a maskable instance
/// boundary) makes that structural, not advisory.</param>
/// <param name="Smooth">The blend radius for the smooth blend variants (0 for the hard ops).</param>
/// <param name="GroupId">The composition group this shape belongs to (0 = ungrouped; grouped shapes emit as ONE
/// workbench-bounded instance and blend in document order).</param>
/// <param name="Mirror">Whether the shape's local field mirrors across its local X=0 plane (<see cref="SdfProgramBuilder.SymmetryX"/>,
/// a POINT op applied BEFORE the primitive). The STYLE page's West button toggles it.</param>
/// <param name="Twist">The shape's local twist rate about Y, in radians per unit of local Y (<see cref="SdfOp.TwistY"/>,
/// a POINT op applied before the primitive; 0 = untwisted). Clamped to ±<see cref="CreatorScene.MaxTwist"/>.</param>
/// <param name="Bend">The shape's local bend rate about Y, in radians per unit of local Y (<see cref="SdfOp.BendY"/>,
/// a POINT op applied before the primitive, after <see cref="Twist"/>; 0 = unbent). Clamped to ±<see cref="CreatorScene.MaxBend"/>.</param>
/// <param name="Dilate">The shape's inflation radius (<see cref="SdfOp.Dilate"/>, a FIELD op applied AFTER the
/// primitive, before <see cref="Onion"/>; 0 = off). Clamped to [0, <see cref="CreatorScene.MaxDilate"/>].</param>
/// <param name="Onion">The shape's shell thickness (<see cref="SdfOp.Onion"/>, a FIELD op applied AFTER the primitive;
/// 0 = solid). Clamped to [0, <see cref="CreatorScene.MaxOnion"/>].</param>
public readonly record struct CreatorShapeState(
    int Id,
    string? Name,
    AvatarPrimitive Type,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    int MaterialIndex,
    SdfBlendOp Blend,
    float Smooth,
    int GroupId,
    bool Mirror = false,
    float Twist = 0f,
    float Bend = 0f,
    float Dilate = 0f,
    float Onion = 0f
);
