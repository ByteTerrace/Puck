namespace Puck.Abstractions;

public interface IClipboardService {
    void SetText(string text);
    bool TryGetText(out string text);
}
