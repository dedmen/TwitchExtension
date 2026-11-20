using System.Text.Json;

namespace TwitchExtension.Desktop;

/// <summary>
/// Settings from <c>settings.json</c> (local, gitignored; see settings.example.json).
/// Missing file or unreadable file falls back to defaults — the app must not crash on config.
/// </summary>
public sealed class AppConfig
{
    public const string DefaultEbsUrl = "http://127.0.0.1:5080";
    public const string DefaultPushKey = "dev-push-key-change-me";

    public string EbsUrl { get; set; } = DefaultEbsUrl;
    public string PushKey { get; set; } = DefaultPushKey;
    public string TenantId { get; set; } = string.Empty;
    public bool SkipButtonEnabled { get; set; } = true;

    /// <summary>
    /// Optional case-insensitive substring of a session's AUMID (e.g. "sendspin") that pins
    /// the monitor to that source while it has content. Empty = pure OS-current-session
    /// behavior. See <c>plan.md</c>, "Session selection (source priority)".
    /// </summary>
    public string PreferredSource { get; set; } = string.Empty;

    /// <summary>Streamer-unique tenant ID (same value as on the extension's settings page).</summary>
    public static readonly System.Text.RegularExpressions.Regex TenantIdPattern =
        new("^[A-Za-z0-9_-]{3,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// False when <c>tenantId</c> is missing or malformed. The app still starts in that
    /// case (tray only, no EBS connection) — see <c>plan.md</c>, Settings.
    /// </summary>
    public bool HasValidTenantId => TenantIdPattern.IsMatch(TenantId);

    public static AppConfig Load()
    {
        string? path = FindSettingsFile();
        if (path is null)
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config is null)
            {
                return new AppConfig();
            }
            if (string.IsNullOrWhiteSpace(config.EbsUrl)) config.EbsUrl = DefaultEbsUrl;
            if (string.IsNullOrWhiteSpace(config.PushKey)) config.PushKey = DefaultPushKey;
            config.PreferredSource = config.PreferredSource?.Trim() ?? string.Empty;
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Could not read {path}: {ex.Message} — using defaults.");
            return new AppConfig();
        }
    }

    /// <summary>
    /// Persists the current settings (tray-menu toggle). Never throws — a config write
    /// failure must not take the app down.
    /// </summary>
    public void Save()
    {
        try
        {
            string path = FindSettingsFile() ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
            var json = JsonSerializer.Serialize(new
            {
                ebsUrl = EbsUrl,
                pushKey = PushKey,
                tenantId = TenantId,
                skipButtonEnabled = SkipButtonEnabled,
                preferredSource = PreferredSource,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Could not save settings: {ex.Message}");
        }
    }

    private static string? FindSettingsFile()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "settings.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
