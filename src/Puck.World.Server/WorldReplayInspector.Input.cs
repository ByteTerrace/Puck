using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplayInspector {
    private readonly record struct InputCursor(uint Revision, ulong Epoch, ulong Event);

    // This is an offline observer of the ordinary re-drive, not another replay evaluator. Names come from
    // that tick's captured definition; cancelled actions and transient edges never depend on a final image.
    private static string CaptureInputs(WorldServer server, Dictionary<Principal, InputCursor> cursors) {
        var text = new StringBuilder();
        var catalog = server.Population.InputCatalog;
        for (var ownerIndex = 0; ownerIndex < server.Input.OwnerCount; ownerIndex++) {
            var principal = server.Input.OwnerPrincipal(ownerIndex);
            if (!server.Input.TryGet(principal, out var input)) { continue; }
            var owner = input!;
            cursors.TryGetValue(principal, out var cursor);
            var changed = cursor.Revision != owner.Revision || cursor.Epoch != owner.Epoch;
            if (changed) {
                Separate();
                text.Append("input ").Append(principal.Describe()).Append(" mode='").Append(owner.Mapping.Name)
                    .Append("' revision=").Append(owner.Revision).Append(" epoch=").Append(owner.Epoch);
                if (server.Input.ControllerOrigin(principal) is { } controller) {
                    text.Append(" actor='").Append(controller.Actor.Describe()).Append("' source='").Append(controller.Source).Append('\'');
                }
                for (var index = 0; index < owner.SwitchCount; index++) {
                    var boundary = owner.SwitchRecord(index);
                    if (cursor.Revision == owner.Revision && boundary.Epoch <= cursor.Epoch) { continue; }
                    text.Append(" switch(tick=").Append(boundary.Tick).Append(" epoch=").Append(boundary.Epoch)
                        .Append(" from='").Append(catalog.Mappings[boundary.From].Name).Append("' to='")
                        .Append(catalog.Mappings[boundary.To].Name).Append("' reset=").Append(boundary.Reset ? "true" : "false")
                        .Append(" consumed=[");
                    var separator = false;
                    foreach (var activation in boundary.ConsumedActivations.Span) {
                        if (separator) { text.Append(','); }
                        text.Append(activation);
                        separator = true;
                    }
                    text.Append("])");
                    foreach (var decision in boundary.Decisions.Span) {
                        text.Append(' ').Append(catalog.Channels[decision.Channel].Name).Append('=')
                            .Append(decision.Reason == WorldInputContinuationReason.Kept ? "kept" : "cancelled")
                            .Append('(').Append(decision.Reason).Append(" hold=").Append(decision.HoldPress)
                            .Append(" since=").Append(ExactTime(decision.HeldSince)).Append(')');
                    }
                }
                foreach (var channel in owner.Mapping.Live) {
                    if (!owner.TryHeld(channel, out var held)) { continue; }
                    text.Append(" held.").Append(catalog.Channels[channel].Name).Append("(press=").Append(held.Press)
                        .Append(" since=").Append(ExactTime(held.Origin.Time(WorldInputJudgement.Capture)))
                        .Append(" consumed=").Append(held.Consumed ? "true" : "false").Append(')');
                }
            }
            foreach (var channel in owner.Mapping.Live) {
                var address = owner.Mapping.Address(channel);
                if (catalog.Channels[channel].Shape != ChannelShape.Relative || owner.Image.Channels.Relative(address).Value == 0) { continue; }
                Separate();
                text.Append(principal.Describe()).Append(' ').Append(catalog.Channels[channel].Name)
                    .Append(" displacement=").Append(owner.Mapping.Channels.DescribeValue(owner.Image, address));
            }
            // Profile recompilation preserves event identities. A fresh attachment can restart the stream
            // at a lower epoch; retain no cursor from that previous attachment.
            var lastEvent = cursor.Revision != owner.Revision && owner.Epoch <= cursor.Epoch ? 0 : cursor.Event;
            for (var index = 0; index < owner.Count; index++) {
                var record = owner.Record(index);
                if (record.Id <= lastEvent) { continue; }
                Separate();
                text.Append(principal.Describe()).Append(' ').Append(catalog.Channels[record.Event.Channel].Name)
                    .Append(' ').Append(record.Event.Kind).Append("#").Append(record.Id).Append(" epoch=").Append(record.Epoch)
                    .Append(" capture=").Append(ExactTime(record.Time(WorldInputJudgement.Capture)))
                    .Append(" admittedTick=").Append(record.Admission.Tick).Append(" activation=").Append(record.Event.Activation);
                if (record.HoldPress != 0) { text.Append(" hold=").Append(record.HoldPress); }
                if (record.Event.Kind == WorldInputEventKind.Text) {
                    text.Append(" scalar=U+").Append(record.Event.RawValue.ToString("X4", CultureInfo.InvariantCulture));
                } else { text.Append(" raw=").Append(record.Event.RawValue); }
                lastEvent = record.Id;
            }
            cursors[principal] = new(owner.Revision, owner.Epoch, lastEvent);
        }
        return text.ToString();

        void Separate() { if (text.Length != 0) { text.Append("; "); } }
    }

    private static string ExactTime(FixedQ4816 value) =>
        ((decimal)value.Value / FixedQ4816.One.Value).ToString(CultureInfo.InvariantCulture) + "s";
}
