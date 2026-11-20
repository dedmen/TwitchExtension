using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwitchExtension.Desktop;

/// <summary>
/// Settings from <c>settings.json</c> (local, gitignored; see settings.example.json).
/// Missing file or unreadable file falls back to defaults — the app must never crash on a
/// config problem (plan.md, Settings): <see cref="Load"/> normalizes every value, and the
/// validity checks (<see cref="HasValidTenantId"/>, <see cref="HasValidEbsUrl"/>) never throw.
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
    public static readonly Regex TenantIdPattern =
        new("^[A-Za-z0-9_-]{3,64}$", RegexOptions.Compiled);

    /// <summary>
    /// False when <c>tenantId</c> is missing or malformed (null is treated as missing).
    /// The app still starts in that case (tray only, no EBS connection) — see <c>plan.md</c>,
    /// Settings. Never throws.
    /// </summary>
    public bool HasValidTenantId => TenantIdPattern.IsMatch(TenantId ?? string.Empty);

    /// <summary>
    /// False when <c>ebsUrl</c> is empty, not a parseable absolute URI (e.g. "http://"),
    /// or not <c>http</c>/<c>https</c> (an <c>ftp://</c> URL would spin the reconnect
    /// loop forever on a dead scheme; a bare host is not a URL at all). The app still
    /// starts in that case (tray only, no WS attempt) — see <c>plan.md</c>, Settings.
    /// Never throws.
    /// </summary>
    public bool HasValidEbsUrl
    {
        get
        {
            var url = EbsUrl?.Trim() ?? string.Empty;
            if (url.Length == 0) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

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
            Normalize(config);
            return config;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[config] Could not read {path}: {ex.GetBaseException().Message} — using defaults.");
            return new AppConfig();
        }
    }

    /// <summary>
    /// Normalizes deserialized values so nothing downstream can throw (plan.md, Settings):
    /// null/whitespace <c>tenantId</c> → "" (treated as missing); an explicit-null
    /// <c>ebsUrl</c> → "" (treated as malformed → tray-only + "Bad ebsUrl" tooltip);
    /// whitespace <c>ebsUrl</c>/<c>pushKey</c> → defaults.
    /// </summary>
    private static void Normalize(AppConfig config)
    {
        config.TenantId = config.TenantId?.Trim() ?? string.Empty;
        if (config.EbsUrl is null)
        {
            config.EbsUrl = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(config.EbsUrl))
        {
            config.EbsUrl = DefaultEbsUrl;
        }
        if (string.IsNullOrWhiteSpace(config.PushKey)) config.PushKey = DefaultPushKey;
        config.PreferredSource = config.PreferredSource?.Trim() ?? string.Empty;
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
            AppLog.Write($"[config] Could not save settings: {ex.GetBaseException().Message}");
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
