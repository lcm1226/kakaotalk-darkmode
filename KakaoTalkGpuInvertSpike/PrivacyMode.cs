namespace KakaoTalkGpuInvertSpike;

internal enum PrivacyMode
{
    Off = 0,
    StatusOnly = 1,
    Full = 2
}

internal static class PrivacyPolicy
{
    public static PrivacyMode Next(PrivacyMode mode) => mode switch
    {
        PrivacyMode.Off => PrivacyMode.StatusOnly,
        PrivacyMode.StatusOnly => PrivacyMode.Full,
        _ => PrivacyMode.Off
    };

    public static PrivacyMode Effective(PrivacyMode manual, bool auto, bool focused, bool peek) =>
        peek ? PrivacyMode.Off : auto && !focused ? PrivacyMode.Full : manual;

    // Reference: the longest date starts 90 DIPs from the right edge.
    // Leave the column beginning one physical pixel to the right of that boundary.
    public static int StatusWidth(PrivacyMode mode, float dpiScale) =>
        mode == PrivacyMode.StatusOnly
            ? Math.Max(0, (int)Math.Round(90 * Math.Clamp(dpiScale, 0.5f, 4f)) - 1)
            : 0;

    public static string Label(PrivacyMode mode) => mode switch
    {
        PrivacyMode.StatusOnly => "Privacy: times and unread counts visible (Ctrl+H)",
        PrivacyMode.Full => "Privacy: full (Ctrl+H)",
        _ => "Privacy: off (Ctrl+H)"
    };
}
