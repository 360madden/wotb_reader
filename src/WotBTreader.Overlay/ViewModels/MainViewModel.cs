using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotBTreader.ApiContracts;
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
    private readonly Func<Uri, string?, TreaderApiClient> _apiClientFactory;
    private readonly ITelemetryStreamService? _streamService;

    private TreaderApiClient? _client;
    private Uri? _clientBaseUri;
    private CancellationTokenSource? _detailLoadCts;
    private long _detailLoadGeneration;
    private SessionRow? _selectedSession;
    private string _status = string.Empty;
    private bool _isRefreshingSessions;
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _liveObservationTimeoutCts;
    private static readonly TimeSpan LiveObservationTimeout = TimeSpan.FromSeconds(2);

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
    private ImageSource? _minimapImageSource;
    private readonly Dictionary<string, ImageSource> _minimapCache = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private double? _livePlayerPositionX;
    private double? _livePlayerPositionZ;
    private int? _livePlayerHP;
    private double? _liveReplayTimeSeconds;
    private bool _hasLiveMemoryObservation;
    private double? _livePlayerYaw;
    private const int MaxLiveTrailPoints = 50;

    // W2S HUD state: the projected nameplates + beacons rendered over the game window.
    private readonly ObservableCollection<NameplateItem> _nameplates = [];
    private readonly ObservableCollection<BeaconItem> _beacons = [];
    private readonly ObservableCollection<PipItem> _pips = [];
    private readonly ObservableCollection<MinimapItem> _minimapItems = [];
    private readonly ObservableCollection<MinimapBeaconItem> _minimapBeacons = [];
    private readonly ObservableCollection<KillItem> _killFeed = [];
    private readonly ObservableCollection<ScoreboardItem> _scoreboard = [];
    private CancellationTokenSource? _frameLoadCts;
    private long _frameLoadGeneration;
    private double _hudFovDegrees = 90.0;
    private double? _lastFrameReplayTimeSeconds;
    private double? _minimapCameraX;
    private double? _minimapCameraZ;
    private double? _minimapCameraYaw;

    public MainViewModel()
        : this(new RendezvousLocator(), static (baseUri, capability) => new TreaderApiClient(baseUri, capability: capability), null)
    {
    }

    public MainViewModel(
        RendezvousLocator locator,
        Func<Uri, string?, TreaderApiClient> apiClientFactory,
        ITelemetryStreamService? streamService = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        _streamService = streamService;
        _syncContext = SynchronizationContext.Current;
        RefreshCommand = new RelayCommand(_ => _ = RefreshSessionsAsync());
        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        JumpToStartCommand = new RelayCommand(_ => JumpToStart());
        JumpToEndCommand = new RelayCommand(_ => JumpToEnd());
        CycleSpeedCommand = new RelayCommand(_ => CycleSpeed());

        if (_streamService is not null)
        {
            _streamService.SessionListChanged += OnStreamSessionListChanged;
            _streamService.MemoryObservationReceived += OnMemoryObservationReceived;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>All sessions from the current web host. This is the unfiltered source; bind to <see cref="FilteredSessions"/> for the displayed list.</summary>
    public ObservableCollection<SessionRow> Sessions { get; } = new();

    /// <summary>Plot points derived from the current time-filtered position data.</summary>
    public ObservableCollection<PlotPoint> Points { get; } = new();

    /// <summary>
    /// Filtered view of <see cref="Sessions"/>, updated when
    /// <see cref="SearchText"/> changes. Bound by the session ListBox.
    /// </summary>
    public ObservableCollection<SessionRow> FilteredSessions { get; } = new();

    /// <summary>
    /// Case-insensitive search text for filtering the session list.
    /// Matches against map label. Empty string shows all sessions.
    /// </summary>
    /// <summary>Live player X position from memory observation. Null when unavailable.</summary>
    public double? LivePlayerPositionX
    {
        get => _livePlayerPositionX;
        private set { _livePlayerPositionX = value; OnPropertyChanged(); }
    }

    /// <summary>Live player Z position from memory observation. Null when unavailable.</summary>
    public double? LivePlayerPositionZ
    {
        get => _livePlayerPositionZ;
        private set { _livePlayerPositionZ = value; OnPropertyChanged(); }
    }

    /// <summary>Live player HP from memory observation. Null when unavailable.</summary>
    public int? LivePlayerHP
    {
        get => _livePlayerHP;
        private set { _livePlayerHP = value; OnPropertyChanged(); }
    }

    /// <summary>Live replay time in seconds from memory observation.</summary>
    public double? LiveReplayTimeSeconds
    {
        get => _liveReplayTimeSeconds;
        private set { _liveReplayTimeSeconds = value; OnPropertyChanged(); }
    }

    /// <summary>True when the overlay is receiving live memory observations.</summary>
    public bool HasLiveMemoryObservation
    {
        get => _hasLiveMemoryObservation;
        private set { _hasLiveMemoryObservation = value; OnPropertyChanged(); }
    }

    /// <summary>Live camera yaw in radians from memory observation.</summary>
    public double? LivePlayerYaw
    {
        get => _livePlayerYaw;
        private set { _livePlayerYaw = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Recent live player position trail for velocity trail rendering.
    /// Capped at <see cref="MaxLiveTrailPoints"/> points (FIFO).
    /// </summary>
    public ObservableCollection<PlotPoint> LivePlayerTrail { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value ?? string.Empty;
            OnPropertyChanged();
            ApplySearchFilter();
        }
    }

    public string Status
    {
        get => _status;
        internal set
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

    /// <summary>
    /// The currently selected session row. Setting this triggers an async
    /// detail load via <see cref="RefreshSelectedAsync"/>.
    /// </summary>
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

            StartSelectedSessionRefresh();
        }
    }

    /// <summary>Refreshes the session list from the web host.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Toggle play/pause for the replay timeline scrubber.</summary>
    public ICommand PlayPauseCommand { get; }

    /// <summary>Jump scrubber to the beginning of the timeline.</summary>
    public ICommand JumpToStartCommand { get; }

    /// <summary>Jump scrubber to the end of the timeline.</summary>
    public ICommand JumpToEndCommand { get; }

    /// <summary>Cycle through playback speeds: 0.5x, 1x, 2x, 4x, 8x.</summary>
    public ICommand CycleSpeedCommand { get; }

    /// <summary>Minimum world X coordinate for the current map boundary projection.</summary>
    public double WorldMinX
    {
        get => _worldMinX;
        private set { _worldMinX = value; OnPropertyChanged(); }
    }

    /// <summary>Maximum world X coordinate for the current map boundary projection.</summary>
    public double WorldMaxX
    {
        get => _worldMaxX;
        private set { _worldMaxX = value; OnPropertyChanged(); }
    }

    /// <summary>Minimum world Z coordinate for the current map boundary projection.</summary>
    public double WorldMinZ
    {
        get => _worldMinZ;
        private set { _worldMinZ = value; OnPropertyChanged(); }
    }

    /// <summary>Maximum world Z coordinate for the current map boundary projection.</summary>
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

    /// <summary>
    /// The minimap texture image for the currently selected session's map,
    /// loaded from the web host's minimap endpoint. Null while loading or unavailable.
    /// </summary>
    public ImageSource? MinimapImageSource
    {
        get => _minimapImageSource;
        private set { _minimapImageSource = value; OnPropertyChanged(); }
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

    /// <summary>Projected nameplates for the W2S HUD, one per visible tank.</summary>
    public ObservableCollection<NameplateItem> Nameplates => _nameplates;

    /// <summary>Projected beacons for the W2S HUD, one per visible POI.</summary>
    public ObservableCollection<BeaconItem> Beacons => _beacons;

    /// <summary>Event-feed pips (damage/death) for the W2S HUD.</summary>
    public ObservableCollection<PipItem> Pips => _pips;

    /// <summary>God-view minimap entries (normalized 0..1 panel coordinates)
    /// for the W2S HUD, rebuilt every frame from the map boundary and every
    /// roster tank's nearest position sample.</summary>
    public ObservableCollection<MinimapItem> MinimapItems => _minimapItems;

    /// <summary>Beacons on the minimap panel (normalized 0..1 coordinates),
    /// rebuilt every frame from the frame's visible beacons and the boundary.</summary>
    public ObservableCollection<MinimapBeaconItem> MinimapBeacons => _minimapBeacons;

    /// <summary>Camera world X for the minimap marker; null when the
    /// viewpoint has no position evidence.</summary>
    public double? MinimapCameraX => _minimapCameraX;

    /// <summary>Camera world Z for the minimap marker; null when the
    /// viewpoint has no position evidence.</summary>
    public double? MinimapCameraZ => _minimapCameraZ;

    /// <summary>Camera facing yaw (radians, packet convention: 0 faces +Z,
    /// +π/2 faces +X) for the minimap direction tick; null when the
    /// viewpoint has no rotation evidence.</summary>
    public double? MinimapCameraYawRadians => _minimapCameraYaw;

    /// <summary>Kill feed for the HUD: every destroy landed up to the current
    /// frame, newest first, with names resolved from the frame's roster.</summary>
    public ObservableCollection<KillItem> KillFeed => _killFeed;

    /// <summary>Scoreboard for the HUD: every roster tank's cumulative damage
    /// dealt and kills at the current frame time, sorted by damage dealt
    /// (highest first).</summary>
    public ObservableCollection<ScoreboardItem> Scoreboard => _scoreboard;

    /// <summary>Vertical field of view (degrees) used to project HUD frames.</summary>
    public double HudFovDegrees
    {
        get => _hudFovDegrees;
        set
        {
            if (value == _hudFovDegrees) return;
            _hudFovDegrees = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Replay time (seconds) of the last successfully loaded HUD frame.</summary>
    public double? LastFrameReplayTimeSeconds
    {
        get => _lastFrameReplayTimeSeconds;
        private set
        {
            _lastFrameReplayTimeSeconds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Fetches the overlay frame at the current replay time and refreshes
    /// <see cref="Nameplates"/>. Called by the playback tick; a stale in-flight
    /// request is cancelled so a slow response can never clobber a newer one.
    /// A failed fetch (host down) keeps the previous frame on screen.
    /// </summary>
    public async Task RefreshOverlayFrameAsync(
        double viewportWidth,
        double viewportHeight,
        CancellationToken cancellationToken = default)
    {
        TreaderApiClient? client = _client;
        SessionRow? session = _selectedSession;
        if (client is null || session is null)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _frameLoadGeneration);
        _frameLoadCts?.Cancel();
        _frameLoadCts?.Dispose();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frameLoadCts = cts;
        try
        {
            OverlayFrameResponse? frame = await client.GetOverlayFrameAsync(
                session.BattleSessionId,
                _currentTime.TotalSeconds,
                _hudFovDegrees,
                viewportWidth,
                viewportHeight,
                cts.Token).ConfigureAwait(true);
            if (generation != _frameLoadGeneration || frame is null)
            {
                return;
            }

            LastFrameReplayTimeSeconds = frame.ReplayTimeSeconds;
            _nameplates.Clear();
            _beacons.Clear();
            _pips.Clear();
            _minimapItems.Clear();
            _minimapBeacons.Clear();
            _scoreboard.Clear();
            _minimapCameraX = frame.CameraX;
            _minimapCameraZ = frame.CameraZ;
            _minimapCameraYaw = frame.CameraYawRadians;
            BuildMinimap(frame);
            BuildKillFeed(frame);
            BuildScoreboard(frame);
            // Far-to-near (depth descending): WPF draws later children on top,
            // so nearer tanks' nameplates win when two overlap. Unknown depth
            // sorts last and is never hidden.
            foreach (OverlayTankResponse tank in frame.Tanks
                .OrderByDescending(tank => tank.Depth))
            {
                if (tank.ScreenX is null || tank.ScreenY is null || !tank.InViewport)
                {
                    continue;
                }

                // The player's own tank is the camera: never a nameplate.
                if (tank.DistanceMeters < 1.0)
                {
                    continue;
                }

                _nameplates.Add(new NameplateItem(
                    tank.EntityId,
                    tank.ScreenX.Value,
                    tank.ScreenY.Value,
                    tank.PlayerName ?? tank.TankName ?? $"Tank {tank.EntityId}",
                    tank.TeamNumber,
                    tank.HpFraction,
                    tank.Alive,
                    tank.DistanceMeters,
                    tank.Depth ?? double.MaxValue,
                    tank.ScreenHeadingDegrees,
                    tank.DamageDealt,
                    tank.Kills,
                    tank.MaxHealth,
                    tank.CurrentHealth));
            }

            foreach (OverlayBeaconResponse beacon in frame.Beacons)
            {
                if (beacon.ScreenX is null || beacon.ScreenY is null || !beacon.InViewport)
                {
                    continue;
                }

                _beacons.Add(new BeaconItem(
                    beacon.Name,
                    beacon.ScreenX.Value,
                    beacon.ScreenY.Value,
                    beacon.Color,
                    beacon.DistanceMeters));
            }

            foreach (OverlayPipResponse pip in frame.Pips)
            {
                _pips.Add(new PipItem(
                    pip.EntityId,
                    pip.Kind,
                    pip.Damage,
                    pip.ScreenX,
                    pip.ScreenY));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or cancelled: keep the previous frame.
        }
        catch (HttpRequestException)
        {
            // Host unavailable: keep the previous frame on screen.
        }
        finally
        {
            if (generation == _frameLoadGeneration)
            {
                cts.Dispose();
                _frameLoadCts = null;
            }
        }
    }

    /// <summary>
    /// Rebuilds <see cref="MinimapItems"/> from the frame's tanks and the
    /// session's map boundary, and <see cref="MinimapBeacons"/> from the
    /// frame's visible beacons. God-view: every roster tank with a position
    /// sample appears, dead or alive, in or out of viewport. When the map
    /// boundary is degenerate/absent the panel renders nothing (fail-closed).
    /// </summary>
    private void BuildMinimap(OverlayFrameResponse frame)
    {
        double minX = _worldMinX, maxX = _worldMaxX, minZ = _worldMinZ, maxZ = _worldMaxZ;
        foreach (OverlayTankResponse tank in frame.Tanks)
        {
            (double U, double V)? normalized = MinimapMath.Normalize(
                tank.WorldX, tank.WorldZ, minX, maxX, minZ, maxZ);
            if (normalized is null)
            {
                continue;
            }

            _minimapItems.Add(new MinimapItem(
                tank.EntityId,
                normalized.Value.U,
                normalized.Value.V,
                tank.TeamNumber,
                tank.Alive));
        }

        foreach (OverlayBeaconResponse beacon in frame.Beacons)
        {
            (double U, double V)? normalized = MinimapMath.Normalize(
                beacon.WorldX, beacon.WorldZ, minX, maxX, minZ, maxZ);
            if (normalized is null)
            {
                continue;
            }

            _minimapBeacons.Add(new MinimapBeaconItem(
                beacon.Name,
                beacon.Color,
                normalized.Value.U,
                normalized.Value.V));
        }
    }

    /// <summary>
    /// Rebuilds <see cref="KillFeed"/> from the frame's kill list, newest
    /// first, resolving entity ids to player names from the same frame's
    /// tanks. Environmental kills (no killer) render as "—".
    /// </summary>
    private void BuildKillFeed(OverlayFrameResponse frame)
    {
        _killFeed.Clear();
        Dictionary<long, string> nameByEntity = frame.Tanks
            .Where(tank => tank.PlayerName is not null)
            .GroupBy(tank => tank.EntityId)
            .ToDictionary(group => group.Key, group => group.First().PlayerName!);

        foreach (OverlayKillResponse kill in frame.Kills.OrderByDescending(k => k.ReplayTimeSeconds))
        {
            string victim = nameByEntity.GetValueOrDefault(kill.VictimEntityId)
                ?? $"Tank {kill.VictimEntityId}";
            string killer = kill.KillerEntityId is long killerId
                ? nameByEntity.GetValueOrDefault(killerId) ?? $"Tank {killerId}"
                : "—";
            _killFeed.Add(new KillItem(
                kill.VictimEntityId,
                kill.KillerEntityId,
                victim,
                killer,
                kill.ReplayTimeSeconds));
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Scoreboard"/> from the frame's tanks, sorted by
    /// damage dealt (highest first, then kills, then entity id for a stable
    /// order). Every roster tank that produced a position sample appears;
    /// dead tanks stay listed greyed with their final totals.
    /// </summary>
    private void BuildScoreboard(OverlayFrameResponse frame)
    {
        foreach (OverlayTankResponse tank in frame.Tanks
                     .OrderByDescending(t => t.DamageDealt)
                     .ThenByDescending(t => t.Kills)
                     .ThenBy(t => t.EntityId))
        {
            _scoreboard.Add(new ScoreboardItem(
                tank.EntityId,
                tank.PlayerName ?? $"Tank {tank.EntityId}",
                tank.TeamNumber,
                tank.DamageDealt,
                tank.DamageTaken,
                tank.Kills,
                tank.HpFraction,
                tank.Alive));
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

        Guid? selectionAtRefreshStart = _selectedSession?.BattleSessionId;
        TreaderApiClient? clientAtRefreshStart = _client;
        _isRefreshingSessions = true;
        bool refreshSucceeded;
        try
        {
            refreshSucceeded = await RefreshSessionsCoreAsync(cancellationToken);
        }
        finally
        {
            _isRefreshingSessions = false;
        }

        bool hostChanged = !ReferenceEquals(clientAtRefreshStart, _client);
        Guid? selectionAfterRefresh = _selectedSession?.BattleSessionId;
        if (refreshSucceeded
            && (hostChanged
                || (selectionAfterRefresh.HasValue && selectionAfterRefresh != selectionAtRefreshStart)))
        {
            StartSelectedSessionRefresh();
        }

        // After a successful refresh, connect the stream service so future
        // session list changes arrive via push instead of polling.
        if (refreshSucceeded && _clientBaseUri is not null && _streamService is not null)
        {
            _ = ConnectStreamSafelyAsync(
                _clientBaseUri,
                _locator.Locate().Record?.Capability);
        }
    }

    private async Task<bool> RefreshSessionsCoreAsync(CancellationToken cancellationToken)
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
            return false;
        }

        try
        {
            TreaderApiClient client = GetOrCreateClient(new Uri(rendezvous.Record.BaseUri, UriKind.Absolute));
            SessionPageResponse? page = await client.GetSessionsAsync(0, PageLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Guid? selectedSessionId = _selectedSession?.BattleSessionId;
            Sessions.Clear();
            if (page is not null)
            {
                foreach (SessionSummaryResponse summary in page.Items)
                {
                    BattleSessionResponse? session = summary.Session;
                    if (session is null || !Guid.TryParse(session.BattleSessionId, out Guid battleSessionId))
                    {
                        continue;
                    }

                    Sessions.Add(new SessionRow(
                        battleSessionId,
                        session.MapName ?? session.MapId ?? "unknown",
                        session.MapId,
                        session.BattleTimeUtc,
                        summary.ParticipantCount,
                        summary.PositionCount,
                        summary.DecodeRun.SourceArtifactId));
                }
            }

            ReconcileSelectedSession(selectedSessionId);
            ApplySearchFilter();
            Status = $"{Sessions.Count} session(s)";

            // Fetch map boundaries lazily on first successful session refresh.
            if (!_boundariesFetched)
            {
                _boundariesFetched = true;
                _ = FetchMapBoundariesAsync(client);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            Status = "Host unreachable";
            return false;
        }
    }

    private void StartSelectedSessionRefresh()
    {
        InvalidateDetailLoad();
        // Let RefreshSelectedAsync own and dispose the CTS for a real load. When
        // selection is null, it returns through the no-selection path without
        // leaving a dead CTS behind.
        _ = RefreshSelectedAsync();
    }

    private void InvalidateDetailLoad()
    {
        Interlocked.Increment(ref _detailLoadGeneration);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _detailLoadCts,
            null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void ReconcileSelectedSession(Guid? previousSessionId)
    {
        if (previousSessionId is not Guid sessionId)
        {
            return;
        }

        SessionRow? retainedSession = Sessions.FirstOrDefault(
            row => row.BattleSessionId == sessionId);
        _selectedSession = retainedSession;
        OnPropertyChanged(nameof(SelectedSession));

        if (retainedSession is not null)
        {
            return;
        }

        InvalidateDetailLoad();
        ClearSessionState();
    }

    public async Task RefreshSelectedAsync(CancellationToken cancellationToken = default)
    {
        bool callerOwnsCancellation = cancellationToken != CancellationToken.None;
        SessionRow? selected = SelectedSession;
        TreaderApiClient? client = _client;
        if (selected is null || client is null)
        {
            ClearSessionState();
            return;
        }

        CancellationTokenSource? ownedLoadCts = null;
        if (cancellationToken == CancellationToken.None)
        {
            ownedLoadCts = new CancellationTokenSource();
            CancellationTokenSource? previousLoadCts = Interlocked.Exchange(
                ref _detailLoadCts,
                ownedLoadCts);
            previousLoadCts?.Cancel();
            previousLoadCts?.Dispose();
            cancellationToken = ownedLoadCts.Token;
        }

        long detailLoadGeneration = Interlocked.Increment(ref _detailLoadGeneration);
        try
        {
            SessionDetailResponse? detail = await client.GetSessionDetailAsync(selected.BattleSessionId, cancellationToken);
            if (!IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
            {
                return;
            }

            if (detail is null)
            {
                ClearSessionState();
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

            // Load minimap texture for this map. The same detail-load token
            // prevents a slower request for the previous selection winning.
            await LoadMinimapAsync(
                detail.Session?.MapId,
                selected,
                client,
                detailLoadGeneration,
                cancellationToken);

            // Minimap loading intentionally handles its own transport errors,
            // but cancellation can supersede this detail request while it is
            // awaiting the image. Do not publish status from a stale request.
            if (!IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    CancellationToken.None))
            {
                return;
            }

            if (detail.PositionsTruncated)
            {
                Status = $"showing latest 5000 of {detail.TotalPositionCount} positions";
            }
        }
        catch (OperationCanceledException) when (callerOwnsCancellation && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // A replaced selection owns the current state; an older request
            // must not clear it while unwinding cancellation. A transport
            // timeout is also handled as an unreachable host below.
            if (!cancellationToken.IsCancellationRequested
                && IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    CancellationToken.None))
            {
                Status = "Host unreachable";
                ClearSessionState();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ObjectDisposedException)
        {
            if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    CancellationToken.None))
            {
                Status = "Host unreachable";
                ClearSessionState();
            }
        }
        finally
        {
            if (ownedLoadCts is not null
                && ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _detailLoadCts,
                        null,
                        ownedLoadCts),
                    ownedLoadCts))
            {
                ownedLoadCts.Dispose();
            }
        }
    }

    private bool IsCurrentDetailLoad(
        SessionRow selected,
        TreaderApiClient client,
        long detailLoadGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Volatile.Read(ref _detailLoadGeneration) == detailLoadGeneration
            && ReferenceEquals(_client, client)
            && _selectedSession?.BattleSessionId == selected.BattleSessionId;
    }

    private async Task LoadMinimapAsync(
        string? mapId,
        SessionRow selected,
        TreaderApiClient client,
        long detailLoadGeneration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
            {
                MinimapImageSource = null;
            }
            return;
        }

        // Check cache first.
        lock (_minimapCache)
        {
            if (_minimapCache.TryGetValue(mapId, out ImageSource? cached))
            {
                if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
                {
                    MinimapImageSource = cached;
                }
                return;
            }
        }

        try
        {
            byte[]? pngBytes = await client.GetMinimapPngAsync(mapId, cancellationToken);
            if (!IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
            {
                return;
            }
            if (pngBytes is not null && pngBytes.Length > 0)
            {
                BitmapImage bitmap = new();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                using (System.IO.MemoryStream stream = new(pngBytes))
                {
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                lock (_minimapCache)
                {
                    _minimapCache[mapId] = bitmap;
                }

                if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
                {
                    MinimapImageSource = bitmap;
                }
            }
            else if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    cancellationToken))
            {
                MinimapImageSource = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Selection changed; preserve the newer selection's state. A
            // transport timeout clears only the still-current selection.
            if (!cancellationToken.IsCancellationRequested
                && IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    CancellationToken.None))
            {
                MinimapImageSource = null;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or System.IO.IOException or NotSupportedException)
        {
            if (IsCurrentDetailLoad(
                    selected,
                    client,
                    detailLoadGeneration,
                    CancellationToken.None))
            {
                MinimapImageSource = null;
            }
        }
    }

    /// <summary>
    /// Clears all session-derived state so stale data never lingers on screen
    /// after session deselection, null detail responses, or API errors.
    /// </summary>
    private void ClearSessionState()
    {
        _allPositions = [];
        Points.Clear();
        Participants = [];
        EventCount = 0;
        Events = [];
        MapName = null;
        Duration = TimeSpan.Zero;
        _currentTime = TimeSpan.Zero;
        IsPlaying = false;
        MinimapImageSource = null;
        ApplyMapBoundaries(null);
    }

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private TreaderApiClient GetOrCreateClient(Uri baseUri)
    {
        if (_client is not null && _clientBaseUri == baseUri)
        {
            return _client;
        }

        // Cancel and invalidate any in-flight detail load before swapping
        // clients so an old HttpClient can never publish into the new host.
        InvalidateDetailLoad();

        // The old host's detail data must not remain visible while the new
        // host is being discovered. The selected row can be reconciled after
        // the new host's session list succeeds.
        ClearSessionState();

        TreaderApiClient? oldClient = _client;
        _boundariesFetched = false;
        _mapBoundaries = new Dictionary<string, MapBoundaryResponse>(StringComparer.OrdinalIgnoreCase);
        _client = _apiClientFactory(baseUri, _locator.Locate().Record?.Capability);
        _clientBaseUri = baseUri;
        OnPropertyChanged(nameof(BaseUri));
        oldClient?.Dispose();
        return _client;
    }

    private async Task ConnectStreamSafelyAsync(Uri baseUri, string? capability)
    {
        try
        {
            await _streamService!.ConnectAsync(
                baseUri,
                capability,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // SignalR is an optional push path; polling remains authoritative.
            // Observe startup failures so they cannot become unobserved task
            // exceptions or replace the user-safe status with transport detail.
            System.Diagnostics.Debug.WriteLine(
                $"[TelemetryStream] Connect failed: {exception.GetType().Name}");
        }
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

    private void OnMemoryObservationReceived(object? sender, GameMemoryResponse observation)
    {
        // SignalR callbacks run on non-UI threads. Marshal property
        // mutations to the WPF dispatcher thread.
        if (_syncContext is not null)
        {
            _syncContext.Post(static state =>
            {
                var (vm, obs) = ((MainViewModel, GameMemoryResponse))state!;
                vm.ApplyMemoryObservation(obs);
            }, (this, observation));
        }
        else
        {
            ApplyMemoryObservation(observation);
        }
    }

    private void ApplyMemoryObservation(GameMemoryResponse observation)
    {
        if (observation.Availability != "Available")
        {
            return;
        }

        HasLiveMemoryObservation = true;

        // Reset the liveness timeout: if no observation arrives within
        // LiveObservationTimeout, HasLiveMemoryObservation flips to false
        // so the overlay doesn't display stale "live" status.
        CancelLiveObservationTimeout();
        CancellationTokenSource timeoutCts = new();
        _liveObservationTimeoutCts = timeoutCts;
        _ = Task.Delay(LiveObservationTimeout, timeoutCts.Token)
            .ContinueWith(static (t, state) =>
            {
                if (t.IsCanceled || t.Exception is not null)
                {
                    return;
                }

                var (viewModel, timeoutSource) =
                    ((MainViewModel ViewModel, CancellationTokenSource TimeoutSource))state!;
                void Expire()
                {
                    // A completed timeout can already be queued when the next
                    // observation arrives. Only the current timeout may clear
                    // the live indicator.
                    if (!ReferenceEquals(
                            Interlocked.CompareExchange(
                                ref viewModel._liveObservationTimeoutCts,
                                null,
                                timeoutSource),
                            timeoutSource))
                    {
                        return;
                    }

                    timeoutSource.Dispose();
                    viewModel.HasLiveMemoryObservation = false;
                }

                if (viewModel._syncContext is SynchronizationContext context)
                {
                    context.Post(static state => ((Action)state!).Invoke(), (Action)Expire);
                }
                else
                {
                    Expire();
                }
            }, (this, timeoutCts), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        if (observation.PlayerPositionX.HasValue)
        {
            LivePlayerPositionX = observation.PlayerPositionX.Value;
        }

        if (observation.PlayerPositionZ.HasValue)
        {
            LivePlayerPositionZ = observation.PlayerPositionZ.Value;
        }

        if (observation.PlayerHP.HasValue)
        {
            LivePlayerHP = observation.PlayerHP.Value;
        }

        if (observation.ReplayTimeSeconds.HasValue)
        {
            LiveReplayTimeSeconds = observation.ReplayTimeSeconds.Value;
        }

        if (observation.PlayerYaw.HasValue)
        {
            LivePlayerYaw = observation.PlayerYaw.Value;
        }

        // Append to the velocity trail when we have both position values.
        if (observation.PlayerPositionX.HasValue && observation.PlayerPositionZ.HasValue)
        {
            // Skip duplicate positions (no movement).
            PlotPoint? last = LivePlayerTrail.Count > 0
                ? LivePlayerTrail[^1]
                : null;

            double x = observation.PlayerPositionX.Value;
            double z = observation.PlayerPositionZ.Value;

            if (last is null || last.X != x || last.Y != z)
            {
                // Team number 9 = live player (matches FastPlotRenderer.LivePlayerTeamNumber).
                LivePlayerTrail.Add(new PlotPoint(x, z, TeamNumber: 9));

                // Cap the trail length.
                while (LivePlayerTrail.Count > MaxLiveTrailPoints)
                {
                    LivePlayerTrail.RemoveAt(0);
                }
            }
        }
    }

    private void CancelLiveObservationTimeout()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _liveObservationTimeoutCts, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private async Task FetchMapBoundariesAsync(TreaderApiClient client)
    {
        try
        {
            IReadOnlyList<MapBoundaryResponse> boundaries = await client
                .GetMapBoundariesAsync();

            // A host switch can complete while this request is in flight. Never
            // let the old client's catalogue overwrite the current projection.
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            void Apply()
            {
                if (!ReferenceEquals(_client, client))
                {
                    return;
                }

                _mapBoundaries = new Dictionary<string, MapBoundaryResponse>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (MapBoundaryResponse b in boundaries)
                {
                    _mapBoundaries[b.MapId] = b;
                }

                ApplyMapBoundaries(SelectedSession);
            }

            if (_syncContext is SynchronizationContext context
                && SynchronizationContext.Current != context)
            {
                context.Post(static state => ((Action)state!).Invoke(), (Action)Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException
            or JsonException or ObjectDisposedException)
        {
            // Permit a later refresh to retry an optional catalogue request,
            // but never let an obsolete host reset the current host's state.
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            void MarkRetry()
            {
                if (ReferenceEquals(_client, client))
                {
                    _boundariesFetched = false;
                }
            }

            if (_syncContext is SynchronizationContext context
                && SynchronizationContext.Current != context)
            {
                context.Post(static state => ((Action)state!).Invoke(), (Action)MarkRetry);
            }
            else
            {
                MarkRetry();
            }
        }
        catch
        {
            // This optional fire-and-forget request must never surface an
            // unobserved task fault or expose machine details to the UI.
            void MarkRetry()
            {
                if (ReferenceEquals(_client, client))
                {
                    _boundariesFetched = false;
                }
            }

            if (_syncContext is SynchronizationContext context
                && SynchronizationContext.Current != context)
            {
                context.Post(static state => ((Action)state!).Invoke(), (Action)MarkRetry);
            }
            else
            {
                MarkRetry();
            }
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

    /// <summary>
    /// Filters <see cref="Sessions"/> into <see cref="FilteredSessions"/>
    /// based on <see cref="SearchText"/>. When the filter excludes the
    /// currently selected session, selection is cleared to avoid stale state.
    /// </summary>
    private void ApplySearchFilter()
    {
        string filter = _searchText.Trim();
        FilteredSessions.Clear();

        if (filter.Length == 0)
        {
            foreach (SessionRow session in Sessions)
            {
                FilteredSessions.Add(session);
            }
        }
        else
        {
            foreach (SessionRow session in Sessions)
            {
                if (session.MapLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredSessions.Add(session);
                }
            }
        }

        // If the currently selected session is no longer visible, deselect it
        // so the detail panel and position plot don't show stale data.
        if (_selectedSession is not null)
        {
            bool stillVisible = false;
            foreach (SessionRow row in FilteredSessions)
            {
                if (row.BattleSessionId == _selectedSession.BattleSessionId)
                {
                    stillVisible = true;
                    break;
                }
            }

            if (!stillVisible)
            {
                _selectedSession = null;
                OnPropertyChanged(nameof(SelectedSession));
                InvalidateDetailLoad();
                ClearSessionState();
            }
        }
    }
}
