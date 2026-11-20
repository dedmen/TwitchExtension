using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TwitchExtension.Desktop;

/// <summary>
/// One track, in the shape of the Track JSON contract (see <c>ebs/plan.md</c>,
/// section "Contract"):
/// <list type="bullet">
///   <item><c>title</c>/<c>artist</c>/<c>album</c>/<c>source</c>: never null (empty string when unknown)</item>
///   <item><c>art</c>: <c>data:</c> URL (≤ 512 KB) or null</item>
///   <item><c>state</c>: "playing" | "paused" | "stopped" | "idle" (idle = no track)</item>
///   <item><c>positionMs</c>/<c>durationMs</c>: int ≥ 0, sampled once at push time (0 when unknown)</item>
///   <item><c>startedAtUtcMs</c>/<c>pausedForMs</c>/<c>pausedSinceUtcMs</c>: timeline baseline for the
///         EBS's serve-time position (maintained by the monitor; 0 = unknown)</item>
///   <item><c>skipEnabled</c>/<c>next</c>: added by the EbsClient at push time (setting / always null)</item>
/// </list>
/// </summary>
public sealed record Track(
    string Title, string Artist, string Album, string? Art, string Source, string State,
    int PositionMs = 0, int DurationMs = 0,
    long StartedAtUtcMs = 0, long PausedForMs = 0, long PausedSinceUtcMs = 0)
{
    public const string StatePlaying = "playing";
    public const string StatePaused = "paused";
    public const string StateStopped = "stopped";
    public const string StateIdle = "idle";

    /// <summary>The "no track" value: all empty, state "idle", art null.</summary>
    public static Track Idle { get; } = new Track("", "", "", null, "", StateIdle);
}

/// <summary>
/// Watches the <em>selected</em> SMTC session (source priority over the OS current session,
/// see <see cref="SelectSessionAsync"/>) and raises <see cref="TrackChanged"/>
/// on every relevant change (session list / current session changed, media properties
/// or playback info changed).
///
/// Apps register an SMTC session only while playing, so on <c>SessionsChanged</c> /
/// <c>CurrentSessionChanged</c> the selection is re-run and the session re-subscribed.
/// A session object is never cached across a list change.
/// </summary>
public sealed class SmtcMonitor : IDisposable
{
    private const int MaxArtBytes = 512 * 1024; // images > 512 KB are dropped (art: null)

    /// <summary>A position deviation beyond this counts as a seek/track restart, not drift.</summary>
    private const long SeekToleranceMs = 10_000;

    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _preferredSource; // AUMID substring, case-insensitive ("" = OS current session)

    // Named delegate fields so handlers can actually be unsubscribed later.
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentSessionChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _mediaPropertiesChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackInfoChanged;

    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    private volatile bool _disposed;

    // Timeline baseline for the current track (plan.md "Timeline fields"), so the EBS can
    // compute the true position at serve time. Mutated only under _gate (every Raise path
    // runs inside ResubscribeCoreAsync / RefreshCoreAsync).
    private Track? _lastTrack;          // last raised track (idle included) — change detection
    private long _startedAtUtcMs;       // when the current track's position was 0 (0 = unknown)
    private long _pausedForMs;          // accumulated paused ms for the current track
    private long _pausedSinceUtcMs;     // when the current pause began (0 while playing)

    /// <summary>Raised on every relevant change; may fire on a background thread.</summary>
    public event Action<Track>? TrackChanged;

    private SmtcMonitor(GlobalSystemMediaTransportControlsSessionManager manager, string preferredSource)
    {
        _manager = manager;
        _preferredSource = preferredSource;
        _sessionsChanged = OnSessionsChanged;
        _currentSessionChanged = OnSessionsChanged;
        _mediaPropertiesChanged = OnSessionEvent;
        _playbackInfoChanged = OnSessionEvent;
        _manager.SessionsChanged += _sessionsChanged;
        _manager.CurrentSessionChanged += _currentSessionChanged;
    }

