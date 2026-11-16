using System.Diagnostics;
using System.Globalization;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Deep-validates and resolves one declared parameter binding row into its reusable template: its (probe,
    // channel) reference, and its target — a views.post row (already proven at document load) whose package declares
    // the named field as a scalar float, since WorldPostPasses.TrySetConfig only accepts one; or another declared probe whose kind declares the named field as a scalar float. The target probe row is
    // resolved here (m_rowIndexById is complete by the time this runs); the target instance is resolved fresh every
    // ServiceParameters pass — if the target row is seat-relative, against this binding's own instance seat; if
    // single, its one instance — so a target that has not instanced yet (or has retired) is never baked in.
    private ParameterBindingTemplate BuildParameterTemplate(WorldProbeBinding.Parameter parameter, string path, ProbeRowInfo rowInfo) {
        var channel = ResolveChannel(
            channel: parameter.Channel,
            manifest: rowInfo.Manifest,
            path: path
        );

        if (parameter.Target is WorldProbeParameterTarget.Probe target) {
            if (!m_rowIndexById.TryGetValue(
                key: target.Id,
                value: out var targetIndex
            )) {
                throw new InvalidOperationException(message: $"{path}.target.id '{target.Id}' names no declared probe.");
            }

            var targetRow = m_rows[targetIndex];
            var targetManifest = targetRow.Manifest;

            if (
                !targetManifest.TryGetConstantOffset(
                field: target.Field,
                offset: out var offset,
                type: out var type
            ) ||
                (type != ShaderValueType.Float) ||
                (targetManifest.Config is not { } targetConfig) ||
                !targetConfig.TryGetValue(
                key: target.Field,
                value: out var targetField
            )
            ) {
                throw new InvalidOperationException(message: $"{path}.target.field '{target.Field}' names no float config field of probe kind '{targetManifest.Name}'.");
            }
            RequireRangeWithin(
                field: targetField,
                name: $"{targetManifest.Name}.{target.Field}",
                parameter: parameter,
                path: path
            );

            return new ParameterBindingTemplate {
                Channel = channel,
                ConstantOffset = offset,
                MaxAgeTicks = MaxAgeTicks(seconds: parameter.MaxAgeSeconds),
                Row = parameter,
                TargetRowInfo = targetRow,
            };
        }

        if (parameter.Target is not WorldProbeParameterTarget.Post post) {
            throw new InvalidOperationException(message: $"{path}.target is required.");
        }
        if (
            (m_post.FirstOrDefault(predicate: row => string.Equals(
                a: row.Name,
                b: post.Pass,
                comparisonType: StringComparison.Ordinal
            )) is not { } row) ||
            !RenderGraphPackageCatalog.Engine.TryGet(
                id: row.Package,
                package: out var package
            )
        ) {
            throw new InvalidOperationException(message: $"{path}.target.pass '{post.Pass}' names no views.post row running a known package.");
        }
        if (
            (package.Config is not { } config) ||
            !config.TryGetValue(
            key: post.Field,
            value: out var field
        ) ||
            (field.Type != ShaderValueType.Float)
        ) {
            throw new InvalidOperationException(message: $"{path}.target.field '{post.Field}' names no float config field of post pass '{post.Pass}' ('{package.Id}').");
        }
        RequireRangeWithin(
            field: field,
            name: $"{post.Pass}.{post.Field}",
            parameter: parameter,
            path: path
        );

        return new ParameterBindingTemplate {
            Channel = channel,
            MaxAgeTicks = MaxAgeTicks(seconds: parameter.MaxAgeSeconds),
            PostField = post.Field,
            PostPass = post.Pass,
            Row = parameter,
        };
    }
    // Refuses a binding whose authored output range leaves the declared range of the config field it writes.
    private static void RequireRangeWithin(ShaderConfigField field, string name, WorldProbeBinding.Parameter parameter, string path) {
        if (
            !ShaderConfigBinding.InRange(
            field: field,
            value: parameter.Range.X
        ) ||
            !ShaderConfigBinding.InRange(
            field: field,
            value: parameter.Range.Y
        )
        ) {
            throw new InvalidOperationException(message: $"{path}.range [{parameter.Range.X}, {parameter.Range.Y}] leaves the declared range of '{name}' [{field.Min}, {field.Max}].");
        }
    }
    // Writes every declared parameter binding's conditioned value into its target — a composed post pass, or
    // another probe's instance (resolved fresh here, at this binding's own instance seat, and patched in place —
    // handed to its running kernel, which adopts them on its next cycle) — skipping a stale reading (older than the
    // binding's own maxAgeSeconds — the write simply stops, never forcing the target back to some other value), a
    // target that is not currently live (a seat-relative target with no instance at this seat), and an unchanged
    // write. A boot shape that never composed presentation (no pass composed under the row's name) leaves
    // every binding's Writes at zero — a harmless, honestly-reported no-op, never a fault.
    private void ServiceParameters() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var instance in m_liveInstances) {
            foreach (var parameter in instance.ParameterBindings) {
                var template = parameter.Template;

                if (!TryReadUnit(
                    channel: template.Channel,
                    instance: instance,
                    maxAgeTicks: template.MaxAgeTicks,
                    nowTimestamp: nowTimestamp,
                    unitInterval: out var unitInterval
                )) {
                    continue;
                }

                var range = template.Row.Range;
                var value = ((float)(range.X + (unitInterval * (range.Y - range.X))));

                if (template.TargetRowInfo is { } targetRow) {
                    if (ResolveInstance(
                        target: targetRow,
                        contextSeat: instance.Seat
                    ) is not { } targetInstance) {
                        continue;
                    }

                    if (
                        ReferenceEquals(
                        objA: parameter.LastTarget,
                        objB: targetInstance
                    ) &&
                        (value == parameter.LastValue)
                    ) {
                        continue;
                    }

                    WriteConstant(
                        offset: template.ConstantOffset,
                        target: targetInstance,
                        value: value
                    );
                    parameter.LastTarget = targetInstance;
                } else if (
                    (value == parameter.LastValue) ||
                    !m_passes.TrySetConfig(
                    field: template.PostField!,
                    pass: template.PostPass!,
                    value: value
                )
                ) {
                    continue;
                }

                parameter.LastValue = value;
                parameter.Writes++;
            }
        }
    }

    /// <summary>Patches one float config field of a declared probe's kind live — the same write
    /// <see cref="ServiceParameters"/> performs for a <c>probe</c>-target parameter binding, driven instead by the
    /// <c>probe.set</c> verb. Bound only by the field's own declared range, never a binding's authored one; a
    /// <c>parameter</c> binding targeting the same field overwrites this write on its own next changed reading. With
    /// no <c>$seat</c> suffix, a seat-relative probe's every live instance is written.</summary>
    /// <param name="probeRef">The declared probe id, optionally suffixed <c>$&lt;seat&gt;</c> to name one instance
    /// of a seat-relative row.</param>
    /// <param name="field">The probe kind's float config field name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="reason">Why the write was refused, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field was written to at least one live instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeRef"/> or <paramref name="field"/> is
    /// <see langword="null"/>.</exception>
    public bool TrySetField(string probeRef, string field, float value, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: probeRef);
        ArgumentNullException.ThrowIfNull(argument: field);

        ParseInstanceRef(
            baseId: out var baseId,
            probeRef: probeRef,
            seat: out var seat
        );

        if (!m_rowIndexById.TryGetValue(
            key: baseId,
            value: out var rowIndex
        )) {
            reason = $"no probe '{baseId}'";

            return false;
        }

        var rowInfo = m_rows[rowIndex];
        var manifest = rowInfo.Manifest;

        if (
            !manifest.TryGetConstantOffset(
            field: field,
            offset: out var offset,
            type: out var type
        ) ||
            (type != ShaderValueType.Float) ||
            (manifest.Config is not { } config) ||
            !config.TryGetValue(
            key: field,
            value: out var configField
        )
        ) {
            reason = $"'{field}' names no float config field of probe kind '{manifest.Name}'";

            return false;
        }
        if (!ShaderConfigBinding.InRange(
            field: configField,
            value: value
        )) {
            reason = $"{value.ToString(
                format: "0.0000",
                provider: CultureInfo.InvariantCulture
            )} is outside the declared range of '{manifest.Name}.{field}' [{configField.Min}, {configField.Max}]";

            return false;
        }

        if (seat is { } explicitSeat) {
            if (ResolveInstance(
                contextSeat: explicitSeat,
                target: rowInfo
            ) is not { } instance) {
                reason = $"no live instance '{InstanceKey(id: baseId, seat: explicitSeat)}'{DescribeKnownInstances(rowInfo: rowInfo)}";

                return false;
            }

            WriteConstant(
                offset: offset,
                target: instance,
                value: value
            );
            reason = null;

            return true;
        }

        if (!rowInfo.IsSeatRelative) {
            if (rowInfo.SingleInstance is not { } single) {
                reason = $"probe '{baseId}' has no live instance";

                return false;
            }

            WriteConstant(
                offset: offset,
                target: single,
                value: value
            );
            reason = null;

            return true;
        }

        var wroteAny = false;

        foreach (var instance in rowInfo.InstancesBySeat!.Values) {
            WriteConstant(
                offset: offset,
                target: instance,
                value: value
            );
            wroteAny = true;
        }

        if (!wroteAny) {
            reason = $"probe '{baseId}' has no live instances";

            return false;
        }

        reason = null;

        return true;
    }
}
