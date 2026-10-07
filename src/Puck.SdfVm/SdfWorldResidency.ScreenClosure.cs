using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private SdfFrame? m_screenClosureFrame;

    internal void FreezeScreenClosure() {
        if (m_frame is not { } frame) { return; }
        var lights = new SdfLights();

        lights.CopyFrom(source: frame.Lights);
        var sky = new SdfSky();

        sky.CopyFrom(source: frame.Sky);
        m_screenClosureFrame = frame with {
            Lights = lights,
            Sky = sky,
            DynamicTransforms = Array.AsReadOnly(array: frame.DynamicTransforms.ToArray()),
            Views = Array.AsReadOnly(array: frame.Views.ToArray()),
            MovedTransforms = null,
            ProgramChanged = false,
        };
    }
    internal void ReleaseScreenClosure() => m_screenClosureFrame = null;
    internal void ResetScreenClosureLighting() {
        m_tables?.Indirect?.ResetLightingForCapture();
        Array.Clear(array: m_renderedSignatures);
    }
}
