using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// What every recorder of an sdf.world pass shares: the view it records, held from its creation (the package's hold and
// the residency's retain, which its build took) until LeaveView gives them back, and followed on the frame thread to the
// view its instance resolves (SdfWorldPasses.PrepareRecord), so its recording, which may run on another thread, reads only
// its instance and the view's residency, its recording domain.
internal abstract class SdfViewRecorder(SdfWorldPasses owner, string instance, SdfWorldView view) {
    private protected SdfWorldView m_view = view;
    // Whether the instance's installed fragment reconstructs over time (SdfWorldPackage.IsTemporal), which every pass of
    // one installed graph agrees on.
    private protected readonly bool m_temporal = SdfWorldPackage.IsTemporal(fragment: owner.FragmentOf(instance: instance)!);

    public object? RecordingDomain => m_view.Residency;

    public void PrepareRecord(in FrameContext context) {
        if (owner.PrepareRecord(instance: instance, view: ref m_view, context: in context)) {
            FollowedResidency();
        }
    }

    // The fragment's names of a pass's ports: each port's resource name with the prefix the graph splices before the
    // pass's part (the pass's name less its part) taken off.
    private protected static string[] LocalNames(IReadOnlyList<ShaderPipelineResource> ports, string pass, string part) {
        var prefix = pass[..^part.Length];

        return [.. ports.Select(selector: port => (port.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix) ? port.Name[prefix.Length..] : port.Name))];
    }

    // Rebinds what the recorder declared for the residency its view left.
    private protected virtual void FollowedResidency() { }
    private protected void LeaveView() => owner.Leave(view: m_view);
}
