namespace Puck.SignedDistance;

internal enum SdfReliefStreamRole { None, ReliefOwner, ReliefRow }

/// <summary>Field instructions and their CPU and shader facets.</summary>
[SdfFieldCatalogue("SdfOp", unsigned: true)]
public static partial class SdfFieldOps {
    private abstract class Entry {
        // The shared formula (SdfFieldFormulas) every reader lowers for what the op does to the walk: a point op's map of
        // the local point, a field op's map of the running distance. Null where the op is the walk's own control (a
        // reset, a scope, a join) or its readers are hand-written twins.
        public virtual SdfFormula? Formula => null;
        // The formula for the factor a point op multiplies into the distance scale, where it has one.
        public virtual SdfFormula? DistanceFormula => null;
        public abstract SdfShaderAction Shader { get; }
        public abstract SdfShaderAction RigidShader { get; }
        public abstract SdfShaderAction PartShader { get; }
        public abstract bool PartDomain { get; }
        public abstract SdfPointAction Point { get; }
        public abstract SdfIntervalAction Interval { get; }
        public abstract SdfIntervalFoldAction IntervalFold { get; }
        public abstract SdfIntervalFieldEditAction IntervalFieldEdit { get; }
        public abstract SdfRenderWarpAction RenderWarp { get; }
        public abstract SdfRenderWarpBoundsAction RenderWarpBounds { get; }
        public abstract SdfFieldStructureAction FieldStructure { get; }
        public abstract SdfDeferredScopeAction DeferredScope { get; }
        public abstract SdfCullChainAction CullChain { get; }
        public abstract SdfCullInstancesAction CullInstances { get; }
        public abstract SdfCertificateChainAction CertificateChain { get; }
        public abstract SdfAdmissionAction Admission { get; }
        public abstract SdfReliefPointFactorAction ReliefPointFactor { get; }
        public abstract SdfStepScaleBinderAction StepScaleBinder { get; }
        public abstract SdfCompositionLipschitzAction CompositionLipschitz { get; }
        public abstract SdfChainLipschitzAction ChainLipschitz { get; }
        public abstract SdfSkipSphereAction SkipSphere { get; }
        public abstract SdfDistanceGainAction DistanceGain { get; }
        public abstract SdfRigidPlanAction RigidPlan { get; }
        public abstract SdfScopeClampAction ScopeClamp { get; }
        public abstract SdfWarpBoundAction WarpBound { get; }
        public abstract SdfScopeReachAction ScopeReach { get; }
        public abstract SdfUnboundedFoldAction UnboundedFold { get; }
        public abstract bool ReadsPoint { get; }
        public abstract bool PartPosePrefix { get; }
        public abstract bool Contact { get; }
        public abstract bool RenderOnly { get; }
        public abstract SdfOpRole Role { get; }
        public abstract SdfReliefStreamRole Stream { get; }
        public abstract SdfFieldResource Resources { get; }
    }

    /// <summary>Returns the external field inputs this entry needs.</summary>
    public static SdfFieldResource ResourcesOf(SdfOp value) => GetResourcesOf(value: value);

    /// <summary>Returns the inputs read by the instruction and its shape or blend.</summary>
    public static SdfFieldResource ResourcesOf(SdfInstruction instruction) {
        var resources = ResourcesOf(value: instruction.Op);

        if ((resources & SdfFieldResource.Shapes) != 0) {
            resources |= SdfFieldShapes.ResourcesOf(value: ((SdfShapeType)instruction.Shape));
        }
        if ((resources & SdfFieldResource.Blend) != 0) {
            resources |= SdfFieldBlends.ResourcesOf(value: ((SdfBlendOp)instruction.Blend));
        }
        return resources;
    }

