using System.Diagnostics;
using System.Windows.Forms;

namespace TwitchExtension.Desktop;

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

        // Route async continuations on this thread through the WinForms message loop.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var config = AppConfig.Load();
        var client = new EbsClient(config);
        var app = new TrayApp(config, client, config.HasValidTenantId);

        // No valid tenantId → tray only: no EBS connection, no retry loop.
        if (config.HasValidTenantId)
        {
            client.Start();
        }
        StartMonitorAsync(app, config);

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
            Console.WriteLine($"[smtc] Could not start SMTC monitoring: {ex.GetBaseException().Message}");
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
    private readonly bool _noTenant;

    public TrayApp(AppConfig config, EbsClient client, bool hasValidTenantId)
    {
        _config = config;
        _client = client;
        _ui = SynchronizationContext.Current!;
        _noTenant = !hasValidTenantId;

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
            Text = _noTenant ? "No tenantId — set it in settings.json" : "No media",
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
            Console.WriteLine($"[ui] Could not open {_config.EbsUrl}: {ex.Message}");
        }
    }

    public void Exit()
    {
        Shutdown();
        Application.Exit();
    }

    /// <summary>
    /// Tidy shutdown (idempotent). A live NotifyIcon keeps the process alive, so it is
    /// hidden and disposed before the message loop exits.
    /// </summary>
    public void Shutdown()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        _monitor?.Dispose();
        _client.Dispose();
    }
}
