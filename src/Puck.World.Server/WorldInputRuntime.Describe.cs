using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    /// <summary>Names a body's current tuple and every declared action, including inactive actions and accepted
    /// source continuation. This is a read only; discovery never admits an input owner.</summary>
    /// <param name="body">The body index in this authority.</param>
    public string DescribeBody(int body) {
        var target = m_host.Body(index: body);
        if (target is null) { return $"body:{body} inactive"; }
        var mapping = Catalog.Mappings[target.InputMappingId];
        var text = new StringBuilder().Append(value: "mode='").Append(value: mapping.Name)
            .Append(value: "' epoch=").Append(value: target.InputEpoch).Append(value: " actions=[");
        for (var logical = 0; (logical < Catalog.Channels.Length); logical++) {
            if (logical != 0) { text.Append(value: ", "); }
            var channel = Catalog.Channels[logical];
            var address = mapping.Address(logicalId: logical);
            text.Append(value: channel.Name).Append(value: ':').Append(value: channel.Shape).Append(value: '=');
            if (!mapping.IsLive(logicalId: logical)) { text.Append(value: "inactive(selected mode)"); }
            else if (channel.Shape == ChannelShape.Text) { text.Append(value: "event-only"); }
            else { text.Append(value: mapping.Channels.DescribeValue(intent: target.ChannelReadComposed, ordinal: address)); }
        }
        text.Append(value: ']');
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners)) { return text.ToString(); }
        foreach (var owner in owners) {
            var input = owner.Input;
            text.Append(value: " owner=").Append(value: owner.Principal.Describe()).Append(value: " mode='")
                .Append(value: input.Mapping.Name).Append(value: "' epoch=").Append(value: input.Epoch);
            var boundary = input.LastSwitch;
            if (boundary.Epoch != 0) {
                text.Append(value: " switch(tick=").Append(value: boundary.Tick).Append(value: " from='")
                    .Append(value: Catalog.Mappings[boundary.From].Name).Append(value: "' to='")
                    .Append(value: Catalog.Mappings[boundary.To].Name).Append(value: "')");
                foreach (var decision in boundary.Decisions.Span) {
                    text.Append(value: ' ').Append(value: Catalog.Channels[decision.Channel].Name).Append(value: '=')
                        .Append(value: decision.Reason).Append(value: " hold=").Append(value: decision.HoldPress)
                        .Append(value: " since=").Append(value: decision.HeldSince);
                }
            }
            foreach (var source in input.Sources) {
                if ((source.RawValue == 0) && !source.AwaitingNeutral) { continue; }
                text.Append(value: " source='").Append(value: source.Source.Name).Append(value: "' action=")
                    .Append(value: Catalog.Channels[source.Source.Channel].Name).Append(value: ' ')
                    .Append(value: source.Source.Kind).Append(value: " raw=").Append(value: source.RawValue);
                if (source.AwaitingNeutral) { text.Append(value: " awaiting-neutral"); }
            }
            foreach (var logical in input.Mapping.Live) {
                if (!input.TryHeld(channel: logical, hold: out var hold)) { continue; }
                text.Append(value: " held.").Append(value: Catalog.Channels[logical].Name).Append(value: " press=")
                    .Append(value: hold.Press).Append(value: " since=").Append(value: hold.Origin.Time(policy: WorldInputJudgement.Capture));
            }
        }
        return text.ToString();
    }
}
