using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using WotBTreader.Overlay.Contracts;
using WotBTreader.Overlay.Discovery;
using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// Drives the overlay: finds the local web host via its rendezvous record,
/// lists battle sessions, and loads position samples for the selected session.
/// Status messages are user-safe: they never contain capability tokens or file paths.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private const int PageLimit = 200;
    private const int MaxPlottedPoints = 2000;

    private readonly RendezvousLocator _locator;
    private readonly Func<Uri, TreaderApiClient> _apiClientFactory;
    private readonly ITelemetryStreamService? _streamService;

    private readonly Func<SessionRow?, bool>? _launchGame;

    private TreaderApiClient? _client;
    private Uri? _clientBaseUri;
    private CancellationTokenSource? _detailLoadCts;
    private SessionRow? _selectedSession;
    private string _status = string.Empty;
    private bool _isRefreshingSessions;
    private readonly SynchronizationContext? _syncContext;

    private Dictionary<string, MapBoundaryResponse> _mapBoundaries = new(StringComparer.OrdinalIgnoreCase);
    private bool _boundariesFetched;
    private double _worldMinX;
    private double _worldMaxX;
    private double _worldMinZ;
    private double _worldMaxZ;
    private IReadOnlyList<ParticipantResponse> _participants = [];
    private IReadOnlyList<EventResponse> _events = [];
    private IReadOnlyList<PositionSampleResponse> _allPositions = [];
    private Dictionary<string, int> _teamByParticipantId = new(StringComparer.Ordinal);
    private int _eventCount;
    private TimeSpan _currentTime;
    private TimeSpan _duration;
    private bool _isPlaying;
    private double _playbackSpeed = 4.0;
    private int _damageTeam1;
    private int _damageTeam2;
    private int _killsTeam1;
    private int _killsTeam2;
    private string? _mapName;

    public MainViewModel()
        : this(new RendezvousLocator(), static baseUri => new TreaderApiClient(baseUri), null, null)
    {
    }

    public MainViewModel(
        RendezvousLocator locator,
        Func<Uri, TreaderApiClient> apiClientFactory,
        ITelemetryStreamService? streamService = null,
        Func<SessionRow?, bool>? launchGame = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        _streamService = streamService;
        _launchGame = launchGame;
        _syncContext = SynchronizationContext.Current;
        RefreshCommand = new RelayCommand(_ => _ = RefreshSessionsAsync());
        LaunchGameCommand = new RelayCommand(_ => LaunchSelectedReplay());
        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        JumpToStartCommand = new RelayCommand(_ => JumpToStart());
        JumpToEndCommand = new RelayCommand(_ => JumpToEnd());
        CycleSpeedCommand = new RelayCommand(_ => CycleSpeed());

        if (_streamService is not null)
        {
            _streamService.SessionListChanged += OnStreamSessionListChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SessionRow> Sessions { get; } = new();

    public ObservableCollection<PlotPoint> Points { get; } = new();

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The loopback base URI of the web host, set once the rendezvous record
    /// is discovered and validated. Empty string until the first successful
    /// refresh. Bound by the WebView2 dashboard to navigate to the host.
    /// </summary>
    public string BaseUri
    {
        get => _clientBaseUri?.ToString() ?? string.Empty;
    }

    public SessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            _selectedSession = value;
            OnPropertyChanged();

            if (_isRefreshingSessions)
            {
                return;
            }

            CancellationTokenSource? previous = _detailLoadCts;
            CancellationTokenSource current = new();
            _detailLoadCts = current;
            previous?.Cancel();
            previous?.Dispose();
            _ = RefreshSelectedAsync(current.Token);
        }
    }

    public ICommand RefreshCommand { get; }

    /// <summary>Launches wotblitz.exe with the currently selected replay.</summary>
    public ICommand LaunchGameCommand { get; }

    /// <summary>Toggle play/pause for the replay timeline scrubber.</summary>
    public ICommand PlayPauseCommand { get; }

    /// <summary>Jump scrubber to the beginning of the timeline.</summary>
    public ICommand JumpToStartCommand { get; }

    /// <summary>Jump scrubber to the end of the timeline.</summary>
    public ICommand JumpToEndCommand { get; }

    /// <summary>Cycle through playback speeds: 0.5x, 1x, 2x, 4x, 8x.</summary>
    public ICommand CycleSpeedCommand { get; }

    public double WorldMinX
    {
        get => _worldMinX;
        private set { _worldMinX = value; OnPropertyChanged(); }
    }

    public double WorldMaxX
    {
        get => _worldMaxX;
        private set { _worldMaxX = value; OnPropertyChanged(); }
    }

    public double WorldMinZ
    {
        get => _worldMinZ;
        private set { _worldMinZ = value; OnPropertyChanged(); }
    }

    public double WorldMaxZ
    {
        get => _worldMaxZ;
        private set { _worldMaxZ = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Participants from the most recently loaded session detail.
    /// Empty when no session is selected.
    /// </summary>
    public IReadOnlyList<ParticipantResponse> Participants
    {
        get => _participants;
        private set
        {
            _participants = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Event count from the most recently loaded session detail.
    /// Zero when no session is selected.
    /// </summary>
    public int EventCount
    {
        get => _eventCount;
        private set
        {
            _eventCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Canonical events from the most recently loaded session detail.
    /// Empty when no session is selected.
    /// </summary>
    public IReadOnlyList<EventResponse> Events
    {
        get => _events;
        private set
        {
            _events = value;
            OnPropertyChanged();
            ComputeStats();
        }
    }

    /// <summary>Total battle duration from the loaded session.</summary>
    public TimeSpan Duration
    {
        get => _duration;
        private set { _duration = value; OnPropertyChanged(); }
    }

    /// <summary>Current playback speed multiplier.</summary>
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        private set
        {
            _playbackSpeed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedLabel));
        }
    }

    /// <summary>Human-readable speed label for the cycle button.</summary>
    public string SpeedLabel => $"{_playbackSpeed:0.#}×";

    /// <summary>Total damage received by team 1.</summary>
    public int DamageTeam1
    {
        get => _damageTeam1;
        private set { _damageTeam1 = value; OnPropertyChanged(); }
    }

    /// <summary>Total damage received by team 2.</summary>
    public int DamageTeam2
    {
        get => _damageTeam2;
        private set { _damageTeam2 = value; OnPropertyChanged(); }
    }

    /// <summary>Vehicles destroyed on team 1.</summary>
    public int KillsTeam1
    {
        get => _killsTeam1;
        private set { _killsTeam1 = value; OnPropertyChanged(); }
    }

    /// <summary>Vehicles destroyed on team 2.</summary>
    public int KillsTeam2
    {
        get => _killsTeam2;
        private set { _killsTeam2 = value; OnPropertyChanged(); }
    }

    /// <summary>The map name for the currently selected session, shown on the minimap background.</summary>
    public string? MapName
    {
        get => _mapName;
        private set { _mapName = value; OnPropertyChanged(); }
    }

    /// <summary>Current scrubber position in the replay timeline.</summary>
    public TimeSpan CurrentTime
    {
        get => _currentTime;
        set
        {
            if (value == _currentTime) return;
            _currentTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTimeSeconds));
            ApplyTimeFilter();
        }
    }

    /// <summary>
    /// Slider-friendly double wrapper for <see cref="CurrentTime"/>.
    /// Two-way bindable; the slider reads and writes this property.
    /// </summary>
    public double CurrentTimeSeconds
    {
        get => _currentTime.TotalSeconds;
        set => CurrentTime = TimeSpan.FromSeconds(value);
    }

    /// <summary>Whether the timeline is auto-advancing.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Jumps the scrubber to a specific event's replay time.
    /// Called from the UI when an event row is clicked.
    /// </summary>
    public void ScrubToEventTime(TimeSpan time)
    {
        CurrentTime = time;
    }

    /// <summary>
    /// Scrubs the timeline by a relative offset, clamping to [0, Duration].
    /// Used by keyboard shortcuts (Left/Right arrow keys).
    /// </summary>
    public void ScrubRelative(TimeSpan offset)
    {
        if (_duration <= TimeSpan.Zero) return;
        TimeSpan target = _currentTime + offset;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _duration) target = _duration;
        CurrentTime = target;
    }

    /// <summary>
    /// Sets playback speed directly. Used by keyboard shortcuts (1-5 keys).
    /// Only accepts the five defined speed levels.
    /// </summary>
    public void SetPlaybackSpeed(double speed)
    {
        if (speed is 0.5 or 1.0 or 2.0 or 4.0 or 8.0)
        {
            PlaybackSpeed = speed;
        }
    }

    /// <summary>Advance the scrubber by one tick during playback.</summary>
    public void AdvancePlayback()
    {
        if (!_isPlaying || _duration <= TimeSpan.Zero) return;
        double msPerTick = 50.0 * _playbackSpeed;
        TimeSpan next = _currentTime + TimeSpan.FromMilliseconds(msPerTick);
        if (next >= _duration)
        {
            CurrentTime = TimeSpan.Zero;
        }
        else
        {
            CurrentTime = next;
        }
    }

    private void TogglePlayPause()
    {
        if (_duration <= TimeSpan.Zero) return;
        IsPlaying = !_isPlaying;
        if (_isPlaying && _currentTime >= _duration)
        {
            CurrentTime = TimeSpan.Zero;
        }
    }

    private void JumpToStart()
    {
        CurrentTime = TimeSpan.Zero;
    }

    private void JumpToEnd()
    {
        if (_duration > TimeSpan.Zero)
            CurrentTime = _duration;
    }

    private void CycleSpeed()
    {
        PlaybackSpeed = _playbackSpeed switch
        {
            0.5 => 1.0,
            1.0 => 2.0,
            2.0 => 4.0,
            4.0 => 8.0,
            _ => 0.5,
        };
    }

    private void ComputeStats()
    {
        int dmg1 = 0, dmg2 = 0, kills1 = 0, kills2 = 0;
        foreach (EventResponse evt in _events)
        {
            if (evt.Kind == "Destroyed")
            {
                if (evt.ParticipantId is string pid && _teamByParticipantId.TryGetValue(pid, out int team))
                {
                    if (team == 1) kills1++;
                    else if (team == 2) kills2++;
                }
            }
            else if (evt.Kind == "Damage" && evt.ParticipantId is string dmgPid)
            {
                int dmg = ParseDamageFromSummary(evt.Summary);
                if (dmg > 0 && _teamByParticipantId.TryGetValue(dmgPid, out int team))
                {
                    if (team == 1) dmg1 += dmg;
                    else if (team == 2) dmg2 += dmg;
                }
            }
        }

        DamageTeam1 = dmg1;
        DamageTeam2 = dmg2;
        KillsTeam1 = kills1;
        KillsTeam2 = kills2;
    }

    private static int ParseDamageFromSummary(string summary)
    {
        // Summary format: "Damage: N HP"
        int colon = summary.IndexOf(':');
        int hp = summary.LastIndexOf(" HP", StringComparison.Ordinal);
        if (colon < 0 || hp <= colon) return 0;
        string num = summary.AsSpan(colon + 1, hp - colon - 1).Trim().ToString();
        return int.TryParse(num, out int result) ? result : 0;
    }

    private void ApplyTimeFilter()
    {
        if (_allPositions.Count == 0)
        {
            Points.Clear();
            return;
        }

        // When the scrubber is at or past the end (or duration is unknown),
        // show all positions without a time filter.
        bool showAll = _duration <= TimeSpan.Zero || _currentTime >= _duration;

        List<PositionSampleResponse> source = showAll
            ? new List<PositionSampleResponse>(_allPositions)
            : _allPositions.Where(p => p.ReplayTime <= _currentTime).ToList();

        int stride = Math.Max(1, source.Count / MaxPlottedPoints);
        Points.Clear();
        for (int i = 0; i < source.Count; i += stride)
        {
            PositionSampleResponse sample = source[i];
            int teamNumber = 0;
            if (sample.ParticipantId is not null)
                _teamByParticipantId.TryGetValue(sample.ParticipantId, out teamNumber);
            Points.Add(new PlotPoint(sample.RawX, sample.RawZ, teamNumber, sample.ParticipantId));
        }
    }

    public async Task RefreshSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _isRefreshingSessions = true;
        try
        {
            await RefreshSessionsCoreAsync(cancellationToken);
        }
        finally
        {
            _isRefreshingSessions = false;
        }

        // After a successful refresh, connect the stream service so future
        // session list changes arrive via push instead of polling.
        if (_clientBaseUri is not null && _streamService is not null)
        {
            _ = _streamService.ConnectAsync(_clientBaseUri, CancellationToken.None);
        }
    }

    private async Task RefreshSessionsCoreAsync(CancellationToken cancellationToken)
    {
        RendezvousResult rendezvous = _locator.Locate();
        if (rendezvous.Status != RendezvousStatus.Found || rendezvous.Record is null)
        {
            Status = rendezvous.Status switch
            {
                RendezvousStatus.NotFound => "Waiting for host…",
                RendezvousStatus.Stale => "Host record expired",
                _ => "Host record invalid",
            };
            return;
        }

        try
        {
            TreaderApiClient client = GetOrCreateClient(new Uri(rendezvous.Record.BaseUri, UriKind.Absolute));
            SessionPageResponse? page = await client.GetSessionsAsync(0, PageLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Sessions.Clear();
            if (page is not null)
            {
                foreach (SessionSummaryResponse summary in page.Items)
                {
                    Sessions.Add(new SessionRow(
                        Guid.Parse(summary.Session.BattleSessionId),
                        summary.Session.MapName ?? summary.Session.MapId ?? "unknown",
                        summary.Session.MapId,
                        summary.Session.BattleTimeUtc,
                        summary.ParticipantCount,
                        summary.PositionCount));
                }
            }

            Status = $"{Sessions.Count} session(s)";

            // Fetch map boundaries lazily on first successful session refresh.
            if (!_boundariesFetched)
            {
                _boundariesFetched = true;
                _ = FetchMapBoundariesAsync(client);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            Status = "Host unreachable";
        }
    }

    public async Task RefreshSelectedAsync(CancellationToken cancellationToken = default)
    {
        SessionRow? selected = SelectedSession;
        TreaderApiClient? client = _client;
        if (selected is null || client is null)
        {
            Participants = [];
            EventCount = 0;
            Events = [];
            MapName = null;
            return;
        }

        try
        {
            SessionDetailResponse? detail = await client.GetSessionDetailAsync(selected.BattleSessionId, cancellationToken);
            if (detail is null)
            {
                Participants = [];
                EventCount = 0;
                Events = [];
                MapName = null;
                return;
            }

            _teamByParticipantId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ParticipantResponse participant in detail.Participants)
            {
                if (participant.TeamNumber is int team)
                {
                    _teamByParticipantId[participant.ParticipantId] = team;
                }
            }

            _allPositions = detail.Positions;
            Duration = detail.Session?.Duration ?? TimeSpan.Zero;
            _currentTime = _duration;
            IsPlaying = false;
            MapName = detail.Session?.MapName;

            Participants = detail.Participants;
            EventCount = detail.EventCount;
            Events = detail.Events;

            ApplyTimeFilter();

            ApplyMapBoundaries(selected);

            if (detail.PositionsTruncated)
            {
                Status = $"showing latest 5000 of {detail.TotalPositionCount} positions";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            Status = "Host unreachable";
            Participants = [];
            EventCount = 0;
            Events = [];
            MapName = null;
        }
    }

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void LaunchSelectedReplay()
    {
        bool launched = _launchGame?.Invoke(SelectedSession) ?? false;
        Status = launched ? "Game launched" : "Launch failed — check game installation";
    }

    private TreaderApiClient GetOrCreateClient(Uri baseUri)
    {
        if (_client is not null && _clientBaseUri == baseUri)
        {
            return _client;
        }

        // Cancel any in-flight detail load before swapping clients so the
        // old HttpClient is never used after disposal.
        _detailLoadCts?.Cancel();

        TreaderApiClient? oldClient = _client;
        _client = _apiClientFactory(baseUri);
        _clientBaseUri = baseUri;
        OnPropertyChanged(nameof(BaseUri));
        oldClient?.Dispose();
        return _client;
    }

    private void OnStreamSessionListChanged(object? sender, EventArgs e)
    {
        // SignalR callbacks run on non-UI threads without a
        // SynchronizationContext. ObservableCollection mutations must
        // happen on the WPF dispatcher thread, so marshal the refresh.
        if (_syncContext is not null)
        {
            _syncContext.Post(static state => _ = ((MainViewModel)state!).RefreshSessionsAsync(), this);
        }
        else
        {
            _ = RefreshSessionsAsync();
        }
    }

    private async Task FetchMapBoundariesAsync(TreaderApiClient client)
    {
        try
        {
            IReadOnlyList<MapBoundaryResponse> boundaries = await client
                .GetMapBoundariesAsync();
            _mapBoundaries = new Dictionary<string, MapBoundaryResponse>(
                StringComparer.OrdinalIgnoreCase);
            foreach (MapBoundaryResponse b in boundaries)
            {
                _mapBoundaries[b.MapId] = b;
            }

            ApplyMapBoundaries(SelectedSession);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException
            or JsonException or ObjectDisposedException)
        {
            // Boundaries are optional — plotting falls back to per-session extents.
        }
    }

    private void ApplyMapBoundaries(SessionRow? session)
    {
        if (session?.MapId is not null
            && _mapBoundaries.TryGetValue(session.MapId, out MapBoundaryResponse? b))
        {
            WorldMinX = b.MinX;
            WorldMaxX = b.MaxX;
            WorldMinZ = b.MinZ;
            WorldMaxZ = b.MaxZ;
        }
        else
        {
            WorldMinX = WorldMaxX = WorldMinZ = WorldMaxZ = 0;
        }
    }
}
