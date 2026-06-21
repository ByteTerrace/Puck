namespace Puck.Abstractions;

public interface INativeWindow : IDisposable {
    NativeDisplayKind DisplayKind { get; }
    bool HasPainted { get; }
    uint Height { get; }
    bool IsOpen { get; }
    bool IsVisible { get; }
    ulong ResizeCount { get; }
    string Title { get; }
    uint Width { get; }

    void Close();
    void PollEvents();
    void Show();
}
