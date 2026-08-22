using Puck.Commands;

namespace Puck.World.Client;

/// <summary>One camera device-enumeration attempt's result: either the ids the platform reported, or a failure
/// message. A binder's periodic scan produces exactly one of these per attempt.</summary>
public abstract record CameraDeviceScanOutcome {
    private CameraDeviceScanOutcome() { }

    /// <summary>The platform enumerated successfully; <paramref name="Ids"/> is every device it currently reports.</summary>
    public sealed record Success(IReadOnlySet<InputDeviceId> Ids) : CameraDeviceScanOutcome;

    /// <summary>The platform enumeration call failed with <paramref name="Message"/>.</summary>
    public sealed record Failure(string Message) : CameraDeviceScanOutcome;
}

/// <summary>The device-table ids a scan outcome adds and retires, and whether the caller should narrate it.</summary>
/// <param name="ToAdd">Ids present in the outcome but not in the previously known set.</param>
/// <param name="ToRetire">Previously known ids the outcome no longer reports; empty for a failed scan — a failure
/// is never read as "every camera unplugged".</param>
/// <param name="Narrate">Whether the caller should report this outcome (a failure's first occurrence in an episode;
/// never for a success or a failure repeating one already reported).</param>
/// <param name="IsFailing">The failing state to carry into the next <see cref="CameraDeviceScanReconciler.Reconcile"/>
/// call as <c>wasFailing</c>.</param>
public readonly record struct CameraDeviceScanDecision(
    IReadOnlyList<InputDeviceId> ToAdd,
    IReadOnlyList<InputDeviceId> ToRetire,
    bool Narrate,
    bool IsFailing
);

/// <summary>Decides one camera device-scan episode's effect on a binder's device table — pure, so the failure
/// once-per-episode narration rule and the add/retire id sets are provable without Media Foundation, a roster, or a
/// live device.</summary>
public static class CameraDeviceScanReconciler {
    /// <summary>Reconciles one scan <paramref name="outcome"/> against the ids previously known.</summary>
    /// <param name="knownIds">The device ids the caller's table held before this scan.</param>
    /// <param name="outcome">This scan's outcome.</param>
    /// <param name="wasFailing">Whether the immediately preceding scan (or the run of scans since the last success)
    /// already failed and narrated.</param>
    public static CameraDeviceScanDecision Reconcile(IReadOnlySet<InputDeviceId> knownIds, CameraDeviceScanOutcome outcome, bool wasFailing) {
        if (outcome is CameraDeviceScanOutcome.Failure) {
            return new CameraDeviceScanDecision(ToAdd: [], ToRetire: [], Narrate: !wasFailing, IsFailing: true);
        }

        var ids = ((CameraDeviceScanOutcome.Success)outcome).Ids;
        var toAdd = new List<InputDeviceId>();

        foreach (var id in ids) {
            if (!knownIds.Contains(item: id)) {
                toAdd.Add(item: id);
            }
        }

        var toRetire = new List<InputDeviceId>();

        foreach (var id in knownIds) {
            if (!ids.Contains(item: id)) {
                toRetire.Add(item: id);
            }
        }

        return new CameraDeviceScanDecision(ToAdd: toAdd, ToRetire: toRetire, Narrate: false, IsFailing: false);
    }
}
