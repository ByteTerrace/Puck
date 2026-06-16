using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint WndProc(nint windowHandle, uint message, nint wParam, nint lParam);
