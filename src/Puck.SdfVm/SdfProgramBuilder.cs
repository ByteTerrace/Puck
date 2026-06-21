using System.Numerics;

namespace Puck.SdfVm;

public sealed class SdfProgramBuilder {
    // KEEP IN SYNC with SDF_SCREEN_MATERIAL in Shaders/sdf-vm.glsl.
    public const int ScreenMaterialId = 65535;

    private readonly List<SdfInstruction> m_instructions = [];
    private readonly List<SdfMaterial> m_materials = [];

    public int AddMaterial(SdfMaterial material) {
        m_materials.Add(item: material);

        return (m_materials.Count - 1);
    }
    public SdfProgramBuilder ResetPoint() {
        return Transform(op: SdfOp.ResetPoint);
    }
    public SdfProgramBuilder Translate(Vector3 offset) {
        return Transform(
            data0: new Vector4(
                value: offset,
                w: 0f
            ),
            op: SdfOp.Translate
        );
    }
    public SdfProgramBuilder Rotate(Quaternion rotation) {
        return Transform(
            data0: new Vector4(
                w: rotation.W,
                x: rotation.X,
                y: rotation.Y,
                z: rotation.Z
            ),
            op: SdfOp.Rotate
        );
    }
    public SdfProgramBuilder Scale(Vector3 scale) {
        return Transform(
            data0: new Vector4(
                value: scale,
                w: 0f
            ),
            op: SdfOp.Scale
        );
    }
    /// <summary>Applies a rigid transform (translation + orientation) sourced at evaluation time from per-frame dynamic
    /// transform <paramref name="slot"/> — element <c>2*slot</c> is the position, <c>2*slot+1</c> the orientation
    /// quaternion in the renderer's dynamic-transform buffer. The shape that follows is repositioned each frame by
    /// updating that buffer, leaving this program (uploaded once) untouched. Honored only by the world render path
    /// (shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c>).</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based).</param>
    public SdfProgramBuilder TransformDynamic(int slot) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: slot,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.TransformDynamic
        );
    }
    public SdfProgramBuilder Repeat(Vector3 spacing) {
        return Transform(
            data0: new Vector4(
                value: spacing,
                w: 0f
            ),
            op: SdfOp.Repeat
        );
    }
    public SdfProgramBuilder RepeatLimited(Vector3 spacing, Vector3 limit) {
        return Transform(
            data0: new Vector4(
                value: spacing,
                w: 0f
            ),
            data1: new Vector4(
                value: limit,
                w: 0f
            ),
            op: SdfOp.RepeatLimited
        );
    }
    public SdfProgramBuilder SymmetryX() {
        return Transform(op: SdfOp.SymmetryX);
    }
    public SdfProgramBuilder SymmetryY() {
        return Transform(op: SdfOp.SymmetryY);
    }
    public SdfProgramBuilder SymmetryZ() {
        return Transform(op: SdfOp.SymmetryZ);
    }
    public SdfProgramBuilder Sphere(float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: radius,
                y: 0f,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Sphere,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Box(Vector3 halfExtents, float round, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: material,
            shape: SdfShapeType.Box,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Torus(float majorRadius, float minorRadius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: majorRadius,
                y: minorRadius,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Torus,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Plane(Vector3 normal, float offset, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: Vector3.Normalize(value: normal),
                w: offset
            ),
            material: material,
            shape: SdfShapeType.Plane,
            smooth: smooth
        );
    }
    public SdfProgramBuilder RoundCone(float lowerRadius, float upperRadius, float height, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: lowerRadius,
                y: upperRadius,
                z: height
            ),
            material: material,
            shape: SdfShapeType.RoundCone,
            smooth: smooth
        );
    }
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: ScreenMaterialId,
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    public SdfProgram Build() {
        return new SdfProgram(
            instructions: m_instructions,
            materials: m_materials
        );
    }

    private SdfProgramBuilder Transform(SdfOp op, Vector4 data0 = default, Vector4 data1 = default) {
        m_instructions.Add(item: new SdfInstruction(
            Blend: 0,
            Data0: data0,
            Data1: data1,
            Material: 0,
            Op: op,
            Shape: 0
        ));

        return this;
    }
    private SdfProgramBuilder Shape(SdfShapeType shape, Vector4 dimensions, int material, SdfBlendOp blend, float smooth) {
        m_instructions.Add(item: new SdfInstruction(
            Blend: (uint)blend,
            Data0: dimensions,
            Data1: new Vector4(
                w: 0f,
                x: smooth,
                y: 0f,
                z: 0f
            ),
            Material: (uint)material,
            Op: SdfOp.ShapeBlend,
            Shape: (uint)shape
        ));

        return this;
    }
}
