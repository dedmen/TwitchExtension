using System.Diagnostics;
using System.Windows.Forms;

namespace TwitchExtension.Desktop;

/// <summary>
/// Append-only log next to <c>settings.json</c> (plan.md, Logging): a <c>WinExe</c> has no
/// console, so <c>Console.WriteLine</c> goes nowhere in production. Log failures are
/// swallowed — logging must never take the app down.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "desktop.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ignore
        }
    }
}

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\TwitchExtension.Desktop.SingleInstance";

    [STAThread]
    private static int Main()
    {
        // Single instance: a second launch exits silently. The mutex is held (and
        // disposed with the app) for the whole process lifetime.
        using var singleInstance = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew) return 0;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Last-resort crash diagnostics (plan.md, Logging): a WinExe crash would be
        // invisible otherwise. Every unhandled path is logged, never re-thrown.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write($"[fatal] unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            AppLog.Write($"[fatal] unobserved task exception: {e.Exception}");
        };
        Application.ThreadException += (_, e) =>
            AppLog.Write($"[fatal] UI thread exception: {e.Exception}");

        // Route async continuations on this thread through the WinForms message loop.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // Config problems must never crash Main (plan.md, Settings): Load() normalizes,
        // the validity checks never throw, and the EbsClient constructor tolerates a
        // malformed ebsUrl (tray-only mode + tooltip).
        var config = AppConfig.Load();
        var client = new EbsClient(config);
        var hasTenant = config.HasValidTenantId;
        var hasEbsUrl = config.HasValidEbsUrl;
        var app = new TrayApp(config, client, hasTenant, hasEbsUrl);

        // No valid tenantId → tray only: no EBS connection, no retry loop, no SMTC
        // monitor (plan.md, Pushing: with nowhere to push, all its COM work would be
        // discarded by the client's guard). No valid ebsUrl → tray only, no WS attempt.
        if (hasTenant && hasEbsUrl)
        {
            client.Start();
        }
        if (hasTenant)
        {
            StartMonitorAsync(app, config);
        }

        // Runs hidden — no main window; the loop lives until Exit is chosen.
        Application.Run();

        app.Shutdown();
        return 0;
    }

    private static async void StartMonitorAsync(TrayApp app, AppConfig config)
    {
        try
        {
            var monitor = await SmtcMonitor.CreateAsync(config.PreferredSource);
            app.Attach(monitor);
        }
        catch (Exception ex)
        {
            // A WinExe has no console — a startup failure must be visible in the tray.
            AppLog.Write($"[smtc] Could not start SMTC monitoring: {ex.GetBaseException().Message}");
            app.ReportFatal("SMTC could not start");
        }
    }
}

/// <summary>
/// The tray UI: a <see cref="NotifyIcon"/> (system icon) with a state tooltip and a
/// context menu (Open EBS, Skip button in overlay, Exit). No visible window.
/// </summary>
internal sealed class TrayApp
{
    private readonly AppConfig _config;
    private readonly EbsClient _client;
    private readonly SynchronizationContext _ui;
    private NotifyIcon? _icon;
    private SmtcMonitor? _monitor;
    private Track? _track;
    private bool _ebsFailing;
    private bool _disposed;
    private readonly bool _noTenant;
    private readonly bool _badEbsUrl;
    private string? _fatalMessage;

    public TrayApp(AppConfig config, EbsClient client, bool hasValidTenantId, bool hasValidEbsUrl)
    {
        _config = config;
        _client = client;
        _ui = SynchronizationContext.Current!;
        _noTenant = !hasValidTenantId;
        _badEbsUrl = !hasValidEbsUrl;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open EBS", null, (_, _) => OpenEbs());
        var skipItem = new ToolStripMenuItem("Skip button in overlay")
        {
            Checked = _config.SkipButtonEnabled,
            CheckOnClick = true,
        };
        skipItem.CheckedChanged += (_, _) => OnSkipToggle(skipItem.Checked);
        menu.Items.Add(skipItem);
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = _noTenant
                ? "No tenantId — set it in settings.json"
                : _badEbsUrl
                    ? "Bad ebsUrl in settings.json"
                    : "No media",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _client.PushSucceeded += () => BeginUi(() => { _ebsFailing = false; UpdateTooltip(); });
        _client.PushFailed += () => BeginUi(() => { _ebsFailing = true; UpdateTooltip(); });
        _client.SkipRequested += () =>
        {
            var monitor = _monitor;
            if (monitor is not null) _ = monitor.SkipAsync();
        };
    }

    /// <summary>
    /// Surfaces a fatal startup error (e.g. SMTC could not start) in the tray tooltip —
    /// a WinExe has no console, so the log file alone would stay invisible.
    /// </summary>
    public void ReportFatal(string message) => BeginUi(() => { _fatalMessage = message; UpdateTooltip(); });

    /// <summary>Tray toggle: persist the setting and push the current snapshot immediately.</summary>
    private void OnSkipToggle(bool enabled)
    {
        _config.SkipButtonEnabled = enabled;
        _config.Save();
        _client.SetSkipEnabled(enabled);
    }

    /// <summary>Wires the monitor (called once, on the UI thread) and emits the current state.</summary>
    public void Attach(SmtcMonitor monitor)
    {
        _monitor = monitor;
        monitor.TrackChanged += OnTrackChanged;
        monitor.RefreshNow();
    }

    private void OnTrackChanged(Track track)
    {
        _client.Enqueue(track); // debounced + deduped inside the client
        BeginUi(() =>
        {
            _track = track;
            UpdateTooltip();
        });
    }

    private void BeginUi(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    private void UpdateTooltip()
    {
        if (_icon is null) return;

        if (_noTenant)
        {
            _icon.Text = "No tenantId — set it in settings.json";
            return;
        }

        if (_badEbsUrl)
        {
            _icon.Text = "Bad ebsUrl in settings.json";
            return;
        }

        if (_fatalMessage is not null)
        {
            _icon.Text = _fatalMessage;
            return;
        }

        if (_ebsFailing)
        {
            _icon.Text = "EBS unreachable";
            return;
        }

        var track = _track;
        if (track is null
            || track.State == Track.StateIdle
            || (string.IsNullOrEmpty(track.Title) && string.IsNullOrEmpty(track.Artist)))
        {
            _icon.Text = "No media";
            return;
        }

        string what = string.IsNullOrEmpty(track.Artist)
            ? track.Title
            : $"{track.Artist} – {track.Title}";
        // NotifyIcon.Text is capped at 128 chars (Sendspin titles can exceed it).
        _icon.Text = Tip(track.State switch
        {
            Track.StatePlaying => $"Now playing: {what}",
            Track.StatePaused => $"Paused: {what}",
            _ => $"Stopped: {what}",
        });
    }

    private static string Tip(string text) => text.Length <= 127 ? text : text[..124] + "…";

    private void OpenEbs()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_config.EbsUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"[ui] Could not open {_config.EbsUrl}: {ex.GetBaseException().Message}");
        }
    }

    public void Exit()
    {
        Shutdown();
        Application.Exit();
    }

    /// <summary>
    /// Tidy shutdown, idempotent (plan.md, Tray UI): the menu Exit item and the loop
    /// teardown can both reach it — a double Dispose while a fire-and-forget send is in
    /// flight would throw from an unobserved task.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        _monitor?.Dispose();
        _client.Dispose();
        AppLog.Write("[app] clean exit");
    }
}