    /// <summary>
    /// Creates the monitor and performs the initial resolve of the selected session.
    /// The manager instance is kept alive for the app's lifetime (held by the monitor).
    /// </summary>
    public static async Task<SmtcMonitor> CreateAsync(string preferredSource = "")
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
        var monitor = new SmtcMonitor(manager, preferredSource);
        await monitor.ResubscribeAsync();
        return monitor;
    }

    /// <summary>Re-reads the current session and emits a track (used right after listeners are wired up).</summary>
    public void RefreshNow() => _ = RefreshCurrentAsync();

    /// <summary>
    /// Skips to the next track: re-resolves the <em>selected</em> session (never a cached one)
    /// and sends <c>TrySkipAsync(Next)</c>. Best effort — a player without a queue (or any
    /// failure) is swallowed. A skip command must never crash the app.
    /// </summary>
    public async Task SkipAsync()
    {
        try
        {
            if (_disposed) return;
            var session = await SelectSessionAsync(_manager.GetSessions().ToList());
            if (session is null) return;
            await session.TrySkipNextAsync().AsTask();
        }
        catch
        {
            // no queue / app doesn't support it / session died: ignore
        }
    }

    public void Dispose()
    {
        _disposed = true;
        UnsubscribeAll();
        _manager.SessionsChanged -= _sessionsChanged;
        _manager.CurrentSessionChanged -= _currentSessionChanged;
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _ = ResubscribeAsync();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => _ = ResubscribeAsync();

    private void OnSessionEvent(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshCurrentAsync();

    private void OnSessionEvent(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshCurrentAsync();

    /// <summary>Re-resolves the current session, subscribes to it, emits its track (or idle).</summary>
    private async Task ResubscribeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ResubscribeCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-subscribes to <em>every</em> session in the list (not just the selected one) and
    /// re-runs the selection: a session that is not selected can still start playing later
    /// (e.g. Sendspin resumes while a browser video is current) — only then, no manager-level
    /// event fires (the list and the current session are unchanged), so the monitor must be
    /// listening on every session to re-select. A session object is never cached across a
    /// list change.
    /// </summary>
    private async Task ResubscribeCoreAsync()
    {
        UnsubscribeAll();

        var sessions = _manager.GetSessions().ToList();
        foreach (var s in sessions)
        {
            try
            {
                s.MediaPropertiesChanged += _mediaPropertiesChanged;
                s.PlaybackInfoChanged += _playbackInfoChanged;
                _watched.Add(s);
            }
            catch
            {
                // session dying mid-subscribe: skip it
            }
        }

        var session = await SelectSessionAsync(sessions);
        if (_disposed || session is null)
        {
            Raise(Track.Idle);
            return;
        }

        _session = session;
        await RefreshCoreAsync(session);
    }

    /// <summary>
    /// Session selection (plan.md "Session selection (source priority)"): when
    /// <c>preferredSource</c> is set and any session in the list has an AUMID containing it
    /// (case-insensitive) **and is an active track** (playing or paused, with content), select
    /// the first such session (prefer one that is playing); otherwise (no match, or the match
    /// is stopped/has no content — e.g. Sendspin stopped) select the OS current session.
    /// Never caches a session across a list change — this is re-run on every list change and
    /// content change.
    ///
    /// A stopped session is *not* eligible: the OS keeps its title/artist after a stop
    /// (only the playback status flips), so "has content" alone would let a stopped Sendspin
    /// keep winning over the browser video (acceptance 12 requires the fallback).
    /// </summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> SelectSessionAsync(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (_preferredSource.Length > 0)
        {
            GlobalSystemMediaTransportControlsSession? pausedMatch = null;
            GlobalSystemMediaTransportControlsSession? playingMatch = null;

            foreach (var candidate in sessions)
            {
                var aumid = candidate.SourceAppUserModelId ?? string.Empty;
                if (aumid.Length == 0 ||
                    aumid.IndexOf(_preferredSource, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Only an active track (playing/paused) wins; a stopped/none preferred
                // session has been abandoned and must not hide the current session.
                var status = candidate.GetPlaybackInfo().PlaybackStatus;
                if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    continue;

                if (!await HasContentAsync(candidate)) continue;

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    playingMatch ??= candidate;
                else
                    pausedMatch ??= candidate;
            }

            // Prefer a playing match; then a paused one; otherwise fall through.
            if (playingMatch is not null) return playingMatch;
            if (pausedMatch is not null) return pausedMatch;
        }

        return _manager.GetCurrentSession();
    }

    /// <summary>True when the session has a title or artist (content, not a dying/empty session).</summary>
    private static async Task<bool> HasContentAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask();
            return !string.IsNullOrWhiteSpace(properties?.Title)
                   || !string.IsNullOrWhiteSpace(properties?.Artist);
        }
        catch
        {
            // dying session: treat as content-less
            return false;
        }
    }

    private async Task RefreshCurrentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                Raise(Track.Idle);
                return;
            }

            // Re-resolve and re-subscribe on every content change (plan.md): the preferred
            // source may have lost its content (stop) and the selection must fall back to
            // the OS current session. A session is never cached across a re-resolve (the
            // projection exposes no stable session id; events fire only on real state
            // changes, so re-subscribing is cheap).
            await ResubscribeCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshCoreAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask();
            var info = session.GetPlaybackInfo();
            var state = info.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => Track.StatePlaying,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => Track.StatePaused,
                _ => Track.StateStopped,
            };

            // Sampled once at push time (no timer-based position polling — the overlay
            // advances the progress bar locally from this anchor). The SDK's WinRT
            // projection exposes the anchor via the session timeline (not via
            // PlaybackInfo.Position / MediaProperties.PlaybackDuration): relative to
            // StartTime, so a typical track (StartTime 0) gives the raw position.
            int positionMs = 0, durationMs = 0;
            try
            {
                var timeline = session.GetTimelineProperties();
                positionMs = (int)Math.Max(0, (timeline.Position - timeline.StartTime).TotalMilliseconds);
                durationMs = (timeline.EndTime - timeline.StartTime).TotalMilliseconds > 0
                    ? (int)(timeline.EndTime - timeline.StartTime).TotalMilliseconds
                    : 0; // 0 = unknown
            }
            catch
            {
                // timeline unavailable on this session: anchor unknown (0)
            }

            var title = Clean(properties?.Title);
            var artist = Clean(properties?.Artist);

            // A session with no content (no title and no artist) is indistinguishable
            // from "no track" — e.g. a dying session after the tab/app closed. The
            // contract's "no track" state is idle.
            if (title.Length == 0 && artist.Length == 0)
            {
                Raise(Track.Idle);
                return;
            }

            UpdateTimelineBaseline(nowUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                state, title, artist, positionMs, durationMs);

            Raise(new Track(
                title,
                artist,
                Clean(properties?.AlbumTitle),
                await ReadThumbnailAsync(properties?.Thumbnail),
                Clean(session.SourceAppUserModelId),
                state,
                positionMs,
                durationMs,
                _startedAtUtcMs,
                _pausedForMs,
                _pausedSinceUtcMs));
        }
        catch
        {
            // The session may be dying mid-read; its list removal (or the next event)
            // triggers a full re-subscribe. Just report idle for now.
            Raise(Track.Idle);
        }
    }

    /// <summary>
    /// Maintains the timeline baseline for the current track (plan.md "Timeline fields"):
    /// re-baseline on a new track (title/artist change or a backwards position reset),
    /// pause/resume accounting on state transitions, and seek self-healing when the
    /// sampled position deviates from the baseline's expectation by more than 10 s.
    /// Caller holds <c>_gate</c>.
    /// </summary>
    private void UpdateTimelineBaseline(long nowUtc, string state, string title, string artist,
        int positionMs, int durationMs)
    {
        var last = _lastTrack;
        var isPlaying = state == Track.StatePlaying;
        var isPaused = state == Track.StatePaused;

        var isNewTrack = last is null
                          || last.Title != title
                          || last.Artist != artist
                          || (last.PositionMs > 0 && positionMs < last.PositionMs - SeekToleranceMs);

        if (isNewTrack)
        {
            // Baseline = when this track's position was 0, from the sampled position —
            // even when the source reports no duration (0). The served position then
            // tracks elapsed play time (decided 2026-11-20: position must advance even
            // with unknown duration); seek self-healing re-baselines on any drift.
            _startedAtUtcMs = nowUtc - positionMs;
            _pausedForMs = 0;
            _pausedSinceUtcMs = isPaused ? nowUtc : 0;
        }
        else if (isPaused && last!.State != Track.StatePaused)
        {
            // transition into pause: start the pause clock
            _pausedSinceUtcMs = nowUtc;
        }
        else if (isPlaying && last!.State == Track.StatePaused && _pausedSinceUtcMs > 0)
        {
            // resume: accumulate the paused span
            _pausedForMs += nowUtc - _pausedSinceUtcMs;
            _pausedSinceUtcMs = 0;
        }

        // Seek self-healing: a sampled position farther than 10 s from the baseline's
        // expectation (user seek, source jump, stale sample) is treated as unknown.
        // Only when the source reports a real timeline (durationMs > 0): a 0/0 source
        // (e.g. Sendspin) never provides position information, and re-baselining to its
        // permanent 0 sample would reset the elapsed-time position on every event.
        if (isPlaying && durationMs > 0 && _startedAtUtcMs > 0)
        {
            var expected = (nowUtc - _startedAtUtcMs) - _pausedForMs;
            if (Math.Abs(positionMs - expected) > SeekToleranceMs)
            {
                _startedAtUtcMs = nowUtc - positionMs;
                _pausedForMs = 0;
            }
        }
    }

    /// <summary>Removes the per-session handlers from every session currently watched.</summary>
    private void UnsubscribeAll()
    {
        foreach (var s in _watched)
        {
            s.MediaPropertiesChanged -= _mediaPropertiesChanged;
            s.PlaybackInfoChanged -= _playbackInfoChanged;
        }
        _watched.Clear();
        _session = null;
    }

    private void Raise(Track track)
    {
        _lastTrack = track;
        TrackChanged?.Invoke(track);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Reads the thumbnail as a <c>data:</c> URL, or null if absent/too large (> 512 KB)/unreadable.
    /// The SDK projection exposes no <c>FileExtension</c> on the thumbnail reference, so the
    /// MIME type defaults to <c>image/jpeg</c> (the plan's default).
    /// </summary>
    private static async Task<string?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync().AsTask();
            using var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);
            var bytes = memory.ToArray();
            if (bytes.Length == 0 || bytes.Length > MaxArtBytes) return null;
            return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }
}
