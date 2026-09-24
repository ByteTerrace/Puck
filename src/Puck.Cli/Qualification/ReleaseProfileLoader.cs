using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Puck.Cli.Qualification;

/// <summary>
/// Reads a <c>puck.release.profile.v1</c> document and refuses one qualification could not honor, naming the first
/// member that is wrong. The parse is strict both ways (<see cref="QualificationJsonContext"/>); the validation holds
/// the matrix together: every backend is one a leg can boot, every resolution halves exactly, every workload is named
/// once and boots a <c>.world.json</c> inside the package, and every matrix cell has exactly one threshold row, carrying
/// a pipeline threshold exactly when its workload churns a pipeline.
/// </summary>
internal static class ReleaseProfileLoader {
    /// <summary>The profile <c>puck qualify</c> reads when none is named, repository-relative.</summary>
    public const string DefaultPath = "tests/Puck.Qualification/release.profile.json";

    private const int MaxExtent = 16384;
    private const string WorldSuffix = ".world.json";

    /// <summary>Reads and validates a profile file.</summary>
    /// <param name="path">The profile's path.</param>
    /// <param name="profile">The profile, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the file is not a usable profile, or empty.</param>
    /// <returns><see langword="true"/> when the file is a valid profile.</returns>
    public static bool TryLoad(string path, [NotNullWhen(returnValue: true)] out ReleaseProfile? profile, out string reason) {
        byte[] bytes;

        profile = null;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"unreadable: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        return TryParse(
            bytes: bytes,
            profile: out profile,
            reason: out reason
        );
    }
    /// <summary>Parses and validates a profile document.</summary>
    /// <param name="bytes">The document's UTF-8 bytes.</param>
    /// <param name="profile">The profile, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the document is not a usable profile, or empty.</param>
    /// <returns><see langword="true"/> when the document is a valid profile.</returns>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(returnValue: true)] out ReleaseProfile? profile, out string reason) {
        profile = null;

        ReleaseProfile? parsed;

        try {
            parsed = JsonSerializer.Deserialize(
                jsonTypeInfo: QualificationJsonContext.Default.ReleaseProfile,
                utf8Json: bytes
            );
        } catch (JsonException exception) {
            reason = $"not a {ReleaseProfile.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (parsed is null) {
            reason = $"not a {ReleaseProfile.SchemaVersion} document: the document is null";

            return false;
        }
        if (!TryValidate(
            profile: parsed,
            reason: out reason
        )) {
            return false;
        }

        profile = parsed;

        return true;
    }
    /// <summary>Validates a parsed profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="reason">The first refusal, naming the member, or empty.</param>
    /// <returns><see langword="true"/> when qualification can run the profile as written.</returns>
    public static bool TryValidate(ReleaseProfile profile, out string reason) {
        reason = (FirstRefusal(profile: profile) ?? string.Empty);

        return (reason.Length == 0);
    }

    private static string? FirstRefusal(ReleaseProfile profile) {
        if (!string.Equals(
            a: profile.Schema,
            b: ReleaseProfile.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            return $"schema: '{profile.Schema}' is not '{ReleaseProfile.SchemaVersion}'";
        }
        if (string.IsNullOrWhiteSpace(value: profile.Publish.EntryAssembly) || (Path.GetFileName(path: profile.Publish.EntryAssembly) != profile.Publish.EntryAssembly)) {
            return $"publish.entryAssembly: '{profile.Publish.EntryAssembly}' must be a file name inside the package";
        }

        return (Backends(profile: profile)
            ?? (Functional(profile: profile)
            ?? (Resolutions(profile: profile)
            ?? (Workloads(profile: profile)
            ?? (Thresholds(profile: profile)
            ?? Deferred(profile: profile))))));
    }
    private static string? Backends(ReleaseProfile profile) {
        if (profile.Backends.Count == 0) {
            return "backends: at least one backend is required";
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var backend in profile.Backends) {
            if (!WorldOffscreenLeg.Backends.Contains(value: backend)) {
                return $"backends: '{backend}' is not a backend a leg boots ({string.Join(separator: ", ", values: WorldOffscreenLeg.Backends)})";
            }
            if (!seen.Add(item: backend)) {
                return $"backends: '{backend}' is listed twice";
            }
        }

        var layers = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var backend in profile.DebugLayers) {
            if (!seen.Contains(item: backend)) {
                return $"debugLayers: '{backend}' is not one of the profile's backends";
            }
            if (!layers.Add(item: backend)) {
                return $"debugLayers: '{backend}' is listed twice";
            }
        }

        return null;
    }
    private static string? Functional(ReleaseProfile profile) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var id in profile.Functional) {
            if (string.IsNullOrWhiteSpace(value: id)) {
                return "functional: a canary id is empty";
            }
            if (!seen.Add(item: id)) {
                return $"functional: '{id}' is listed twice";
            }
        }

        return null;
    }
    private static string? Resolutions(ReleaseProfile profile) {
        if (profile.Resolutions.Count == 0) {
            return "resolutions: at least one resolution is required";
        }

        var seen = new HashSet<(int, int)>();

        foreach (var resolution in profile.Resolutions) {
            if (
                (resolution.Width is <= 0 or > MaxExtent) ||
                (resolution.Height is <= 0 or > MaxExtent)
            ) {
                return $"resolutions: {resolution.Label} is outside 1..{MaxExtent} on an axis";
            }
            if (
                ((resolution.Width % 2) != 0) ||
                ((resolution.Height % 2) != 0)
            ) {
                return $"resolutions: {resolution.Label} has an odd axis, so a half-size resize is not exact";
            }
            if (!seen.Add(item: (resolution.Width, resolution.Height))) {
                return $"resolutions: {resolution.Label} is listed twice";
            }
        }

        return null;
    }
    private static string? Workloads(ReleaseProfile profile) {
        if (profile.Workloads.Count == 0) {
            return "workloads: at least one workload is required";
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var workload in profile.Workloads) {
            var at = $"workloads '{workload.Name}'";

            if (
                (workload.Name.Length == 0) ||
                !workload.Name.All(predicate: static character => (char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character) || (character == '-')))
            ) {
                return $"workloads: '{workload.Name}' is not a name of lowercase letters, digits and hyphens";
            }
            if (!seen.Add(item: workload.Name)) {
                return $"workloads: '{workload.Name}' is listed twice";
            }
            if (WorldRefusal(world: workload.World) is { } world) {
                return $"{at}.world: {world}";
            }
            if (workload.WarmupTicks <= 0) {
                return $"{at}.warmupTicks: {workload.WarmupTicks} must be positive";
            }
            if (workload.SoakTicks <= 0) {
                return $"{at}.soakTicks: {workload.SoakTicks} must be positive";
            }
            if (workload.WorldReloads < 0) {
                return $"{at}.worldReloads: {workload.WorldReloads} must not be negative";
            }
            if (workload.TimeoutSeconds <= 0) {
                return $"{at}.timeoutSeconds: {workload.TimeoutSeconds} must be positive";
            }
            if (workload.Pipeline is { } pipeline) {
                if (string.IsNullOrWhiteSpace(value: pipeline.Instance)) {
                    return $"{at}.pipeline.instance: a views.pipelines row name is required";
                }
                if (string.IsNullOrWhiteSpace(value: pipeline.Layout)) {
                    return $"{at}.pipeline.layout: a views.layouts row name is required";
                }
                if (pipeline.SettleFrames <= 0) {
                    return $"{at}.pipeline.settleFrames: {pipeline.SettleFrames} must be positive";
                }
                if (
                    (pipeline.Reloads < 0) ||
                    (pipeline.Resizes < 0) ||
                    (pipeline.Loads < 0)
                ) {
                    return $"{at}.pipeline: reloads, resizes and loads must not be negative";
                }
            }
        }

        return null;
    }
    private static string? WorldRefusal(string world) {
        if (!world.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldSuffix
        )) {
            return $"'{world}' is not a {WorldSuffix} document, the one form a generated overlay can take as its basis";
        }
        if (
            world.Contains(value: '\\') ||
            Path.IsPathRooted(path: world) ||
            world.Split(separator: '/').Any(predicate: static segment => (segment is "" or "." or ".."))
        ) {
            return $"'{world}' must be a package-relative path with forward slashes and no '.', '..' or empty segment";
        }

        return null;
    }
    private static string? Thresholds(ReleaseProfile profile) {
        var workloads = profile.Workloads.ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static workload => workload.Name
        );
        var resolutions = profile.Resolutions.Select(selector: static resolution => (resolution.Width, resolution.Height)).ToHashSet();
        var covered = new HashSet<(string, string, int, int)>();

        foreach (var threshold in profile.Thresholds) {
            var cell = $"{threshold.Workload}/{threshold.Width}x{threshold.Height}/{threshold.Backend}";

            if (!workloads.TryGetValue(
                key: threshold.Workload,
                value: out var workload
            )) {
                return $"thresholds {cell}: no workload is named '{threshold.Workload}'";
            }
            if (!profile.Backends.Contains(value: threshold.Backend)) {
                return $"thresholds {cell}: '{threshold.Backend}' is not one of the profile's backends";
            }
            if (!resolutions.Contains(item: (threshold.Width, threshold.Height))) {
                return $"thresholds {cell}: {threshold.Width}x{threshold.Height} is not one of the profile's resolutions";
            }
            if (!covered.Add(item: (threshold.Workload, threshold.Backend, threshold.Width, threshold.Height))) {
                return $"thresholds {cell}: the cell has two threshold rows";
            }
            if (workload.Pipeline is null) {
                if (threshold.PeakOwnedPipelineBytes is not null) {
                    return $"thresholds {cell}: peakOwnedPipelineBytes must be null, since the workload churns no pipeline";
                }
            } else if (threshold.PeakOwnedPipelineBytes is not > 0L) {
                return $"thresholds {cell}: peakOwnedPipelineBytes must be a positive byte count, since the workload churns a pipeline";
            }
        }

        foreach (var workload in profile.Workloads) {
            foreach (var resolution in profile.Resolutions) {
                foreach (var backend in profile.Backends) {
                    if (!covered.Contains(item: (workload.Name, backend, resolution.Width, resolution.Height))) {
                        return $"thresholds: the cell {workload.Name}/{resolution.Label}/{backend} has no threshold row";
                    }
                }
            }
        }

        return null;
    }
    private static string? Deferred(ReleaseProfile profile) {
        var seen = new HashSet<QualificationDeferredCheck>();

        foreach (var deferral in profile.Deferred) {
            if (!seen.Add(item: deferral.Check)) {
                return $"deferred: {deferral.Check} is listed twice";
            }
            if (string.IsNullOrWhiteSpace(value: deferral.Reason)) {
                return $"deferred: {deferral.Check} gives no reason";
            }
        }

        return null;
    }
}
