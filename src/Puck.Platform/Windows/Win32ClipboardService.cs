using System.Runtime.InteropServices;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

public sealed partial class Win32ClipboardService : IClipboardService {
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public void SetText(string text) {
        ArgumentNullException.ThrowIfNull(text);

        if (!User32.OpenClipboard(windowHandle: 0)) {
            return;
        }

        try {
            if (!User32.EmptyClipboard()) {
                return;
            }

            var byteCount = ((text.Length + 1) * sizeof(char));
            var memoryHandle = Kernel32.GlobalAlloc(
                bytes: (nuint)byteCount,
                flags: GmemMoveable
            );

            if (memoryHandle == 0) {
                return;
            }

            var buffer = Kernel32.GlobalLock(memoryHandle: memoryHandle);

            if (buffer == 0) {
                _ = Kernel32.GlobalFree(memoryHandle: memoryHandle);
                return;
            }

            try {
                Marshal.Copy(
                    destination: buffer,
                    length: text.Length,
                    source: text.ToCharArray(),
                    startIndex: 0
                );
                Marshal.WriteInt16(
                    ofs: (text.Length * sizeof(char)),
                    ptr: buffer,
                    val: 0
                );
            } finally {
                _ = Kernel32.GlobalUnlock(memoryHandle: memoryHandle);
            }

            if (User32.SetClipboardData(
                format: CfUnicodeText,
                memoryHandle: memoryHandle
            ) == 0) {
                _ = Kernel32.GlobalFree(memoryHandle: memoryHandle);
            }
        } finally {
            _ = User32.CloseClipboard();
        }
    }
    public bool TryGetText(out string text) {
        text = string.Empty;

        if (!User32.OpenClipboard(windowHandle: 0)) {
            return false;
        }

        try {
            var dataHandle = User32.GetClipboardData(format: CfUnicodeText);

            if (dataHandle == 0) {
                return false;
            }

            var buffer = Kernel32.GlobalLock(memoryHandle: dataHandle);

            if (buffer == 0) {
                return false;
            }

            try {
                text = (Marshal.PtrToStringUni(ptr: buffer) ?? string.Empty);
                return (text.Length > 0);
            } finally {
                _ = Kernel32.GlobalUnlock(memoryHandle: dataHandle);
            }
        } finally {
            _ = User32.CloseClipboard();
        }
    }
}
