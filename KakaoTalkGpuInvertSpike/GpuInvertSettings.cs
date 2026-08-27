using System.Text.Json;

namespace KakaoTalkGpuInvertSpike;

internal sealed class GpuInvertSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KakaoTalkGpuInvertSpike",
        "settings.json");

    public bool Enabled { get; set; } = true;

    public bool PrivacyModeEnabled { get; set; }

    public bool AutoPrivacyEnabled { get; set; }

    public int InvertStrength { get; set; } = 100;

    public bool ShowStrengthControl { get; set; } = true;

    public static GpuInvertSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new GpuInvertSettings();
            }

            return JsonSerializer.Deserialize<GpuInvertSettings>(File.ReadAllText(SettingsPath)) ??
                new GpuInvertSettings();
        }
        catch
        {
            return new GpuInvertSettings();
        }
    }

    public static void Save(GpuInvertSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings persistence must never prevent shutdown.
        }
    }
}
