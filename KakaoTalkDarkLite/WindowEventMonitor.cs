namespace KakaoTalkDarkLite;

internal sealed class WindowEventMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;

    private readonly Action _windowChanged;
    private readonly NativeMethods.WinEventProcDelegate _eventProc;
    private readonly List<nint> _hooks = [];
    private uint _processId;
    private nint _targetHandle;
    private bool _disposed;

    public WindowEventMonitor(Action windowChanged)
    {
        _windowChanged = windowChanged;
        _eventProc = OnWinEvent;
    }

    public void Attach(uint processId, nint targetHandle)
    {
        if (_disposed || processId == 0 || targetHandle == nint.Zero)
        {
            Detach();
            return;
        }

        if (_processId == processId && _targetHandle == targetHandle)
        {
            return;
        }

        Detach();
        _processId = processId;
        _targetHandle = targetHandle;

        AddHook(EventSystemForeground, EventSystemForeground);
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventSystemMinimizeStart, EventSystemMinimizeEnd);
        AddHook(EventObjectDestroy, EventObjectHide);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
    }

    public void Detach()
    {
        foreach (var hook in _hooks)
        {
            _ = NativeMethods.UnhookWinEvent(hook);
        }

        _hooks.Clear();
        _processId = 0;
        _targetHandle = nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Detach();
        _disposed = true;
    }

    private void AddHook(uint eventMin, uint eventMax)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            nint.Zero,
            _eventProc,
            _processId,
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
        if (_disposed || hwnd != _targetHandle)
        {
            return;
        }

        if (eventType >= EventObjectDestroy && (objectId != ObjectIdWindow || childId != 0))
        {
            return;
        }

        try
        {
            _windowChanged();
        }
        catch
        {
            // The safety timer will retry without letting an unmanaged callback terminate the app.
        }
    }
}
