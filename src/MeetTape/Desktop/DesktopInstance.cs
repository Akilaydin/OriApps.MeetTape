namespace OriApps.MeetTape.Desktop;

internal sealed class DesktopInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private RegisteredWaitHandle? listener;
    public bool IsPrimary { get; }

    public DesktopInstance(string name)
    {
        mutex = new Mutex(false, name);
        try
        {
            try { IsPrimary = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsPrimary = true; }
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        }
        catch
        {
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void ActivateExisting() => activation.Set();

    public void Listen(Action activate) => listener = ThreadPool.RegisterWaitForSingleObject(activation,
        (_, _) =>
        {
            try { activate(); }
            catch (InvalidOperationException) { /* The desktop lifetime may already be shutting down. */ }
        }, null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        if (listener is not null)
        {
            using var finished = new ManualResetEvent(false);
            if (listener.Unregister(finished)) finished.WaitOne();
        }
        activation.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
