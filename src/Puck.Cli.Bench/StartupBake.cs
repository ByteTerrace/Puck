namespace Puck.Cli.Bench;

/// <summary>A reported residency and its bake endpoints in milliseconds since process launch; a missing endpoint fails
/// the sample. The identity distinguishes residencies with the same display name.</summary>
/// <param name="Id">The residency's diagnostic identity.</param>
/// <param name="Name">Its display name.</param>
/// <param name="StartMilliseconds">Its first observed bake start.</param>
/// <param name="DoneMilliseconds">Its last completion, cleared when another bake starts.</param>
public sealed record StartupBakeResidency(string Id, string Name, double? StartMilliseconds, double? DoneMilliseconds);

/// <summary>Assesses the paused lockstep cold-bake transcript against the World's reported residency census.</summary>
public static class StartupBake {
    /// <summary>Matches start and completion endpoints to every residency in <c>world.residency</c>. The readiness
    /// barrier discovers late demands; its waiting time is outside the interval.</summary>
    /// <param name="row">The process and console verdict.</param>
    /// <param name="result">The complete drained process transcript.</param>
    /// <returns>The sample with residency evidence and an interval only when every reported bake finished.</returns>
    public static StartupSample Assess(StartupSample row, CliProcessResult result) {
        var reported = new Dictionary<string, string>(StringComparer.Ordinal);
        var observed = new Dictionary<string, StartupBakeResidency>(StringComparer.Ordinal);
        var reasons = new List<string>();
        if (row.Error is { } error) { reasons.Add(error); }
        var settled = false;
        foreach (var line in result.OutputLines) {
            var text = line.Line;
            if (line.Stream == CliProcessOutputStream.Stdout) {
                const string Prefix = "[world.residency: ";
                var report = text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : text;
                if (!report.StartsWith("residency=", StringComparison.Ordinal)) { continue; }
                if (Field(report, "residency-id") is not { } id) {
                    reasons.Add($"world.residency omitted a residency identity: {report}");
                    continue;
                }
                reported[id] = Field(report, "residency") ?? id;
                continue;
            }
            if (text.StartsWith("[bakes: settled at tick ", StringComparison.Ordinal)) { settled = true; }
            if (text.StartsWith("[bakes: not settled ", StringComparison.Ordinal)) { reasons.Add(text); }
            if (!text.StartsWith("[sdf.mesh-bake]", StringComparison.Ordinal)) { continue; }
            if (text.Contains("failed:", StringComparison.Ordinal) || text.Contains("refused:", StringComparison.Ordinal)) { reasons.Add(text); }
            var starting = text.StartsWith("[sdf.mesh-bake] start ", StringComparison.Ordinal);
            if (!starting && !text.StartsWith("[sdf.mesh-bake] done ", StringComparison.Ordinal)) { continue; }
            if (Field(text, "residency-id") is not { } identity) {
                reasons.Add($"bake endpoint omitted a residency identity: {text}");
                continue;
            }
            observed.TryGetValue(identity, out var prior);
            observed[identity] = new(identity, Field(text, "residency") ?? identity,
                starting ? prior?.StartMilliseconds ?? line.ElapsedMilliseconds : prior?.StartMilliseconds,
                starting ? null : line.ElapsedMilliseconds);
        }
        if (!settled) { reasons.Add("missing settled bake barrier"); }
        if (reported.Count == 0) { reasons.Add("world.residency reported no residencies"); }
        var residencies = reported.Select(pair => observed.TryGetValue(pair.Key, out var bake)
            ? bake : new StartupBakeResidency(pair.Key, pair.Value, null, null)).ToArray();
        foreach (var residency in residencies) {
            if (residency.StartMilliseconds is null || residency.DoneMilliseconds is null) {
                reasons.Add($"reported residency {residency.Id} ({residency.Name}) has no complete bake");
            }
        }
        foreach (var residency in observed.Values) {
            if (!reported.ContainsKey(residency.Id) && residency.DoneMilliseconds is null) {
                reasons.Add($"residency {residency.Id} ({residency.Name}) started but never finished");
            }
        }
        return row with {
            BakeResidencies = residencies,
            BakeMilliseconds = reasons.Count == 0
                ? observed.Values.Max(static bake => bake.DoneMilliseconds) - observed.Values.Min(static bake => bake.StartMilliseconds)
                : null,
            Error = reasons.Count == 0 ? null : string.Join("; ", reasons),
        };
    }

    private static string? Field(string text, string name) {
        var prefix = name + "=";
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (token.StartsWith(prefix, StringComparison.Ordinal)) { return token[prefix.Length..].TrimEnd(']'); }
        }
        return null;
    }
}
