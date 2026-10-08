using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Govorun.Core.Hotkeys;
using Serilog;

namespace Govorun.Core.Settings;

/// <summary>
/// The complete (deliberately tiny — "zero settings") persisted state,
/// stored in %APPDATA%\Govorun\settings.json.
/// </summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HotkeyMode Hotkey { get; set; } = HotkeyMode.CtrlWin;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActivationMode Activation { get; set; } = ActivationMode.PushToTalk;

    public bool OnboardingCompleted { get; set; }

    public bool AutoStart { get; set; } = true;

    public double? BenchmarkRtf { get; set; }

    /// <summary>WASAPI capture device ID; null = system default.</summary>
    public string? MicDeviceId { get; set; }

    /// <summary>
    /// Daily check against the GitHub releases API — the app's only network request.
    /// On by default, and the Settings tab says plainly what it does.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Version the user was already told about, so we don't nag twice.</summary>
    public string? SkippedVersion { get; set; }

    // Usage counters: they drive the donation ask ("you dictated N words") and nothing else.
    public DateTime? FirstRunUtc { get; set; }
    public int DictationCount { get; set; }
    public long WordsDictated { get; set; }

    public int DonationPromptsShown { get; set; }
    public DateTime? LastDonationPromptUtc { get; set; }

    /// <summary>Set by "I already donated" — suppresses the ask for good.</summary>
    public bool DonationPromptSuppressed { get; set; }

    private static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Govorun");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static string LogPath => Path.Combine(Directory, "log.txt");

    public static string HistoryPath => Path.Combine(Directory, "history.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings — start fresh rather than crash at boot.
        }
        return new AppSettings();
    }

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    /// <summary>
    /// Best-effort persistence. Called from settings handlers on the UI thread, so a
    /// locked or full %APPDATA% must not surface as an unhandled dispatcher exception.
    /// Writes to a temp file and renames, so a crash mid-write can't leave an empty
    /// settings.json that would silently reset the user's hotkey and mic.
    /// </summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SaveOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings to {Path}", FilePath);
        }
    }
}
