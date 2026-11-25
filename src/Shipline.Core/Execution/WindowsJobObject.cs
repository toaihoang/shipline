using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shipline.Core.Execution;

/// <summary>
/// A Windows Job Object: a group of processes that can be killed together.
/// </summary>
/// <remarks>
/// Every process that a step starts, and every process those start, joins the job automatically. So killing the job
/// kills the whole process tree, even grandchildren whose parent has already exited (Process.Kill(true) cannot find
/// those, because it walks parent-child links). The job is created with "kill on job close": when Shipline itself dies,
/// Windows closes the handle and kills the processes, so a crash never leaves orphan processes behind.
/// Jenkins solves the same problem with its ProcessTreeKiller, which scans all processes for a BUILD_ID variable.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private nint _handle;

    public WindowsJobObject()
    {
        _handle = CreateJobObject(0, 0);
        if (_handle == 0)
        {
            throw new Win32Exception();
        }

        ExtendedLimitInformation limits = default;
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        uint size = (uint)Marshal.SizeOf<ExtendedLimitInformation>();
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref limits, size))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseHandle(_handle);
            _handle = 0;
            throw new Win32Exception(error);
        }
    }

    /// <summary>
    /// Adds a started process to the job. A child that the process starts before this call is not in the job; for a
    /// shell that has only just started, that window is far shorter than its startup time.
    /// </summary>
    public void AddProcess(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Kills every process in the job.</summary>
    public void KillAll()
    {
        TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        CloseHandle(_handle);
        _handle = 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint jobAttributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint infoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    // JOBOBJECT_BASIC_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
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

    // IO_COUNTERS
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
