using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Puck.Cli;

/// <summary>
/// The job every World this process starts joins (<see cref="WorldLaunch"/>), with any child that starts Worlds of its
/// own (a sweep's <c>puck test</c>), so the operating system ends them when this process ends, however it ends (an
/// interrupt, a harness timeout, <c>taskkill</c>). On Windows it is a job object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the handle is never closed, so the kernel closes it when this process exits
/// and kills every process in the job, each child's own children included. Nested jobs (Windows 8 and later) let a CLI
/// that itself runs inside a job (a CI agent's, or a parent CLI's) create its own.
/// </summary>
public static partial class ChildProcessJob {
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Job = new(valueFactory: Create, mode: LazyThreadSafetyMode.ExecutionAndPublication);

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct BasicLimitInformation {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct IoCounters {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct ExtendedLimitInformation {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, nint name);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int informationClass, void* information, uint length);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    /// <summary>Adds a just-started child to the job. Elsewhere than Windows nothing joins it: a World's own exit backstop
    /// (<c>--exit-after-seconds</c>) is what bounds it there.</summary>
    /// <param name="process">The started process.</param>
    /// <exception cref="InvalidOperationException">The job could not be created, or the process could not join it.</exception>
    public static void Assign(Process process) {
        if (!OperatingSystem.IsWindows()) { return; }
        if (!AssignProcessToJobObject(job: Job.Value, process: process.Handle) && !process.HasExited) {
            throw new InvalidOperationException(message: $"process {process.Id} could not join the job that ends it with this process: {new Win32Exception(error: Marshal.GetLastPInvokeError()).Message}");
        }
    }

    private static unsafe nint Create() {
        var job = CreateJobObject(attributes: 0, name: 0);

        if (job == 0) {
            throw new InvalidOperationException(message: $"the job that ends a World with this process could not be created: {new Win32Exception(error: Marshal.GetLastPInvokeError()).Message}");
        }

        var information = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };

        if (!SetInformationJobObject(information: &information, informationClass: JobObjectExtendedLimitInformation, job: job, length: (uint)sizeof(ExtendedLimitInformation))) {
            throw new InvalidOperationException(message: $"the job that ends a World with this process could not be set to kill on close: {new Win32Exception(error: Marshal.GetLastPInvokeError()).Message}");
        }

        return job;
    }
}
