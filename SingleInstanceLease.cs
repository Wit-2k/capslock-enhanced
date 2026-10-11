namespace Remake;

/// <summary>限制一个登录会话内只有一个 watchdog 父进程。</summary>
internal sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = @"Local\CapsLockPlus";
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static bool TryAcquire(out SingleInstanceLease? lease)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            lease = null;
            return false;
        }

        lease = new SingleInstanceLease(mutex);
        return true;
    }

    public void Dispose()
    {
        if (!_ownsMutex)
            return;

        // Mutex 由入口线程取得，因此也由入口线程释放。
        _ownsMutex = false;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
