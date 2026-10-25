using System.Text;
using Puck.Overlays;

namespace Puck.World;

/// <summary>Observes the text elements the HUD actually shows. A visible arrival or changed resolved text
/// speaks once; hiding or removing it stops its utterance. Uses the HUD's existing binding and template grammar.</summary>
public sealed class WorldHudSpeechFeed(IHudBindingResolver bindings, Action<string, WorldVoiceProfile, string> speak, Action<string> stop) {
    private readonly Dictionary<string, (string Text, WorldVoiceProfile Voice)> m_seen = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_visible = new(comparer: StringComparer.Ordinal);

    /// <summary>Begins a HUD frame's visible-element walk.</summary>
    public void BeginFrame() => m_visible.Clear();
    /// <summary>Observes a visible panel, scoped by world (-1) or local seat.</summary>
    public void Observe(WorldHudPanel panel, int seat) {
        foreach (var element in panel.Elements) {
            if (element is not { Kind: WorldHudElementKind.Text, Voice: { } voice }) {
                continue;
            }
            var key = $"{seat}/{panel.Id}/{element.Id}";
            var text = ResolveText(bindings: bindings, element: element, seat: seat);

            m_visible.Add(item: key);
            var current = (text, voice);

            if (m_seen.TryGetValue(key: key, value: out var previous) && (previous == current)) {
                continue;
            }
            m_seen[key] = current;
            if (string.IsNullOrWhiteSpace(value: text)) {
                stop(key);
            } else {
                speak(key, voice, text);
            }
        }
    }
    /// <summary>Stops utterances whose elements no longer show.</summary>
    public void EndFrame() {
        foreach (var key in m_seen.Keys) {
            if (!m_visible.Contains(item: key)) {
                m_hidden.Add(item: key);
            }
        }
        foreach (var key in m_hidden) {
            stop(key);
            m_seen.Remove(key: key);
        }

        m_hidden.Clear();
    }
    // The keys a frame's end found hidden, reused from frame to frame.
    private readonly List<string> m_hidden = [];
    /// <summary>Resolves the same literal, binding or template the text renderer presents.</summary>
    public static string ResolveText(WorldHudElement element, IHudBindingResolver bindings, int seat) {
        if ((element.Template is { Length: > 0 } template) && HudTemplate.TryParse(error: out _, segments: out var segments, template: template)) {
            var text = new StringBuilder();

            foreach (var segment in segments) {
                if (!segment.IsPlaceholder) {
                    text.Append(value: segment.Text);
                } else if (bindings.TryResolve(segment.Text, seat, out _, out var value)) {
                    text.Append(value: value);
                }
            }
            return text.ToString();
        }
        if ((element.Binding is { Length: > 0 } binding) && bindings.TryResolve(binding: binding, fraction: out _, seat: seat, text: out var resolved)) {
            return resolved.ToString();
        }
        return (element.Text ?? string.Empty);
    }
}
