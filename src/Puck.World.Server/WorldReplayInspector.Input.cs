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

        for (var ownerIndex = 0; (ownerIndex < server.Input.OwnerCount); ownerIndex++) {
            var principal = server.Input.OwnerPrincipal(index: ownerIndex);

            if (!server.Input.TryGet(input: out var input, principal: principal)) { continue; }
            var owner = input!;

            cursors.TryGetValue(key: principal, value: out var cursor);
            var changed = ((cursor.Revision != owner.Revision) || (cursor.Epoch != owner.Epoch));

            if (changed) {
                Separate();
                text.Append(value: "input ").Append(value: principal.Describe()).Append(value: " mode='").Append(value: owner.Mapping.Name)
                    .Append(value: "' revision=").Append(value: owner.Revision).Append(value: " epoch=").Append(value: owner.Epoch);
                if (server.Input.ControllerOrigin(principal: principal) is { } controller) {
                    text.Append(value: " actor='").Append(value: controller.Actor.Describe()).Append(value: "' source='").Append(value: controller.Source).Append(value: '\'');
                }
                for (var index = 0; (index < owner.SwitchCount); index++) {
                    var boundary = owner.SwitchRecord(index: index);

                    if ((cursor.Revision == owner.Revision) && (boundary.Epoch <= cursor.Epoch)) { continue; }
                    text.Append(value: " switch(tick=").Append(value: boundary.Tick).Append(value: " epoch=").Append(value: boundary.Epoch)
                        .Append(value: " from='").Append(value: catalog.Mappings[boundary.From].Name).Append(value: "' to='")
                        .Append(value: catalog.Mappings[boundary.To].Name).Append(value: "' reset=").Append(value: (boundary.Reset ? "true" : "false"))
                        .Append(value: " consumed=[");
                    var separator = false;

                    foreach (var activation in boundary.ConsumedActivations.Span) {
                        if (separator) { text.Append(value: ','); }
                        text.Append(value: activation);
                        separator = true;
                    }
                    text.Append(value: "])");
                    foreach (var decision in boundary.Decisions.Span) {
                        text.Append(value: ' ').Append(value: catalog.Channels[decision.Channel].Name).Append(value: '=')
                            .Append(value: ((decision.Reason == WorldInputContinuationReason.Kept) ? "kept" : "cancelled"))
                            .Append(value: '(').Append(value: decision.Reason).Append(value: " hold=").Append(value: decision.HoldPress)
                            .Append(value: " since=").Append(value: ExactTime(value: decision.HeldSince)).Append(value: ')');
                    }
                }
                foreach (var channel in owner.Mapping.Live) {
                    if (!owner.TryHeld(channel: channel, hold: out var held)) { continue; }
                    text.Append(value: " held.").Append(value: catalog.Channels[channel].Name).Append(value: "(press=").Append(value: held.Press)
                        .Append(value: " since=").Append(value: ExactTime(value: held.Origin.Time(policy: WorldInputJudgement.Capture)))
                        .Append(value: " consumed=").Append(value: (held.Consumed ? "true" : "false")).Append(value: ')');
                }
            }
            foreach (var channel in owner.Mapping.Live) {
                var address = owner.Mapping.Address(logicalId: channel);

                if ((catalog.Channels[channel].Shape != ChannelShape.Relative) || (owner.Image.Channels.Relative(ordinal: address).Value == 0)) { continue; }
                Separate();
                text.Append(value: principal.Describe()).Append(value: ' ').Append(value: catalog.Channels[channel].Name)
                    .Append(value: " displacement=").Append(value: owner.Mapping.Channels.DescribeValue(intent: owner.Image, ordinal: address));
            }
            // Profile recompilation preserves event identities. A fresh attachment can restart the stream
            // at a lower epoch; retain no cursor from that previous attachment.
            var lastEvent = (((cursor.Revision != owner.Revision) && (owner.Epoch <= cursor.Epoch)) ? 0 : cursor.Event);

            for (var index = 0; (index < owner.Count); index++) {
                var record = owner.Record(index: index);

                if (record.Id <= lastEvent) { continue; }
                Separate();
                text.Append(value: principal.Describe()).Append(value: ' ').Append(value: catalog.Channels[record.Event.Channel].Name)
                    .Append(value: ' ').Append(value: record.Event.Kind).Append(value: "#").Append(value: record.Id).Append(value: " epoch=").Append(value: record.Epoch)
                    .Append(value: " capture=").Append(value: ExactTime(value: record.Time(policy: WorldInputJudgement.Capture)))
                    .Append(value: " admittedTick=").Append(value: record.Admission.Tick).Append(value: " activation=").Append(value: record.Event.Activation);
                if (record.HoldPress != 0) { text.Append(value: " hold=").Append(value: record.HoldPress); }
                if (record.Event.Kind == WorldInputEventKind.Text) {
                    text.Append(value: " scalar=U+").Append(value: record.Event.RawValue.ToString(format: "X4", provider: CultureInfo.InvariantCulture));
                } else { text.Append(value: " raw=").Append(value: record.Event.RawValue); }
                lastEvent = record.Id;
            }
            cursors[principal] = new(owner.Revision, owner.Epoch, lastEvent);
        }
        return text.ToString();

        void Separate() { if (text.Length != 0) { text.Append(value: "; "); } }
    }
    private static string ExactTime(FixedQ4816 value) =>
        ((((decimal)value.Value) / FixedQ4816.One.Value).ToString(provider: CultureInfo.InvariantCulture) + "s");
}
