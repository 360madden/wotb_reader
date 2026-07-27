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

    private TreaderApiClient? _client;
    private Uri? _clientBaseUri;
    private CancellationTokenSource? _detailLoadCts;
    private SessionRow? _selectedSession;
    private string _status = string.Empty;
    private bool _isRefreshingSessions;

    public MainViewModel()
        : this(new RendezvousLocator(), static baseUri => new TreaderApiClient(baseUri), null)
    {
    }

    public MainViewModel(
        RendezvousLocator locator,
        Func<Uri, TreaderApiClient> apiClientFactory,
        ITelemetryStreamService? streamService = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        _streamService = streamService;
        RefreshCommand = new RelayCommand(_ => _ = RefreshSessionsAsync());

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
                        summary.Session.BattleTimeUtc,
                        summary.ParticipantCount,
                        summary.PositionCount));
                }
            }

            Status = $"{Sessions.Count} session(s)";
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
            return;
        }

        try
        {
            SessionDetailResponse? detail = await client.GetSessionDetailAsync(selected.BattleSessionId, cancellationToken);
            if (detail is null)
            {
                return;
            }

            Dictionary<string, int> teamByParticipantId = new(StringComparer.Ordinal);
            foreach (ParticipantResponse participant in detail.Participants)
            {
                if (participant.TeamNumber is int team)
                {
                    teamByParticipantId[participant.ParticipantId] = team;
                }
            }

            IReadOnlyList<PositionSampleResponse> positions = detail.Positions;
            int stride = Math.Max(1, positions.Count / MaxPlottedPoints);
            Points.Clear();
            for (int i = 0; i < positions.Count; i += stride)
            {
                PositionSampleResponse sample = positions[i];
                int teamNumber = 0;
                if (sample.ParticipantId is not null)
                {
                    teamByParticipantId.TryGetValue(sample.ParticipantId, out teamNumber);
                }

                Points.Add(new PlotPoint(sample.RawX, sample.RawZ, teamNumber));
            }

            if (detail.PositionsTruncated)
            {
                Status = $"showing latest 5000 of {detail.TotalPositionCount} positions";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            Status = "Host unreachable";
        }
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

        // Cancel any in-flight detail load before swapping clients so the
        // old HttpClient is never used after disposal.
        _detailLoadCts?.Cancel();

        TreaderApiClient? oldClient = _client;
        _client = _apiClientFactory(baseUri);
        _clientBaseUri = baseUri;
        oldClient?.Dispose();
        return _client;
    }

    private void OnStreamSessionListChanged(object? sender, EventArgs e)
    {
        _ = RefreshSessionsAsync();
    }
}
