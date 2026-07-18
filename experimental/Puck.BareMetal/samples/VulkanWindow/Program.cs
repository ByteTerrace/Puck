// Puck.BareMetal VulkanWindow — minimal Vulkan window on puck, with explicit teardown.
//
// Resources register with a DisposalScope and are torn down in reverse order when the scope
// is disposed (the `using` below) — the ownership model the DI container will use: each
// registered service releases its own native resources in Dispose(). Runs with no .NET
// runtime — puck core + mimalloc allocator, freestanding.
using System;
using System.Runtime.InteropServices;
using Puck.BareMetal.VulkanWindow;

internal static unsafe class Program {
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr file, byte* buffer, int bytes, int* written, IntPtr overlapped);
    private static void Log(string message) {
        byte* buffer = stackalloc byte[256];
        int length = message.Length;

        for (int i = 0; ((i < length) && (i < 255)); i++)
            buffer[i] = (byte)message[i];
        buffer[length] = (byte)'\n';
        int written;

        WriteFile(GetStdHandle(handle: STD_OUTPUT_HANDLE), buffer, (length + 1), &written, default);
    }

    // Owns the OS window; destroyed last (registered first). One of two IDisposable
    // implementers the scope tears down via polymorphic interface dispatch.
    private sealed class WindowResource : IDisposable {
        private readonly nint _window;

        public WindowResource(nint window) => _window = window;

        public void Dispose() {
            Win32.DestroyWindow(window: _window);
            Log(message: "window destroyed");
        }
    }

    // Owns the Vulkan objects; destroyed first (registered last), before the window.
    private sealed class VulkanResource : IDisposable {
        public void Dispose() => Vk.Shutdown();
    }

    private static int Main() {
        Log(message: "Puck.BareMetal VulkanWindow: starting");

        // The scope disposes everything (in reverse) when Main returns.
        using DisposalScope scope = new DisposalScope();

        nint window = Win32.CreateMainWindow(height: 720, width: 1280);

        if (window == 0) {
            Log(message: "FAILED: could not create window");
            return 1;
        }
        scope.Track(item: new WindowResource(window: window));
        Log(message: "window created");

        if (!Vk.Initialize(windowHandle: window) || !Vk.InitializeRendering(height: 720, width: 1280)) {
            Log(message: "FAILED: Vulkan bring-up did not complete");
            return 2;
        }
        scope.Track(item: new VulkanResource());

        Log(message: "rendering; window should be cleared to color");

        // Frame cap so the run terminates without user interaction; remove it to keep the
        // window open (and rendering) until closed.
        int frame = 0;
        const int maxFrames = 180;

        while (Win32.PumpMessages()) {
            Vk.DrawFrame();
            if (++frame >= maxFrames)
                break;
        }

        Log(message: "loop ended; disposing scope (Vulkan teardown, then window)");
        return 0;
    }
}
