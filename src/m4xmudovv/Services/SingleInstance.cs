using System;
using System.Threading;

namespace m4xmudovv.Services;

/// <summary>
/// Only one copy may run: two copies would fight over library.json/settings.json and show two tray icons.
/// A second launch asks the first one to show its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private RegisteredWaitHandle? _registration;

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, "Local\\" + name, out var created);
        IsFirst = created;
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + name + ".show");
    }

    public bool IsFirst { get; }

    public void SignalFirstInstance() => _showSignal.Set();

    /// <summary>Calls <paramref name="onSignal"/> (on a thread-pool thread) whenever another launch is attempted.</summary>
    public void ListenForSignal(Action onSignal)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => onSignal(), null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showSignal.Dispose();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
