using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Presentation;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Post;

/// <summary>The process-isolated hostile-window proof for the native capture lifetime contract.</summary>
internal static class CaptureLifetimeProbe {
    private const int CloseDeadlineMilliseconds = 750;
    // Match WorldFeedProfile.Default without coupling the observable platform POST to World internals.
    private const int TargetHeight = 240;
    private const int TargetWidth = 320;

    public static bool TryRun(string[] args, out int exitCode) {
        if (!args.Contains(value: "--capture-probe", comparer: StringComparer.OrdinalIgnoreCase)) {
            exitCode = 0;
            return false;
        }

        exitCode = Run();
        return true;
    }

    private static int Run() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
            Console.Out.WriteLine(value: "PROBE capture-lifetime skip | Windows Graphics Capture requires Windows 10 2004 (build 19041) or newer");
            return 0;
        }

        var service = new Win32NativeImageCaptureService();
        if (!service.IsSupported) {
            Console.Out.WriteLine(value: "PROBE capture-lifetime skip | Windows Graphics Capture is unavailable in this desktop session");
            return 0;
        }

        using var target = new CaptureProbeWindow();
        if (service.TryCreateWindowCapture(windowTitleFragment: $"missing-{Guid.NewGuid():N}", width: TargetWidth, height: TargetHeight, refreshRateHz: 60.0, feed: out var missing) || (missing is not null)) {
            return Fail(message: "creation failure returned a live feed");
        }

        if (!service.TryCreateWindowCapture(windowTitleFragment: target.Title, width: TargetWidth, height: TargetHeight, refreshRateHz: 60.0, feed: out var feed)) {
            Console.Out.WriteLine(value: "PROBE capture-lifetime skip | the compositor service could not open the responsive probe window");
            return 0;
        }

        Surface firstFrame;
        using (feed) {
            if (!TryWaitForFrame(feed: feed, timeout: TimeSpan.FromSeconds(value: 3), surface: out firstFrame)) {
                Console.Out.WriteLine(value: "PROBE capture-lifetime skip | the interactive compositor produced no probe-window frame");
                return 0;
            }

            if (!IsValidFrame(surface: in firstFrame)) {
                return Fail(message: "the responsive probe window did not produce a non-flat BGRA frame at the requested extent");
            }

            var maximumReadMilliseconds = MeasureNonblockingReads(feed: feed, attempts: 256);
            if (maximumReadMilliseconds > 50.0) {
                return Fail(message: $"TryCapture blocked for {maximumReadMilliseconds:0.###}ms");
            }

            target.Resize(width: 500, height: 300);
            if (!TryWaitForFrame(feed: feed, timeout: TimeSpan.FromSeconds(value: 3), surface: out var resized) || !HasRequestedExtent(surface: in resized)) {
                return Fail(message: "target resize did not preserve atomic requested output dimensions");
            }

            target.Minimize();
            Thread.Sleep(millisecondsTimeout: 100);
            var minimizedReadMilliseconds = MeasureNonblockingReads(feed: feed, attempts: 128);
            if (feed.IsEnded || (minimizedReadMilliseconds > 50.0)) {
                return Fail(message: "a minimized target ended the feed or blocked consumption");
            }
            target.Restore();

            target.Pumping = false;
            var hostileClose = Stopwatch.StartNew();
            feed.Dispose();
            hostileClose.Stop();
            target.Pumping = true;
            if (hostileClose.ElapsedMilliseconds > CloseDeadlineMilliseconds) {
                return Fail(message: $"a non-pumping target delayed feed disposal for {hostileClose.ElapsedMilliseconds}ms");
            }
        }

        // A deliberately slow producer proves that a fast consumer samples only completed revisions and that the feed
        // owns its requested cadence instead of letting WGC's display cadence drive uploads.
        const double slowRateHz = 5.0;
        if (!service.TryCreateWindowCapture(windowTitleFragment: target.Title, width: TargetWidth, height: TargetHeight, refreshRateHz: slowRateHz, feed: out var slowFeed)) {
            return Fail(message: "the slow-producer feed could not open");
        }

        using (slowFeed) {
            if (!TryWaitForFrame(feed: slowFeed, timeout: TimeSpan.FromSeconds(value: 3), surface: out _)) {
                return Fail(message: "the slow producer published no initial frame");
            }

            var cadence = MeasureCadence(feed: slowFeed, duration: TimeSpan.FromSeconds(value: 1.2));
            if ((cadence.Frames < 3) || (cadence.Frames > 8) || (cadence.MaximumReadMilliseconds > 50.0)) {
                return Fail(message: $"the {slowRateHz:0.#}Hz feed published {cadence.Frames} revisions in 1.2s or blocked for {cadence.MaximumReadMilliseconds:0.###}ms");
            }
        }

        var process = Process.GetCurrentProcess();
        var handlesBeforeCycles = process.HandleCount;
        var threadsBeforeCycles = process.Threads.Count;
        var worstCycleCloseMilliseconds = 0L;
        for (var cycle = 0; cycle < 8; cycle++) {
            if (!service.TryCreateWindowCapture(windowTitleFragment: target.Title, width: TargetWidth, height: TargetHeight, refreshRateHz: 120.0, feed: out var cycleFeed)) {
                return Fail(message: $"create/capture/dispose cycle {cycle} could not open the target");
            }

            if ((cycle & 1) != 0) {
                _ = TryWaitForFrame(feed: cycleFeed, timeout: TimeSpan.FromSeconds(value: 2), surface: out _);
            }

            var timer = Stopwatch.StartNew();
            cycleFeed.Dispose();
            timer.Stop();
            worstCycleCloseMilliseconds = Math.Max(val1: worstCycleCloseMilliseconds, val2: timer.ElapsedMilliseconds);
            if (timer.ElapsedMilliseconds > CloseDeadlineMilliseconds) {
                return Fail(message: $"cycle {cycle} disposal took {timer.ElapsedMilliseconds}ms");
            }
        }

        // C#/WinRT event-source caches are weak/finalizable even after the native IClosable and owned COM reference
        // have been released. Collect here so this assertion detects rooted native resources, not projection wrappers
        // that the runtime is intentionally free to retain until its next collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var resourceDeadline = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
        var handleGrowth = int.MaxValue;
        var threadGrowth = int.MaxValue;
        do {
            Thread.Sleep(millisecondsTimeout: 50);
            process.Refresh();
            handleGrowth = process.HandleCount - handlesBeforeCycles;
            threadGrowth = process.Threads.Count - threadsBeforeCycles;
        } while (((handleGrowth > 12) || (threadGrowth > 4)) && (Stopwatch.GetTimestamp() < resourceDeadline));

        if ((handleGrowth > 12) || (threadGrowth > 4)) {
            return Fail(message: $"repeated cycles grew resources by {handleGrowth} handles and {threadGrowth} threads");
        }

        if (!service.TryCreateWindowCapture(windowTitleFragment: target.Title, width: TargetWidth, height: TargetHeight, refreshRateHz: 120.0, feed: out var concurrentFeed)) {
            return Fail(message: "the concurrent-disposal feed could not open");
        }

        _ = TryWaitForFrame(feed: concurrentFeed, timeout: TimeSpan.FromSeconds(value: 2), surface: out _);
        Exception? readerFailure = null;
        var keepReading = true;
        var reader = Task.Run(action: () => {
            try {
                while (Volatile.Read(location: ref keepReading)) {
                    _ = concurrentFeed.TryCapture(surface: out _);
                }
            } catch (Exception exception) {
                readerFailure = exception;
            }
        });
        var concurrentClose = Stopwatch.StartNew();
        concurrentFeed.Dispose();
        concurrentClose.Stop();
        Volatile.Write(location: ref keepReading, value: false);
        if (!reader.Wait(millisecondsTimeout: 1000) || (readerFailure is not null) || (concurrentClose.ElapsedMilliseconds > CloseDeadlineMilliseconds)) {
            return Fail(message: $"concurrent TryCapture/Dispose failed (close {concurrentClose.ElapsedMilliseconds}ms, reader {readerFailure?.GetType().Name ?? "timeout"})");
        }

        if (!service.TryCreateWindowCapture(windowTitleFragment: target.Title, width: TargetWidth, height: TargetHeight, refreshRateHz: 60.0, feed: out var closingFeed)) {
            return Fail(message: "the target-closure feed could not open");
        }

        _ = TryWaitForFrame(feed: closingFeed, timeout: TimeSpan.FromSeconds(value: 2), surface: out _);
        target.Close();
        if (!SpinWait.SpinUntil(condition: () => closingFeed.IsEnded, millisecondsTimeout: 2000)) {
            closingFeed.Dispose();
            return Fail(message: "destroying the target did not end the feed");
        }

        var endedClose = Stopwatch.StartNew();
        closingFeed.Dispose();
        endedClose.Stop();
        if (endedClose.ElapsedMilliseconds > CloseDeadlineMilliseconds) {
            return Fail(message: $"disposing after target closure took {endedClose.ElapsedMilliseconds}ms");
        }

        var monitor = RunMonitorScenario(service: service);
        if (!monitor.Ok) {
            return Fail(message: monitor.Note);
        }

        Console.Out.WriteLine(value: $"PROBE capture-lifetime ok | non-flat {TargetWidth}x{TargetHeight} BGRA; 5Hz latest-result cadence and reads nonblocking; hung/minimized/resized/destroyed targets bounded; 8 cycles grew {handleGrowth} handles/{threadGrowth} threads; worst close {worstCycleCloseMilliseconds}ms; {monitor.Note}");
        return 0;
    }

    // Lenient primary-monitor path: whole-monitor capture reuses the window feed's pump/scale/dispose. Headless or CI
    // compositors may expose no monitor or yield no frame, so a missing target or empty feed is a skip-grade note; only
    // a blocking read or an unbounded dispose is a hard failure.
    [SupportedOSPlatform("windows10.0.19041")]
    private static (bool Ok, string Note) RunMonitorScenario(Win32NativeImageCaptureService service) {
        if (!service.TryCreateMonitorCapture(monitorIndex: 0, width: TargetWidth, height: TargetHeight, refreshRateHz: 5.0, feed: out var feed)) {
            return (Ok: true, Note: "monitor skipped: no primary monitor");
        }

        using (feed) {
            var hadFrame = TryWaitForFrame(feed: feed, timeout: TimeSpan.FromSeconds(value: 1.2), surface: out _);
            var readMilliseconds = MeasureNonblockingReads(feed: feed, attempts: 128);
            if (readMilliseconds > 50.0) {
                return (Ok: false, Note: $"monitor TryCapture blocked for {readMilliseconds:0.###}ms");
            }

            var close = Stopwatch.StartNew();
            feed.Dispose();
            close.Stop();
            if (close.ElapsedMilliseconds > CloseDeadlineMilliseconds) {
                return (Ok: false, Note: $"monitor feed disposal took {close.ElapsedMilliseconds}ms");
            }

            return (Ok: true, Note: hadFrame ? "monitor feed ok" : "monitor skipped: no frame");
        }
    }

    private static bool TryWaitForFrame(INativeImageCaptureFeed feed, TimeSpan timeout, out Surface surface) {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline) {
            if (feed.TryCapture(surface: out surface)) {
                return true;
            }

            if (feed.IsEnded) {
                break;
            }

            Thread.Sleep(millisecondsTimeout: 5);
        }

        surface = default;
        return false;
    }

    private static bool IsValidFrame(in Surface surface) {
        if (!HasRequestedExtent(surface: in surface) || (surface.Format != SurfaceFormat.B8G8R8A8Unorm)) {
            return false;
        }

        var pixels = MemoryMarshal.Cast<byte, uint>(span: surface.Pixels.Span);
        if (pixels.IsEmpty) {
            return false;
        }

        var first = pixels[0];
        foreach (var pixel in pixels[1..]) {
            if (pixel != first) {
                return true;
            }
        }

        return false;
    }

    private static bool HasRequestedExtent(in Surface surface) =>
        surface.IsCpuPixels && (surface.Width == TargetWidth) && (surface.Height == TargetHeight) && (surface.Pixels.Length == (TargetWidth * TargetHeight * 4));

    private static double MeasureNonblockingReads(INativeImageCaptureFeed feed, int attempts) {
        var maximumTicks = 0L;
        for (var attempt = 0; attempt < attempts; attempt++) {
            var start = Stopwatch.GetTimestamp();
            _ = feed.TryCapture(surface: out _);
            maximumTicks = Math.Max(val1: maximumTicks, val2: Stopwatch.GetTimestamp() - start);
        }

        return ((double)maximumTicks * 1000.0) / Stopwatch.Frequency;
    }

    private static (int Frames, double MaximumReadMilliseconds) MeasureCadence(INativeImageCaptureFeed feed, TimeSpan duration) {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var frames = 0;
        var maximumTicks = 0L;
        while (Stopwatch.GetTimestamp() < deadline) {
            var start = Stopwatch.GetTimestamp();
            if (feed.TryCapture(surface: out _)) {
                frames++;
            }
            maximumTicks = Math.Max(val1: maximumTicks, val2: Stopwatch.GetTimestamp() - start);
            Thread.Sleep(millisecondsTimeout: 1);
        }

        return (Frames: frames, MaximumReadMilliseconds: ((double)maximumTicks * 1000.0) / Stopwatch.Frequency);
    }

    private static int Fail(string message) {
        Console.Error.WriteLine(value: $"PROBE capture-lifetime fail | {message}");
        return 1;
    }
}
