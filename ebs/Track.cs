using System.Text.Json.Serialization;

namespace TwitchExtension.Ebs;

/// <summary>
/// The current now-playing track.
/// Contract (canonical, see plan.md):
/// - title/artist/album/source: strings, never null (empty string when unknown)
/// - art: "data:" URL string or null
/// - state: "playing" | "paused" | "stopped" | "idle" (idle = no track)
/// - updatedAt: epoch ms, set by the EBS on every accepted track push; 0 until first push
/// - positionMs: playback position (ms) sampled by the client at push time; stored raw;
///   when the timeline fields below are set, the served position is computed at serve
///   time from them (see ServedTrackJson), otherwise the raw sample is served with the
///   stream delay applied (legacy fallback)
/// - durationMs: total track length in ms; 0 when unknown
/// - startedAtUtcMs: epoch ms (streamer-PC clock) when the track's position was 0; 0 = unknown
/// - pausedForMs: accumulated paused ms for this track; 0 when unknown
/// - pausedSinceUtcMs: epoch ms when the current pause began; 0 while playing or unknown
/// - skipEnabled: true → the overlay shows the skip-to-next button
/// - next: next track in queue, or null (hidden by the overlay)
/// </summary>
public class Track
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("album")]
    public string Album { get; set; } = string.Empty;

    [JsonPropertyName("art")]
    public string? Art { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = TrackState.Idle;

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("startedAtUtcMs")]
    public long StartedAtUtcMs { get; set; }

    [JsonPropertyName("pausedForMs")]
    public long PausedForMs { get; set; }

    [JsonPropertyName("pausedSinceUtcMs")]
    public long PausedSinceUtcMs { get; set; }

    [JsonPropertyName("skipEnabled")]
    public bool SkipEnabled { get; set; }

    [JsonPropertyName("next")]
    public NextTrack? Next { get; set; }
}

/// <summary>Next track in queue ({"title","artist"}), or null when the source doesn't provide it.</summary>
public class NextTrack
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;
}

public static class TrackState
{
    public const string Playing = "playing";
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string Idle = "idle";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Playing, Paused, Stopped, Idle };
}
