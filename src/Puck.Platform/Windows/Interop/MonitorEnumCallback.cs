
namespace Puck.Platform.Windows.Interop;

internal delegate bool MonitorEnumCallback(nint monitorHandle, nint deviceContext, nint clipRectangle, nint parameter);
