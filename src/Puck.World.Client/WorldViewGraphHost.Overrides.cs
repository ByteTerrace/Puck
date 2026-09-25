using Puck.Commands;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;
using Puck.World.Protocol;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    public sealed partial class Entry {
        private readonly Dictionary<string, JsonElement> m_pending = new(comparer: StringComparer.Ordinal);

        private string? m_appliedOutput;

        internal ShaderPipelinePlan? AppliedPlan { get; set; }
        internal (ShaderPipelinePlan Plan, ShaderPipelineSource Source)? Candidate { get; set; }

        internal string Name { get; init; } = string.Empty;

        internal WorldViewGraphHost? Owner { get; init; }

        /// <summary>Gets the source read the installed graph was compiled from, or <see langword="null"/> before a graph
        /// this runtime compiled has installed.</summary>
        public ShaderPipelineSource? InstalledSource { get; private set; }
        /// <summary>Gets the previewed parameter overrides not yet committed, keyed by pass; each value is the pass's
        /// whole previewed config object. Session state: a move of the row's revision discards them.</summary>
        public IReadOnlyDictionary<string, JsonElement> PendingOverrides => m_pending;
        /// <summary>Gets the <c>WorldDefinitionFingerprint.ComputeGraph</c> of <see cref="Row"/>, the revision a
        /// preview is based on, or <see langword="null"/> before a row has been reconciled.</summary>
        public string? Revision { get; private set; }
        /// <summary>Gets the accepted row this instance last reconciled, or <see langword="null"/> before one.</summary>
        public WorldViewGraph? Row { get; private set; }
        /// <summary>Gets the image version a live <c>pipeline.output</c> selected, or <see langword="null"/> when the
        /// instance shows its row's output.</summary>
        public string? SelectedOutput { get; private set; }

        internal void Adopt(WorldViewGraph row) {
            if (ReferenceEquals(
                objA: Row,
                objB: row
            )) {
                return;
            }

            var revision = WorldDefinitionFingerprint.ComputeGraph(graph: row);

            Row = row;
            if (string.Equals(
                a: revision,
                b: Revision,
                comparisonType: StringComparison.Ordinal
            )) {
                return;
            }

            // A preview is based on one revision of the row; when it moves (a commit landed, another author edited
            // the row, a document was loaded) the preview is discarded and the committed values show.
            Revision = revision;
            m_pending.Clear();
            SelectedOutput = null;
            ClockScale = row.TimeScale;
            ApplyOverrides();
        }

        /// <summary>Adopts a graph that installed since this entry last looked: records the source read it was compiled
        /// from and binds its passes to the effective values. The host's pump calls it before every frame, and every
        /// preview and commit calls it first, so neither acts on a graph whose values were never applied.</summary>
        public void Synchronize() {
            if (
                !Node.IsReady ||
                (Node.Plan is not { } plan) ||
                ReferenceEquals(
                objA: plan,
                objB: AppliedPlan
            )
            ) {
                return;
            }

            AppliedPlan = plan;
            InstalledSource = (((Candidate is { } candidate) && ReferenceEquals(
                objA: candidate.Plan,
                objB: plan
            ))
                ? candidate.Source
                : null
            );
            ApplyOverrides();
        }

        // Binds every configurable pass to its effective values — the preview, else the committed override, else the
        // source's defaults — and shows the effective output. A value the installed graph refuses is reported and the
        // pass keeps what it had.
        private void ApplyOverrides() {
            if (
                !Node.IsReady ||
                (Node.Plan is not { } plan)
            ) {
                return;
            }

            foreach (var pass in plan.Passes) {
                if (pass.Parameters.Schema is not { Count: > 0 }) {
                    continue;
                }
                if (!Node.TrySetConfig(
                    config: EffectiveConfig(pass: pass.Name),
                    passName: pass.Name,
                    reason: out var reason
                )) {
                    Owner?.Report?.Invoke(
                        Name,
                        $"override refused: pass '{pass.Name}': {reason}"
                    );
                }
            }

            // The node keeps its selection across an install, so an output is selected only when the effective one
            // differs from the last this entry selected; the source's first output is never selected needlessly.
            var desired = (SelectedOutput ?? Row?.Output);

            if (
                (desired is null) &&
                (m_appliedOutput is null)
            ) {
                return;
            }

            var output = (desired ?? plan.Outputs[0]);

            if (string.Equals(
                a: output,
                b: m_appliedOutput,
                comparisonType: StringComparison.Ordinal
            )) {
                return;
            }

            try {
                Node.SelectOutput(name: output);
                m_appliedOutput = desired;
            } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                Owner?.Report?.Invoke(
                    Name,
                    $"output refused: {exception.Message}"
                );
            }
        }
        private JsonElement? EffectiveConfig(string pass) {
            if (m_pending.TryGetValue(
                key: pass,
                value: out var pending
            )) {
                return pending;
            }
            if (
                (Row?.Overrides is { } overrides) &&
                overrides.TryGetValue(
                key: pass,
                value: out var committed
            )
            ) {
                return committed;
            }

            return null;
        }

        /// <summary>Previews overrides for one pass: the fields of <paramref name="change"/> replace the pass's
        /// effective values field by field, a <see langword="null"/> field returns to the source's default, and the
        /// result binds through the installed graph's config schema before it shows.</summary>
        /// <param name="pass">The pass name.</param>
        /// <param name="change">The config object carrying the changed fields.</param>
        /// <param name="reason">Why the preview was refused.</param>
        /// <returns><see langword="true"/> when the preview binds and shows.</returns>
        public bool TrySetOverride(string pass, JsonElement change, out string reason) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: pass);
            Synchronize();

            if (!TryMergeOverride(
                change: change,
                current: EffectiveConfig(pass: pass),
                merged: out var merged,
                reason: out reason
            )) {
                return false;
            }
            if (!Node.TrySetConfig(
                config: merged,
                passName: pass,
                reason: out reason
            )) {
                return false;
            }

            m_pending[pass] = merged;

            return true;
        }
        /// <summary>Previews an image version as the instance's output.</summary>
        /// <param name="output">The image version to show.</param>
        /// <param name="reason">Why the selection was refused.</param>
        /// <returns><see langword="true"/> when the output shows.</returns>
        public bool TrySelectOutput(string output, out string reason) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: output);
            Synchronize();

            try {
                Node.SelectOutput(name: output);
            } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                reason = exception.Message;

                return false;
            }

            SelectedOutput = output;
            m_appliedOutput = output;
            reason = string.Empty;

            return true;
        }
        /// <summary>Builds the commit of this instance's preview: its committed overrides with the previewed passes
        /// replacing theirs, its live time scale and output, the revision the preview is based on, and the source
        /// identity of the installed graph. Nothing is taken from any other instance.</summary>
        /// <param name="principal">The acting identity.</param>
        /// <param name="commit">The commit, when this returns <see langword="true"/>.</param>
        /// <param name="reason">Why nothing can be committed.</param>
        /// <returns><see langword="true"/> when a graph this runtime compiled is installed.</returns>
        public bool TryPrepareCommit(Principal principal, [NotNullWhen(returnValue: true)] out WorldMutation.CommitViewGraph? commit, out string reason) {
            commit = null;
            Synchronize();

            if (
                (Row is not { } row) ||
                (Revision is not { } revision)
            ) {
                reason = "no accepted row has been reconciled";

                return false;
            }
            if (
                !Node.IsReady ||
                (InstalledSource is not { } installed) ||
                !ReferenceEquals(
                objA: Node.Plan,
                objB: AppliedPlan
            )
            ) {
                reason = "no compiled graph is installed to commit from";

                return false;
            }

            commit = BuildCommit(
                installed: installed,
                output: (SelectedOutput ?? row.Output),
                pending: m_pending,
                principal: principal,
                revision: revision,
                row: row,
                timeScale: ClockScale
            );
            reason = string.Empty;

            return true;
        }
    }

    /// <summary>Builds a commit of a preview against one row: the row's committed overrides with each previewed pass
    /// replacing its own (an empty previewed pass removes it), the given time scale and output, the row's revision, and
    /// the installed source's identities.</summary>
    /// <param name="principal">The acting identity.</param>
    /// <param name="row">The row the preview is based on.</param>
    /// <param name="revision">The row's <c>WorldDefinitionFingerprint.ComputeGraph</c>.</param>
    /// <param name="pending">The previewed passes.</param>
    /// <param name="installed">The source read the installed graph was compiled from.</param>
    /// <param name="timeScale">The live clock rate.</param>
    /// <param name="output">The shown image version, or <see langword="null"/> for the source's first output.</param>
    /// <returns>The commit.</returns>
    public static WorldMutation.CommitViewGraph BuildCommit(Principal principal, WorldViewGraph row, string revision, IReadOnlyDictionary<string, JsonElement> pending, ShaderPipelineSource installed, float timeScale, string? output) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: pending);
        ArgumentNullException.ThrowIfNull(argument: installed);

        var overrides = new SortedDictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var (pass, config) in (row.Overrides ?? new Dictionary<string, JsonElement>())) {
            overrides[pass] = config;
        }
        foreach (var (pass, config) in pending) {
            if (config.EnumerateObject().Any()) {
                overrides[pass] = config;
            } else {
                _ = overrides.Remove(key: pass);
            }
        }

        return new WorldMutation.CommitViewGraph(
            ConfigIdentity: installed.ConfigIdentity,
            Name: row.Name,
            Output: output,
            Overrides: ((overrides.Count == 0)
                ? null
                : new Dictionary<string, JsonElement>(collection: overrides, comparer: StringComparer.Ordinal)),
            Principal: principal,
            Revision: revision,
            SourceIdentity: installed.SourceIdentity,
            TimeScale: timeScale
        );
    }
    /// <summary>Merges a change into a pass's current override object field by field: a field of
    /// <paramref name="change"/> replaces the current one, and a <see langword="null"/> field removes it so the source's
    /// default shows.</summary>
    /// <param name="current">The pass's current override object, or <see langword="null"/> for none.</param>
    /// <param name="change">The change, a JSON object.</param>
    /// <param name="merged">The merged object, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the change is not an object.</param>
    /// <returns><see langword="true"/> when <paramref name="change"/> is an object.</returns>
    public static bool TryMergeOverride(JsonElement? current, JsonElement change, out JsonElement merged, out string reason) {
        merged = default;

        if (change.ValueKind != JsonValueKind.Object) {
            reason = "an override change must be a JSON object of config fields.";

            return false;
        }

        var fields = new SortedDictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        if (current is { ValueKind: JsonValueKind.Object } existing) {
            foreach (var field in existing.EnumerateObject()) {
                fields[field.Name] = JsonNode.Parse(json: field.Value.GetRawText());
            }
        }
        foreach (var field in change.EnumerateObject()) {
            if (field.Value.ValueKind == JsonValueKind.Null) {
                _ = fields.Remove(key: field.Name);
            } else {
                fields[field.Name] = JsonNode.Parse(json: field.Value.GetRawText());
            }
        }

        var node = new JsonObject();

        foreach (var (name, value) in fields) {
            node[name] = value;
        }

        using var document = JsonDocument.Parse(json: node.ToJsonString());

        merged = document.RootElement.Clone();
        reason = string.Empty;

        return true;
    }
}
