using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Remake;

/// <summary>让 watchdog 退出时由内核自动清理其 worker 子进程。</summary>
internal sealed unsafe class JobObjectLease : IDisposable
{
    private readonly SafeFileHandle _job;

    public JobObjectLease()
    {
        _job = PInvoke.CreateJobObject(null, default);
        if (_job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败。");

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = (JOB_OBJECT_LIMIT)(JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk);
        if (PInvoke.SetInformationJobObject(
                new HANDLE(_job.DangerousGetHandle()),
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                &limits,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)).Value == 0)
        {
            int error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject 失败。");
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectLimitSilentBreakawayOk = 0x1000;

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_job.IsClosed || _job.IsInvalid)
            throw new ObjectDisposedException(nameof(JobObjectLease));

        // worker 启动后立即加入 job；breakaway 允许 keyFunc_run 启动的程序独立存活。
        var processHandle = new HANDLE(process.Handle);
        if (PInvoke.AssignProcessToJobObject(
                new HANDLE(_job.DangerousGetHandle()),
                processHandle).Value == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 失败。");
    }

    public void Dispose() => _job.Dispose();
}
