namespace Puck.Scene;

/// <summary>
/// The accumulating sink the per-section validators write to. Each message is prefixed with a JSON-ish path
/// (e.g. <c>scene.objects[2].radius</c>) so an artist can find the offending field without a stack trace. The thick
/// gate collects ALL errors in one pass rather than throwing on the first, so a document can be fixed in one round.
/// </summary>
internal sealed class ValidationErrors {
    private readonly List<string> m_messages = [];

    public IReadOnlyList<string> Messages => m_messages;
    public bool HasErrors => (m_messages.Count > 0);

    public void Add(string path, string message) {
        m_messages.Add(item: $"{path}: {message}");
    }
    public void RequireVector(string path, IReadOnlyList<float>? components, int length) {
        if (!JsonVector.IsValid(components: components, length: length)) {
            Add(path: path, message: $"expected {length} finite numeric components");
        }
    }
    public void RequireRange(string path, string name, float value, FloatRange range) {
        if (!range.Contains(value: value)) {
            Add(path: path, message: $"{name} {value} is outside the allowed range [{range.Minimum}, {range.Maximum}]");
        }
    }
    public void RequireFinite(string path, string name, float value) {
        if (!float.IsFinite(f: value)) {
            Add(path: path, message: $"{name} must be a finite number (was {value})");
        }
    }
}