    [SdfFieldEntry(0)]
    private sealed class ResetPoint : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                localPosition = worldPosition;
                distanceScale = 1.0;
                currentSlot = SDF_TRANSFORM_SLOT_NONE;
                #ifdef SDF_DYNAMIC_TRANSFORMS
                rootDynamicIndex = index + 1u;
                #endif
                laneErodeSkipShape = false;
                laneErodeAmount = 0.0;
                if (trackMaterial) {
                    parityMaterialDelta = 0;
                }
                uint frameLength = SDF_RESET_FRAME_LENGTH(instructionHeader);
                if (frameLength != 0u) {
                    if (frameLength >= segmentMeta.w - index - 1u) { return sdfIsaErrorHit(); }
                    uint key = SDF_RESET_FRAME_KEY(instructionHeader);
                    if (key == 0u || key > index + 1u) { return sdfIsaErrorHit(); }

                    if (key == frameKey && framePending == SDF_SEGMENT_NONE) {
                        localPosition = framePosition;
                        distanceScale = frameScale;
                        currentSlot = frameSlot;
                        index += frameLength;
                    } else {
                        frameKey = key;
                        framePending = index + frameLength + 1u;
                    }
                }
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.ResetPoint;
        public override SdfIntervalAction Interval => SdfIntervalAction.ResetPoint;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Allowed;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Identity;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Zero;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.ResetPoint;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.ResetPoint;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.ResetPoint;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.ResetPoint;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => false;
        public override bool PartPosePrefix => true;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Structure;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.ResetSlot;
    }

    [SdfFieldEntry(1)]
    private sealed class Translate : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.TranslatePoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Rigid(("p", Formula!));
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Translate;
        public override SdfIntervalAction Interval => SdfIntervalAction.Translate;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Allowed;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Translate;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Translate;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Translate;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Translate;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Identity;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Translate;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => true;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    [SdfFieldEntry(2)]
    private sealed class Rotate : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.RotatePoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Rigid(("p", Formula!));
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Rotate;
        public override SdfIntervalAction Interval => SdfIntervalAction.Rotate;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Allowed;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Rotate;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Rotate;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Rotate;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Identity;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Rotate;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => true;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    [SdfFieldEntry(3)]
    private sealed class Scale : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.ScalePoint;
        public override SdfFormula? DistanceFormula => SdfFieldFormulas.ScaleDistance;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!), ("distanceScale", DistanceFormula!));
        public override SdfShaderAction RigidShader => new(Code: "break;", Operands: SdfShaderOperands.None);
        public override SdfShaderAction PartShader => SdfShaderAction.Part(("p", Formula!), ("distanceScale", DistanceFormula!));
        public override bool PartDomain => true;
        public override SdfPointAction Point => SdfPointAction.Scale;
        public override SdfIntervalAction Interval => SdfIntervalAction.Scale;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Allowed;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Scale;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Scale;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Scale;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Scale;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Scale;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Scale;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.IdentityScale;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => true;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>A rigid transform (translation + orientation) read at evaluation time from a per-frame dynamic-transform
    /// buffer slot (Data0.x = slot index) rather than from immediate instruction data: element <c>2*slot</c> is the
    /// position (xyz), <c>2*slot+1</c> the orientation quaternion. Lets a moving entity (player, enemy, carried screen)
    /// be repositioned each frame by updating a small buffer, without re-uploading the static scene program. Honored only
    /// by shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c> (the world path); a no-op elsewhere.</summary>
    [SdfFieldEntry(4)]
    private sealed class TransformDynamic : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                #ifdef SDF_DYNAMIC_TRANSFORMS
                // A rigid transform sourced from a per-frame buffer slot (data0.x): place the shape at the slot's world
                // position + orientation by moving the sample point into the shape's local frame (translate then
                // inverse-rotate), exactly like an immediate Translate + Rotate would.
                SDF_VM_LOAD_DATA0;
                uint dynamicSlot = (uint)data0.x;
                if (index == rootDynamicIndex) {
                    localPosition = sdfWorldPointInSlot(worldPosition, dynamicSlot, dynamicRoot);
                } else {
                    localPosition = sdfPointInSlot(localPosition, dynamicSlot);
                }
                currentSlot = (int)dynamicSlot;
                break;
                #else
                return sdfIsaErrorHit();
                #endif
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Unsupported;
        public override SdfIntervalAction Interval => SdfIntervalAction.Unsupported;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.TransformDynamic;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.TransformDynamic;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Identity;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.TransformDynamic;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => true;
        public override bool Contact => false;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.DynamicSlot;
    }

    /// <summary>Rotates a plane (Shape: XY=0, YZ=1, XZ=2) driven by a coordinate (Blend: X=0, Y=1, Z=2).
    /// Angle = Data0.x * (driver - Data0.y); inverse-domain rotation is (c*u+s*v, -s*u+c*v).</summary>
    [SdfFieldEntry(5)]
    private sealed class RotatePlane : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                uint u = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_YZ) ? SDF_AXIS_Y : SDF_AXIS_X;
                uint v = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_XY) ? SDF_AXIS_Y : SDF_AXIS_Z;
                uint driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                if (driver >= 3u) { return sdfIsaErrorHit(); }
                float angle = data0.x * (sdfComponent(localPosition, driver) - data0.y);
                float c = cos(angle), sn = sin(angle);
                float pu = sdfComponent(localPosition, u), pv = sdfComponent(localPosition, v);
                sdfSetComponent(localPosition, u, c * pu + sn * pv);
                sdfSetComponent(localPosition, v, -sn * pu + c * pv);
                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Warp;
        public override SdfIntervalAction Interval => SdfIntervalAction.Warp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.RotatePlane;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.RotatePlane;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Elongates the shape that follows by clamping the point into a box (Data0.xyz = extents): the shape's
    /// cross-section is swept over <c>±extents</c> — the classic capsule-from-sphere operator.</summary>
    [SdfFieldEntry(6)]
    private sealed class Elongate : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.ElongatePoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Fold;
        public override SdfIntervalAction Interval => SdfIntervalAction.Fold;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Elongate;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Elongate;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    [SdfFieldEntry(7)]
    private sealed class ShapeBlend : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                // Consume the pending SDF_OP_LANE_ERODE effect (if any) FIRST, before every other skip path below,
                // so it never leaks onto a later, unrelated shape (a bound-culled
                // shape still clears it here).
                bool laneEroded = laneErodeSkipShape;
                float laneErosion = laneErodeAmount;
                laneErodeSkipShape = false;
                laneErodeAmount = 0.0;

                if (laneEroded) {
                    break;
                }

                SDF_VM_LOAD_DATA0;
                SDF_VM_LOAD_DATA1;
                // The per-shape flavour of the segment early-out above (same exactness argument): inside an
                // EVALUATED segment, a Union shape whose own (tighter) sphere cannot beat the running minimum
                // skips just its evaluation — always sound, because shape ops never mutate chain state.
                uint4 shapeBoundMeta = sdfProgramWord(boundsOffset + (SDF_BOUND_RECORD_VECTORS * index) + 1u);

                [branch]
                if (shapeBoundMeta.x != SDF_BOUND_NONE
                ) {
                    float4 shapeBound = asfloat(sdfProgramWord(boundsOffset + (SDF_BOUND_RECORD_VECTORS * index)));
                    float3 shapeBoundCenter = shapeBound.xyz;
                    bool shapeBoundReady = (shapeBoundMeta.x == SDF_BOUND_STATIC);

                #ifdef SDF_DYNAMIC_TRANSFORMS
                    if (shapeBoundMeta.x == SDF_BOUND_DYNAMIC) {
                        shapeBoundCenter = sdfDynamicBoundCenter(shapeBoundCenter, shapeBoundMeta.y);
                        shapeBoundReady = true;
                    }
                #endif

                    if (shapeBoundReady && (result.distance <= SDF_FAR_DISTANCE)) {
                        float3 toShapeCenter = (worldPosition - shapeBoundCenter);
                        float shapeClearance = max((result.distance + shapeBound.w), 0.0);

                        if (dot(toShapeCenter, toShapeCenter) >= (shapeClearance * shapeClearance)) {
                            break;
                        }
                    }
                }

                uint shapeType = SDF_INSTRUCTION_SHAPE(instructionHeader);
                float candidate = ((evaluateShape(shapeType, localPosition, data0, data1) * distanceScale) + laneErosion);

                // Hand the DISTANCE-SCALED (plus any pending lane erosion) candidate to the shared blend tail below.
                float shapeStepScale = asfloat(SDF_SHAPE_STEP_SCALE(shapeBoundMeta));
                composeCandidate = shapeStepScale > 0.0 ? candidate * shapeStepScale : candidate;
                composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
                composeSmooth = data1.x;
                composePending = true;

                if (trackMaterial) {
                    int material = (int)SDF_INSTRUCTION_MATERIAL(instructionHeader);

                    // An active wallpaper stride recolors this shape by its cell key — never a screen sentinel (the whole
                    // >= SDF_SCREEN_MATERIAL range, not just the exact value: a screen-instance id must survive intact).
                    if ((material < SDF_SCREEN_MATERIAL) && (parityMaterialDelta != 0)) {
                        material += parityMaterialDelta;
                    }

                    composeMaterial = material;
                    composeInstance = segmentInstance;
                    composeSlot = currentSlot;
                }
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.ShapeBlend;
        public override SdfIntervalAction Interval => SdfIntervalAction.ShapeBlend;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.ShapeBlend;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.ShapeBlend;
        public override SdfCullChainAction CullChain => SdfCullChainAction.ShapeBlend;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.ShapeBlend;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.ShapeBlend;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.ShapeBlend;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.ShapeBlend;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.ShapeBlend;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.ShapeBlend;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.ShapeBlend;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.ShapeBlend;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.ShapeBlend;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.ShapeBlend;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Structure;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => (SdfFieldResource.Shapes | SdfFieldResource.Blend);
    }

    [SdfFieldEntry(8)]
    private sealed class Repeat : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.RepeatPoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Rigid(("p", Formula!));
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Fold;
        public override SdfIntervalAction Interval => SdfIntervalAction.Fold;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Repeat;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Repeat;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Fold;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Unbounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Lattice;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    [SdfFieldEntry(9)]
    private sealed class RepeatLimited : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.RepeatLimitedPoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Rigid(("p", Formula!));
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Fold;
        public override SdfIntervalAction Interval => SdfIntervalAction.Fold;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.RepeatLimited;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.RepeatLimited;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Fold;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.RepeatLimit;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Lattice;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Shells the entire field accumulated so far: <c>d = abs(d) − thickness</c> (Data0.x = thickness) turns
    /// solids into hollow skins. A field op, not a point op — order objects so it follows everything it should shell.</summary>
    [SdfFieldEntry(10)]
    private sealed class Onion : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.OnionField;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("distance", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.EditField;
        public override SdfIntervalAction Interval => SdfIntervalAction.EditField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Onion;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Onion;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.Data0X;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Inflates the entire field accumulated so far by a radius: <c>d −= radius</c> (Data0.x = radius) rounds
    /// and fattens everything before it. A field operation, not a point operation.</summary>
    [SdfFieldEntry(11)]
    private sealed class Dilate : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.DilateField;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("distance", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.EditField;
        public override SdfIntervalAction Interval => SdfIntervalAction.EditField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Dilate;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Dilate;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.Data0X;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Folds the evaluation point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry
    /// group (all 17 IUC groups; square/rectangular lattices plus the equilateral hex lattice for P3 and up). The
    /// lattice reduction is <see cref="RepeatLimited"/> restricted to two axes (P1 is bit-identical to it); the
    /// per-cell stage composes mirrors/rotations keyed on the lattice parity. Every branch is an isometry, so
    /// distances are preserved. Instruction lanes: Shape = <see cref="SdfWallpaperGroup"/>, Blend =
    /// <see cref="SdfPlane"/>, Material = the parity-material stride (the cell key — checker parity or hex
    /// 3-coloring — strides the material id of later shape wins in the chain; 0 keeps the fold purely geometric).
    /// Data0.xy = cell extents (hex: pitch = x), Data0.zw = their exact reciprocals (<see cref="SdfWallpaperFold.InverseCell"/>),
    /// Data1.xy = the cell limits (<see cref="SdfWallpaperFold.LimitRefusal"/>: whole numbers on a square lattice,
    /// unbounded on a hex lattice); Data1.zw are unused.</summary>
    [SdfFieldEntry(12)]
    private sealed class WallpaperFold : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                // header lanes: y = the wallpaper group, z = the fold plane (0 = XZ, 1 = XY, 2 = YZ), w = the
                // parity-material stride. The fold is an isometry, so distanceScale is untouched.
                uint group = SDF_INSTRUCTION_SHAPE(instructionHeader);
                uint plane = SDF_INSTRUCTION_BLEND(instructionHeader);
                uint axisA = ((plane == SDF_PLANE_YZ) ? SDF_AXIS_Y : SDF_AXIS_X);
                uint axisB = ((plane == SDF_PLANE_XY) ? SDF_AXIS_Y : SDF_AXIS_Z);
                float2 cellIndex;
                float2 cellPoint = float2(sdfComponent(localPosition, axisA), sdfComponent(localPosition, axisB));
                float2 folded = sdfWallpaperFoldCell(cellPoint, group, data0.xy, data0.zw, data1.xy, cellIndex);

                sdfSetComponent(localPosition, axisA, folded.x);
                sdfSetComponent(localPosition, axisB, folded.y);

                // The stride recolors the shapes the fold repeats: the cell key strides their declared material, so
                // each lattice cell selects its own row of the palette. Distances never depend on it.
                if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                    parityMaterialDelta = (sdfWallpaperCellKey(group, cellIndex) * (int)SDF_INSTRUCTION_MATERIAL(instructionHeader));
                }

                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.WallpaperFold;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.WallpaperFold;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.WallpaperFold;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.WallpaperLimit;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Lattice;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Log-spherical domain warp: tiles space into infinite self-similar "Droste" shells by folding the
    /// radial log-coordinate to the nearest shell — a translation along <c>log(radius)</c> becomes a uniform scaling
    /// in Cartesian space, so one authored prototype shell repeats outward/inward as scaled copies from a handful of
    /// instructions. Data0.x = w (the log cell size = <c>ln(shellRatio)</c>, host-baked); Data0.y = twist (radians of
    /// per-shell Z-spin — the isometric Droste spiral; 0 = concentric shells); Data0.z = 1/w (host-baked reciprocal).
    /// Not an isometry (space scales by ~r), so the runtime folds the r/density correction into the per-candidate
    /// <c>distanceScale</c> (exactly like <see cref="Scale"/>) and <c>AnalyzeLipschitz</c> bakes a conservative
    /// <c>exp(w/2)</c> step clamp so the over-relaxed march cannot tunnel through a shell boundary. Folds only the
    /// radial coordinate (theta/phi are preserved, so there is no polar pinching); like <see cref="Repeat"/>, prototype
    /// content should respect the shell cell (radii within a factor of <c>shellRatio</c>).</summary>
    [SdfFieldEntry(13)]
    private sealed class LogSphere : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                float foldRadius = max(length(localPosition), SDF_LOGSPHERE_MIN_RADIUS);
                float logRadius = log(foldRadius);
                float shell = round(logRadius * data0.z);   // nearest shell index k
                float shellScale = exp(shell * data0.x);    // exp(k*w) = the shell's Cartesian scale = r / rFolded

                localPosition /= shellScale;                // fold every shell onto the prototype

                // Optional Droste spiral: rotate each shell about Z by k*twist. UNCONDITIONAL (twist == 0 -> cos 0 = 1,
                // sin 0 = 0, an exact identity on both backends) so no divergent branch re-rolls codegen. A rotation is
                // an ISOMETRY: it neither touches distanceScale nor contributes to the Lipschitz factor. No atan2, so
                // the Z axis is a plain rotation fixed line, NOT a polar-pinch singularity.
                float spinAngle = (shell * data0.y);
                float spinCos = cos(spinAngle);
                float spinSin = sin(spinAngle);

                localPosition.xy = float2(
                    ((spinCos * localPosition.x) - (spinSin * localPosition.y)),
                    ((spinSin * localPosition.x) + (spinCos * localPosition.y)));

                // The r/density metric correction: within a shell the fold is a similarity that shrank space by
                // 1/shellScale, so evaluateShape(localPosition) UNDERESTIMATES true distance by 1/shellScale — multiply
                // the candidate back by shellScale. This rides the SAME per-candidate distanceScale channel SDF_OP_SCALE
                // writes (applied per candidate below), composing multiplicatively when a Scale is also on the chain.
                // NEVER stepScale (that clamps the FINAL distance once, and cannot per-candidate-correct a mixed chain).
                distanceScale *= shellScale;
                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.LogSphere;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.LogSphere;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.LogSphere;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.LogSphere;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Unbounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Lattice;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Stochastic domain-repeat fold: tiles space into cells like <see cref="Repeat"/>, then per cell applies a
    /// hashed position displacement, an optional hashed orientation ("tumble"), and an optional hashed material variant —
    /// scattering one prototype into a jittered field from a single instruction. Both the displacement and the tumble are
    /// isometries (translate + rotate), so distances are preserved: the field stays 1-Lipschitz, no cull bound changes,
    /// and <c>AnalyzeLipschitz</c> needs only one conservative reach line (the jitter half-amplitude). Instruction lanes:
    /// Shape = seed (uint), Blend = <see cref="SdfNoiseFlavor"/> (how the position offset is distributed — White = 0 the
    /// byte-identical default, Blue, Gaussian; tumble and material variant are unaffected, and every flavor shares the same
    /// offset bound so no Lipschitz change), Material = materialVariants (0 = geometric only). Data0.xyz = spacing
    /// (world units/axis), Data0.w = jitter (peak-to-peak position displacement), Data1.xyz = 1/spacing (host-baked, like
    /// <see cref="Repeat"/>), Data1.w = tumble in [0,1] (0 = no rotation; 1 = up to ±π about a random axis). jitter,
    /// tumble, and materialVariants each default to an exact identity (amplitude 0 / count 0), so an unused-and-zeroed
    /// instruction leaves the point byte-identical on both backends. The per-cell hash is integer-only (canonical PCG3D
    /// on the two's-complement cell index xored with the seed), so it is bit-identical across DXC's SPIR-V and DXIL
    /// targets — only the final uint→float and the tumble trig carry the usual ±1 LSB warp noise. Like <see cref="Repeat"/>,
    /// keep the in-cell content clear of the <c>round()</c> boundary: jitter/2 + prototype radius ≤ min(spacing)/2.</summary>
    [SdfFieldEntry(14)]
    private sealed class CellJitter : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                float3 cell = round(localPosition * data1.xyz);   // data1.xyz = 1/spacing (host-baked)
                // asuint(int3(cell)) is well-defined two's-complement for a negative rounded index on both backends.
                uint3 seed = uint3(SDF_INSTRUCTION_SHAPE(instructionHeader), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_A), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_B));
                uint3 key = (asuint(int3(cell)) ^ seed);
                uint3 h0 = sdfPcg3d(key);   // still used by the material-variant apply below (every flavor)

                // The POSITION offset r0, shaped by the Blend-lane noise flavor (KEEP IN SYNC with
                // Puck.SignedDistance.SdfNoiseFlavor). Only r0 changes. Every flavor stays in [0, 1] per axis — CLOSED at 1,
                // because (float)0xFFFFFFFFu rounds up to 2^32 — so (r0 - 0.5) * jitter holds within +-jitter/2 per
                // axis (White's bound) and the AnalyzeLipschitz reach term is unchanged for all three.
                uint noiseFlavor = SDF_INSTRUCTION_BLEND(instructionHeader);
                float3 r0;
                if (noiseFlavor == SDF_NOISE_BLUE) {
                    // The R3 low-discrepancy lattice: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596
                    // (the real root of x^4 = x + 1), as a fixed-point rank-1 lattice on the integer cell index. The uint
                    // mul-add wraps mod 2^32 = the fractional part, so it is BIT-IDENTICAL across DXC's SPIR-V and DXIL
                    // (a float frac would diverge). The three alphas are ROTATED across the axes so the offset components
                    // decorrelate. "Blue-ish" low-discrepancy (de-clumped), NOT true isotropic blue noise. The seed folds
                    // in ADDITIVELY (a lattice translation preserves low-discrepancy, unlike an xor), so the field varies
                    // with seed AND a non-zero seed breaks the circulant's (1,1,1) main-diagonal degeneracy.
                    uint3 uc = (asuint(int3(cell)) + seed);
                    uint bx = ((uc.x * SDF_R3_ALPHA1) + (uc.y * SDF_R3_ALPHA2) + (uc.z * SDF_R3_ALPHA3));
                    uint by = ((uc.x * SDF_R3_ALPHA2) + (uc.y * SDF_R3_ALPHA3) + (uc.z * SDF_R3_ALPHA1));
                    uint bz = ((uc.x * SDF_R3_ALPHA3) + (uc.y * SDF_R3_ALPHA1) + (uc.z * SDF_R3_ALPHA2));
                    r0 = (float3(bx, by, bz) * SDF_INV_2POW32);
                } else if (noiseFlavor == SDF_NOISE_GAUSSIAN) {
                    // Central-limit: the mean of 3 decorrelated hashed uniforms per axis — a Bates(3) distribution, i.e.
                    // bell-SHAPED and clustered toward the cell centre, not a true Gaussian (it has compact support, which
                    // is exactly what keeps the +-jitter/2 bound). FLOAT-averaged, not uint-summed, to avoid 32-bit overflow.
                    uint3 g1 = sdfPcg3d(key ^ SDF_HASH_STREAM_A);
                    uint3 g2 = sdfPcg3d(key ^ SDF_HASH_STREAM_B);
                    r0 = (((float3)h0 + (float3)g1 + (float3)g2) * (SDF_INV_2POW32 / 3.0));
                } else {
                    // SDF_NOISE_WHITE (0, the default): the plain independent PCG3D uniform.
                    r0 = ((float3)h0 * SDF_INV_2POW32);
                }

                localPosition -= (data0.xyz * cell);               // the SDF_OP_REPEAT fold
                localPosition -= ((r0 - 0.5) * data0.w);           // per-cell position jitter (data0.w peak-to-peak)

                // Tumble (an isometry): guarded by amplitude so a zeroed op stays an EXACT identity (no rotate, no
                // divergent codegen). The hashed axis is uniform on the sphere; the angle is |r1.z| * tumble * pi.
                if (data1.w > 0.0) {
                    uint3 h1 = sdfPcg3d(key ^ SDF_HASH_TUMBLE);
                    float3 r1 = ((float3)h1 * SDF_INV_2POW32);
                    float zz = ((2.0 * r1.x) - 1.0);
                    float rr = sqrt(max(0.0, (1.0 - (zz * zz))));
                    float phi = (SDF_TAU * r1.y);
                    float3 axis = float3((rr * cos(phi)), (rr * sin(phi)), zz);   // uniform on the unit sphere
                    float angle = ((r1.z * data1.w) * SDF_PI);
                    float ha = (0.5 * angle);
                    float sa = sin(ha);
                    float4 q = float4((axis * sa), cos(ha));       // (x,y,z,w) matches SDF_OP_ROTATE's layout
                    localPosition = rotatePointByInverseQuaternion(localPosition, q);
                }

                // Per-cell material variant (same channel WallpaperFold recolors through): a hashed palette row in
                // 0..variants-1, added to a later shape's material by the SDF_OP_SHAPE_BLEND parityMaterialDelta apply.
                if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                    parityMaterialDelta = (int)(h0.z % SDF_INSTRUCTION_MATERIAL(instructionHeader));
                }

                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.CellJitter;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.CellJitter;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Unbounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Lattice;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Angular domain-repeat fold: folds the plane perpendicular to a chosen axis into <c>count</c> equal
    /// sectors, so one authored prototype repeats rotationally around the axis (gears, wheels, columns of a rotunda,
    /// clock ticks, flower petals) from a single instruction — the rotational sibling of the linear <see cref="Repeat"/>
    /// and the lattice <see cref="WallpaperFold"/>. The fold is a rotation into the base sector (and, when the mirror
    /// flag is set, a reflection of each sector across its bisector — the kaleidoscope fold): both branches are
    /// isometries, so distances are preserved — the field stays 1-Lipschitz (factor 1, no step clamp, exactly like
    /// <see cref="Repeat"/>) and no cull bound changes. Instruction lanes: Shape = <see cref="SdfAxis"/> (the
    /// rotation axis), Blend = the mirror flag (0 = plain repeat, 1 = kaleidoscope), Material = the per-sector palette
    /// stride (the sector index 0..count-1 strides the material id of a later shape win; 0 keeps the fold purely
    /// geometric). Data0.x = the sector angle <c>2π/count</c> (host-baked), Data0.y = <c>count/(2π)</c> = 1/angle
    /// (host-baked), Data0.z = count, Data0.w = 1/count (host-baked); Data1 is reserved. The fold uses <c>atan2</c>/
    /// <c>floor</c> (floats), so a point exactly on a sector seam carries the usual ±1 LSB warp noise (geometry only;
    /// the optional per-sector material can flip at a seam exactly as <see cref="WallpaperFold"/>'s can). Like
    /// <see cref="Repeat"/>, keep the prototype clear of the sector walls (the two radial half-planes through the axis)
    /// — content that overspills a wall is clipped by the neighbouring sector.</summary>
    [SdfFieldEntry(15)]
    private sealed class RepeatPolar : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                uint polarAxis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                // The fold plane (u,v) perpendicular to the axis; the axial coordinate is untouched.
                float2 pv;
                if (polarAxis == SDF_AXIS_X) { pv = localPosition.yz; }
                else if (polarAxis == SDF_AXIS_Z) { pv = localPosition.xy; }
                else { pv = localPosition.xz; }   // SDF_AXIS_Y (default): the XZ ground plane

                float sectorAngle = data0.x;
                float a = (atan2(pv.y, pv.x) + (0.5 * sectorAngle));
                float r = length(pv);
                float sector = floor(a * data0.y);                         // data0.y = 1/angle
                a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));  // a in [-angle/2, angle/2)
                if (SDF_INSTRUCTION_BLEND(instructionHeader) != 0u) { a = abs(a); }             // mirror: reflect across the sector bisector
                pv = (float2(cos(a), sin(a)) * r);

                if (polarAxis == SDF_AXIS_X) { localPosition.yz = pv; }
                else if (polarAxis == SDF_AXIS_Z) { localPosition.xy = pv; }
                else { localPosition.xz = pv; }

                // Per-sector material variant (the same channel SDF_OP_WALLPAPER_FOLD / SDF_OP_CELL_JITTER recolor
                // through): wrap the raw sector index into [0, count) and stride the palette.
                if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                    float wrapped = (sector - (data0.z * floor(sector * data0.w)));   // data0.z = count, data0.w = 1/count
                    parityMaterialDelta = ((int)wrapped * (int)SDF_INSTRUCTION_MATERIAL(instructionHeader));
                }

                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.RepeatPolar;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.RepeatPolar;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.RepeatPolar;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Adds a bounded sinusoidal displacement to the field accumulated so far — surface relief (bumps,
    /// corrugation, a rippled skin), the SDF-native answer to height/parallax mapping (the relief is real geometry, so
    /// it shadows and self-occludes correctly). A field op (like <see cref="Onion"/>/<see cref="Dilate"/>), evaluated at
    /// the current folded point: order it after the shapes it should displace. Data0.xyz = per-axis angular frequency,
    /// Data0.w = amplitude; the basis is the separable product <c>amp·sin(fx·x)·sin(fy·y)·sin(fz·z)</c> (deterministic
    /// float trig — ±1 LSB like the twist/bend warps, cross-backend-parity-safe without a hashed noise table). Not
    /// 1-Lipschitz: the added relief's gradient reaches <c>amp·‖freq‖</c>, so the field can overestimate true distance by
    /// up to <c>1 + amp·‖freq‖</c> — <c>AnalyzeLipschitz</c> bakes that as a conservative step clamp (reach-independent,
    /// folded like the log-spherical product). amp = 0 is an exact identity (byte-identical).</summary>
    [SdfFieldEntry(16)]
    private sealed class Displace : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                // Evaluate the same continuous field at every distance. Switching to f-|amplitude| outside
                // a band introduces a jump that corrupts footprint hits and finite-difference curvature.
                float3 df = (data0.xyz * localPosition);
                result.distance += (data0.w * ((sin(df.x) * sin(df.y)) * sin(df.z)));
                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderEditField;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderEditField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Displacement;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.SampleRelief;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Relief;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Displace;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.Data0W;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Warps the sample point by a bounded, cross-coupled sinusoidal field before the shapes evaluate — organic
    /// bulging / wobble / terrain. A point op (like the fold ops). Data0.xyz = per-axis angular frequency, Data0.w =
    /// amplitude; the point moves by <c>amp·(sin(fx·y), sin(fy·z), sin(fz·x))</c> — cross-coupled (each axis driven by the
    /// next axis's coordinate) so the warp is non-separable, and deterministic float trig (±1 LSB). Not an isometry: the
    /// Jacobian is <c>I</c> plus a perturbation of spectral norm ≤ <c>amp·‖freq‖</c>, so the metric stretches by up to
    /// <c>1 + amp·‖freq‖</c> — <c>AnalyzeLipschitz</c> bakes that step clamp (reach-independent) and folds the point's max
    /// travel (<c>amp·√3</c>) into a downstream twist/bend's reach. amp = 0 is an exact identity (byte-identical).</summary>
    [SdfFieldEntry(17)]
    private sealed class DomainWarp : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                localPosition += (data0.w * float3(
                    sin(data0.x * localPosition.y),
                    sin(data0.y * localPosition.z),
                    sin(data0.z * localPosition.x)));
                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.DomainWarp;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.DomainWarp;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Displacement;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.DomainWarp;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.DomainWarp;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Reflection fold across an arbitrary plane — the general-normal superset of the axis-aligned
    /// <c>SymmetryX</c>/<c>SymmetryY</c>/<c>SymmetryZ</c> builder methods:
    /// everything on the plane's negative side
    /// is mirrored onto its positive side, so one authored half repeats mirror-imaged (a kaleidoscope leaf, a bilateral
    /// body, the reflect atom of a KIFS fold). Data0.xyz = the unit plane normal (host-normalized), Data0.w = the plane
    /// offset; the fold is <c>p -= 2·min(dot(p, n) + offset, 0)·n</c> — for <c>n = x̂, offset = 0</c> this is
    /// <c>abs(p.x)</c> to the bit, so it is an exact superset of <c>SymmetryX</c>. A reflection is an isometry,
    /// so distances are preserved: the field stays 1-Lipschitz (factor 1, no step clamp) and no cull bound changes. Like
    /// the axis symmetries, keep authored content on the plane's positive side (the kept half); content straddling the
    /// plane is folded onto itself.</summary>
    [SdfFieldEntry(18)]
    private sealed class SymmetryPlane : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.SymmetryPlanePoint;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("p", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Rigid(("p", Formula!));
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Fold;
        public override SdfIntervalAction Interval => SdfIntervalAction.Fold;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.SymmetryPlane;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.SymmetryPlane;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Fold;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Opens a scoped field accumulator — the first half of the <see cref="PushField"/>/<see cref="PopField"/>
    /// pair (<see cref="SdfProgramBuilder.PushField"/>). Saves the running nearest-surface distance onto a bounded stack
    /// and reseeds a fresh accumulator (<c>SDF_FAR_DISTANCE</c>), so every accumulator-reading op emitted until the
    /// matching <see cref="PopField"/> — the intersection family, and the <see cref="Onion"/>/<see cref="Dilate"/>/
    /// <see cref="Displace"/> field ops — acts on this scope's field alone, not on everything emitted before it. That is
    /// the whole cure for the flat accumulator's "a field op shells the entire scene" bug: an <c>Onion</c> inside a scope
    /// shells only the scope's own shapes. A scope touches the field, never the evaluation point (localPosition /
    /// distanceScale / the wallpaper material delta), so <see cref="ResetPoint"/> semantics are unchanged and per-shape
    /// cull bounds after the Push in the same chain stay sound. Depth is capped at
    /// <see cref="SdfProgramBuilder.MaxFieldScopeDepth"/> (a shader-constant + validator rule, not part of the packed
    /// layout). Op-unused (scope-free) programs are byte-identical. Every lane of a push is zero.</summary>
    [SdfFieldEntry(19)]
    private sealed class PushField : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                switch (fieldDepth) {
                    case 0u: parent0 = result; break;
                    case 1u: parent1 = result; break;
                    case 2u: parent2 = result; break;
                    case 3u: parent3 = result; break;
                    default: return sdfIsaErrorHit();
                }
                fieldDepth++;
                result.distance = SDF_FAR_DISTANCE;
                if (trackMaterial) {
                    result.material = 0;
                    result.instanceIndex = -1;
                    result.frameSlot = SDF_TRANSFORM_SLOT_NONE;
                }
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.PushField;
        public override SdfIntervalAction Interval => SdfIntervalAction.PushField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.PushField;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.PushField;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.PushField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Identity;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Zero;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.PushField;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.PushField;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.ScopeBoundary;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.PushField;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.PushField;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => false;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Structure;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Closes the scope opened by the matching <see cref="PushField"/> and composes the scope's accumulated
    /// field back into the saved parent accumulator as a single candidate — reusing the shape blend tail (a pop is just
    /// another candidate), so it costs no second copy of the blend switch. The compose blend + smooth radius are carried
    /// on the pop instruction (Blend lane + Data1.x, <see cref="SdfProgramBuilder.PushField"/>'s arguments): a Union
    /// compose makes the whole scope far-neutral (and so maskable / segment-eligible again — the culling payoff), while an
    /// intersection-family compose composes the scope globally (unmaskable, exactly like an unscoped intersection). The
    /// scope's candidate is already in world units (its shapes were distance-scaled as they blended in) and must not be
    /// re-scaled or take the point material delta (the fusion trap). Its material tie-break matches shape's (strict
    /// compare — the parent keeps its material on a tie). A chamfer compose is the one non-1-Lipschitz case: it enters
    /// <see cref="SdfProgram.StepScale"/> through the same per-composition recurrence a chamfer
    /// <see cref="ShapeBlend"/> does, so repeated pops accumulate.</summary>
    [SdfFieldEntry(20)]
    private sealed class PopField : Entry {
        public override SdfShaderAction Shader => new(
            Code: $$"""
                if (fieldDepth == 0u) { return sdfIsaErrorHit(); }
                fieldDepth--;
                SDF_VM_LOAD_DATA1;
                // The scope's accumulated field IS the candidate — ALREADY in world units (its shapes were
                // distance-scaled as they blended in), so it is NOT re-multiplied by distanceScale, and the point
                // parityMaterialDelta must NOT touch it (the fusion trap). Restore the parent accumulator as the
                // blend LHS, then fall into the SAME material-winner + blendShape tail a SHAPE uses. The compose blend
                // + smooth ride the POP instruction (header.z / data1.x, baked by SdfProgramBuilder.PushField).
                // data1.y is the scope's baked 1/L candidate scale on every pop. The pop's operand is a stairs pop's
                // step count in data1.z, or a morph pop's weight from its lane.
                composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
                float candidateScale = data1.y;
                composeCandidate = result.distance;
                if (candidateScale > 0.0) {
                    composeCandidate *= candidateScale;
                }
                if (trackMaterial) {
                    composeMaterial = result.material;
                    composeInstance = result.instanceIndex;
                    composeSlot = result.frameSlot;
                }
                composeSmooth = data1.x;
                composeOperand = data1.z;
                if (composeBlend == SDF_BLEND_MORPH) {
                    SDF_VM_LOAD_DATA0;
                    composeOperand = {{SdfFieldFormulas.MorphWeight.Dispatch(shader: true)}};
                }
                switch (fieldDepth) {
                    case 0u: result = parent0; break;
                    case 1u: result = parent1; break;
                    case 2u: result = parent2; break;
                    default: result = parent3; break;
                }
                composePending = true;
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.PopField;
        public override SdfIntervalAction Interval => SdfIntervalAction.PopField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.PopField;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.PopField;
        public override SdfCullChainAction CullChain => SdfCullChainAction.PopField;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.PopField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Identity;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.PopField;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.PopField;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.PopField;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.ScopeBoundary;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.PopField;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.PopField;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => false;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Structure;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.Blend;
    }

    /// <summary>Adds bounded hash-lattice fBm value noise to the field accumulated so far — irregular surface relief
    /// (terrain crags, bark, rock) where <see cref="Displace"/>'s periodic sine product would read as corrugation. A
    /// field op (like <see cref="Onion"/>/<see cref="Dilate"/>/<see cref="Displace"/>), evaluated at the current folded
    /// point: order it after the shapes it should displace. Data0 = (frequency [cells per world unit], amplitude
    /// [world units], gain, lacunarity), Data1.x = host-baked <c>1/Σ gainᵏ</c> normalization (the octave sum stays in
    /// <c>[-1, 1]</c> before amplitude), Shape = seed, Blend = octave count. The basis is 3D value noise on the integer
    /// lattice — one integer-only PCG3D hash per corner (cell decisions bit-identical across both DXC targets, the
    /// <see cref="CellJitter"/> discipline), quintic-smoothed trilinear blend (deterministic float mul/add, ±1 LSB) —
    /// summed over the declared octaves. Not 1-Lipschitz: the normalized fBm gradient is bounded by
    /// <c>frequency·(15/4)·√3·(Σ (gain·lacunarity)ᵏ / Σ gainᵏ)</c> (quintic max slope 15/8 on a corner span of 2, axes
    /// combined Euclidean), so the field can overestimate by <c>1 + amplitude·(that bound)</c> —
    /// <c>AnalyzeLipschitz</c> bakes it as a conservative step clamp (reach-independent, folded like
    /// <see cref="Displace"/>'s). The outward surface reach is at most <c>|amplitude|</c> (the normalized sum is
    /// bounded by 1) — the scoped-field margin and cull channels read that. amplitude = 0 is an exact identity.</summary>
    [SdfFieldEntry(21)]
    private sealed class NoiseDisplace : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.ReliefField;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("distance", Formula!)).After("""
            // The relief's mask rows stand directly before it in its segment (the program's door, SdfProgram.
            // ReliefRows.cs); malformed words that count past the segment's start or the row cap are refused here.
            uint reliefRows = SDF_INSTRUCTION_MATERIAL(instructionHeader);
            if ((reliefRows > SDF_MAX_RELIEF_ROWS) || (reliefRows > (index - segmentMeta.z))) { return sdfIsaErrorHit(); }
            float weight = ((reliefRows == 0u) ? 1.0 : sdfMaskProduct((index - reliefRows), reliefRows, localPosition, dataOffset));
            """);
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.WeightedRelief;
        public override SdfIntervalAction Interval => SdfIntervalAction.WeightedRelief;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Relief;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.NoiseDisplace;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.SampleRelief;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Relief;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.NoiseDisplace;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.Data0Y;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.ReliefOwner;
        public override SdfFieldResource Resources => SdfFieldResource.ReliefRows;
    }

    /// <summary>Divides both coordinates perpendicular to Shape (axis 0..2) by an axial profile.
    /// Data0=(amount, bulge, origin, inverseSpan); Data1=(distanceCorrection, startScale, 0, 0).
    /// s(t)=startScale+amount*t+bulge*sin(pi*t), t=clamp((origin-p[axis])*inverseSpan,0,1).
    /// The shader floors s at FlareMinScale; the host bounds the resulting Jacobian. Render-only.</summary>
    [SdfFieldEntry(22)]
    private sealed class AxialProfile : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                uint axis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                if (axis >= 3u) { return sdfIsaErrorHit(); }
                distanceScale *= sdfAxialProfile(localPosition, axis, data0, data1);
                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => new(
            Code: """
                float4 data1 = sdfFieldData(dataOffset, domainIndex, 1u);
                uint axis = SDF_INSTRUCTION_SHAPE(domain);
                if (axis >= 3u) { parent = sdfIsaErrorHit(); return; }
                distanceScale *= sdfAxialProfile(localPosition, axis, data0, data1);
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override bool PartDomain => true;
        public override SdfPointAction Point => SdfPointAction.Warp;
        public override SdfIntervalAction Interval => SdfIntervalAction.Warp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.AxialProfile;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.AxialProfile;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.AxialProfile;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Adds a cubic polynomial of the Blend-selected driver coordinate to the Shape-selected
    /// target coordinate. Data0.xyz=(linear, quadratic, cubic); both selectors are distinct axes in [0,2].
    /// The host bounds its derivative over the composed chain reach. Render-only.</summary>
    [SdfFieldEntry(23)]
    private sealed class Shear : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                uint target = SDF_INSTRUCTION_SHAPE(instructionHeader), driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                if (target >= 3u || driver >= 3u) { return sdfIsaErrorHit(); }
                sdfShear(localPosition, target, driver, data0.xyz);
                break;
                """,
            Operands: SdfShaderOperands.Data0
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => new(
            Code: """
                uint target = SDF_INSTRUCTION_SHAPE(domain), driver = SDF_INSTRUCTION_BLEND(domain);
                if (target >= 3u || driver >= 3u) { parent = sdfIsaErrorHit(); return; }
                sdfShear(localPosition, target, driver, data0.xyz);
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override bool PartDomain => true;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Shear;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Shear;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.Shear;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.NoGlobalBound;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Shear;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Shear;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>An anisotropic Gaussian domain displacement: p -= push * exp(-|(p-center)/radii|²).
    /// Data0 = center.xyz, push.x; Data1 = radii.xyz, push.y; Shape stores push.z as float bits.
    /// Its reach-independent Jacobian bound is 1 + |push| * sqrt(2/e) / min(radii). Render-only.</summary>
    [SdfFieldEntry(24)]
    private sealed class GaussianPush : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                float3 gaussianPush = float3(data0.w, data1.w, asfloat(SDF_INSTRUCTION_SHAPE(instructionHeader)));
                float3 gaussianOffset = ((localPosition - data0.xyz) / data1.xyz);
                float gaussianWeight = exp(-dot(gaussianOffset, gaussianOffset));
                localPosition -= (gaussianPush * gaussianWeight);
                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.RenderWarp;
        public override SdfIntervalAction Interval => SdfIntervalAction.RenderWarp;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.GaussianPush;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.GaussianPush;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.GaussianPush;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.GaussianPush;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.GaussianPush;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.GaussianPush;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>Per-shape lane-driven erosion, ordered immediately before the <see cref="ShapeBlend"/> it targets
    /// (whatever ordinary point ops the shape's own chain still applies in between). Data0 = (lane index 0..3 — see
    /// an integer in [0, 3], from, to, noiseScale), Data1.x = the target shape's HOST-BAKED reach (its bound
    /// radius; see <see cref="SdfProgramBuilder.LaneErode"/>). Reads the CURRENT dynamic slot's
    /// <c>DynamicTransform.Lanes</c> component named by the lane index (zero under no dynamic slot).
    /// <c>t = saturate((lane − from) / (to − from))</c>: a reversed range (from &gt; to) runs the fold the other way
    /// (the shape grows IN as the lane rises). <c>t &gt;= 1</c> skips the target shape's evaluation entirely — a
    /// cheap early-out, no field cost. Otherwise the shape is dilated inward by <c>t · reach</c>, modulated by 3D
    /// value noise so the erosion front is ragged rather than a uniform shrink. RENDER-ONLY, like the other warp
    /// family ops: <c>Puck.SignedDistance.Queries.SdfFieldEvaluator</c> does not interpret this op (it carries no
    /// dynamic-transform table in its signature to read a lane from), so a shape carrying it is unreachable for
    /// deterministic field contact.</summary>
    [SdfFieldEntry(25)]
    private sealed class LaneErode : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                float lane = sdfFieldLane(currentSlot, (uint)data0.x);
                float t = saturate((lane - data0.y) / (data0.z - data0.y));

                if (t >= 1.0) {
                    laneErodeSkipShape = true;
                    laneErodeAmount = 0.0;
                } else {
                    float noiseSample = 0.5 + 0.5 * sdfValueNoise3((localPosition * data0.w), SDF_LANE_ERODE_SEED);
                    float raggedT = saturate(t + ((noiseSample - 0.5) * SDF_LANE_ERODE_RAGGED_AMOUNT * (4.0 * t * (1.0 - t))));

                    laneErodeSkipShape = false;
                    laneErodeAmount = (raggedT * data1.x);
                }

                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.LaneErode;
        public override SdfIntervalAction Interval => SdfIntervalAction.LaneErode;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.Ignore;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.LaneErode;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.LaneErode;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => false;
        public override bool RenderOnly => true;
        public override SdfOpRole Role => SdfOpRole.Point;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.SlotLanes;
    }

    /// <summary>Adds amplitude*(F(localPoint*frequency)-0.5) to the running field.
    /// Data0=(frequency, amplitude, randomness, 0), Shape=seed, Blend=SdfCellMode.
    /// The 27-cell PCG3D neighborhood is exact within the mode's admitted randomness range.</summary>
    [SdfFieldEntry(26)]
    private sealed class CellDisplace : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.CellDisplaceField;
        public override SdfShaderAction Shader => SdfShaderAction.Map(("distance", Formula!));
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.EditField;
        public override SdfIntervalAction Interval => SdfIntervalAction.EditField;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.CellDisplace;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.DisruptsScope;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Disable;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Unknown;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.CellDisplace;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.SampleRelief;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Relief;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.CellDisplace;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Disable;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Uncertified;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.HalfData0Y;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.None;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    /// <summary>One data row of a relief mask: data the <see cref="NoiseDisplace"/> after its run reads, and nothing
    /// else. Every other reader of the stream steps over it: it moves no point, writes no field and takes no owner. The
    /// Blend lane names its kind (<see cref="SdfReliefRowKind"/>): a band (Data0 = start, width, inside, outside), a
    /// region's pose (Data0.xyz = origin, Data1 = its unit quaternion) or a region's primitive (Shape = its
    /// <see cref="SdfShapeType"/>, Data0 and Data1 as a <see cref="ShapeBlend"/> packs them, Data1.x zero). A relief's
    /// Material lane counts the rows directly before it, which <see cref="SdfProgram"/> decodes once into typed masks
    /// (<see cref="SdfReliefMask"/>) and refuses unless they are bands each followed by pose and primitive pairs.</summary>
    [SdfFieldEntry(27)]
    private sealed class ReliefRow : Entry {
        public override SdfShaderAction Shader => new(Code: "break;", Operands: SdfShaderOperands.None);
        public override SdfShaderAction RigidShader => SdfShaderAction.Unavailable;
        public override SdfShaderAction PartShader => SdfShaderAction.Unavailable;
        public override bool PartDomain => false;
        public override SdfPointAction Point => SdfPointAction.Identity;
        public override SdfIntervalAction Interval => SdfIntervalAction.Identity;
        public override SdfIntervalFoldAction IntervalFold => SdfIntervalFoldAction.Unsupported;
        public override SdfIntervalFieldEditAction IntervalFieldEdit => SdfIntervalFieldEditAction.Unsupported;
        public override SdfRenderWarpAction RenderWarp => SdfRenderWarpAction.Unsupported;
        public override SdfRenderWarpBoundsAction RenderWarpBounds => SdfRenderWarpBoundsAction.Unbounded;
        public override SdfFieldStructureAction FieldStructure => SdfFieldStructureAction.Other;
        public override SdfDeferredScopeAction DeferredScope => SdfDeferredScopeAction.Ignore;
        public override SdfCullChainAction CullChain => SdfCullChainAction.Allowed;
        public override SdfCullInstancesAction CullInstances => SdfCullInstancesAction.EditsField;
        public override SdfCertificateChainAction CertificateChain => SdfCertificateChainAction.Identity;
        public override SdfAdmissionAction Admission => SdfAdmissionAction.ReliefRow;
        public override SdfReliefPointFactorAction ReliefPointFactor => SdfReliefPointFactorAction.Identity;
        public override SdfStepScaleBinderAction StepScaleBinder => SdfStepScaleBinderAction.Ignore;
        public override SdfCompositionLipschitzAction CompositionLipschitz => SdfCompositionLipschitzAction.Ignore;
        public override SdfChainLipschitzAction ChainLipschitz => SdfChainLipschitzAction.Ignore;
        public override SdfSkipSphereAction SkipSphere => SdfSkipSphereAction.Ignore;
        public override SdfDistanceGainAction DistanceGain => SdfDistanceGainAction.Identity;
        public override SdfRigidPlanAction RigidPlan => SdfRigidPlanAction.Refuse;
        public override SdfScopeClampAction ScopeClamp => SdfScopeClampAction.Ignore;
        public override SdfWarpBoundAction WarpBound => SdfWarpBoundAction.Ignore;
        public override SdfScopeReachAction ScopeReach => SdfScopeReachAction.None;
        public override SdfUnboundedFoldAction UnboundedFold => SdfUnboundedFoldAction.Bounded;
        public override bool ReadsPoint => true;
        public override bool PartPosePrefix => false;
        public override bool Contact => true;
        public override bool RenderOnly => false;
        public override SdfOpRole Role => SdfOpRole.Field;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.ReliefRow;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }
}
