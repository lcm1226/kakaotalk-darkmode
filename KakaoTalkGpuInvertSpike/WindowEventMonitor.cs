namespace KakaoTalkGpuInvertSpike;

internal sealed class WindowEventMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0;

    private readonly Action _windowChanged;
    private readonly Action _foregroundChanged;
    private readonly NativeMethods.WinEventProc _eventProc;
    private readonly List<nint> _hooks = [];
    private uint _processId;
    private nint _targetHandle;
    private volatile bool _disposed;

    public WindowEventMonitor(Action windowChanged, Action foregroundChanged)
    {
        _windowChanged = windowChanged;
        _foregroundChanged = foregroundChanged;
        _eventProc = OnWinEvent;
    }

    public void Attach(uint processId, nint targetHandle)
    {
        if (_disposed || processId == 0 || targetHandle == nint.Zero)
        {
            Detach();
            return;
        }

        if (_processId == processId && ReadTargetHandle() == targetHandle)
        {
            return;
        }

        Detach();
        _processId = processId;
        Interlocked.Exchange(ref _targetHandle, targetHandle);
        AddHook(EventSystemForeground, EventSystemForeground, 0);
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventSystemMinimizeStart, EventSystemMinimizeEnd);
        AddHook(EventObjectDestroy, EventObjectHide);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
    }

    public void Detach()
    {
        _processId = 0;
        Interlocked.Exchange(ref _targetHandle, nint.Zero);
        foreach (var hook in _hooks)
        {
            _ = NativeMethods.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }

    private void AddHook(uint eventMin, uint eventMax, uint? processId = null)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            nint.Zero,
            _eventProc,
            processId ?? _processId,
            0,
            WinEventOutOfContext);
        if (hook != nint.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (eventType == EventSystemForeground)
        {
            Notify(_foregroundChanged);
            return;
        }

        if (hwnd != ReadTargetHandle())
        {
            return;
        }

        if (eventType >= EventObjectDestroy && (objectId != ObjectIdWindow || childId != 0))
        {
            return;
        }

        Notify(_windowChanged);
    }

    private static void Notify(Action callback)
    {
        try
        {
            callback();
        }
        catch
        {
            // The safety timer retries without allowing a native callback to terminate the app.
        }
    }

    private nint ReadTargetHandle()
    {
        return Interlocked.CompareExchange(ref _targetHandle, nint.Zero, nint.Zero);
    }
}
