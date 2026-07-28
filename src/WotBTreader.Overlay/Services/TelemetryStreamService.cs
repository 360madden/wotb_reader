using Microsoft.AspNetCore.SignalR.Client;

namespace WotBTreader.Overlay.Services;

/// <summary>
/// Connects to the web host's SignalR telemetry hub and surfaces session
/// lifecycle events so the overlay can refresh without polling.
/// Connection failures are silent — the caller must fall back to polling.
/// </summary>
public interface ITelemetryStreamService : IDisposable
{
    /// <summary>Raised when new telemetry arrives that may affect the session list.</summary>
    event EventHandler? SessionListChanged;

    /// <summary>
    /// Opens a connection to the telemetry hub at <c>{baseUri}/api/v1/stream</c>.
    /// Safe to call multiple times; subsequent calls are no-ops if already connected.
    /// </summary>
    Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default);
}

internal sealed class TelemetryStreamService : ITelemetryStreamService
{
    private readonly object _gate = new();
    private HubConnection? _connection;
    private Uri? _connectedUri;
    private CancellationTokenSource? _streamCts;
    private bool _disposed;

    public event EventHandler? SessionListChanged;

    public async Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_connectedUri == baseUri && _connection?.State == HubConnectionState.Connected)
            {
                return;
            }

            _connectedUri = null;
        }

        // Tear down any previous connection outside the lock so SignalR
        // callbacks that acquire _gate (e.g. OnConnectionClosed) don't
        // deadlock with the disposal.
        _ = DisposeConnectionAsync().ContinueWith(static t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TelemetryStream] Previous connection disposal failed: {t.Exception.InnerException?.Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        Uri hubUri = new(baseUri, "/api/v1/stream");
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .WithAutomaticReconnect()
            .Build();

        connection.Closed += OnConnectionClosed;
        connection.Reconnected += OnReconnected;

        await connection.StartAsync(cancellationToken);

        // The hub's SubscribeAsync returns IAsyncEnumerable, making it a
        // server-streaming method. Use StreamAsync to consume it.
        CancellationTokenSource streamCts = new();
        lock (_gate)
        {
            CancelStream();
            _streamCts = streamCts;
            _connection = connection;
            _connectedUri = baseUri;
        }

        _ = ConsumeStreamAsync(connection, streamCts.Token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        CancelStream();
        _ = DisposeConnectionAsync().ContinueWith(static t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TelemetryStream] Dispose connection failed: {t.Exception.InnerException?.Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void CancelStream()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _streamCts;
            _streamCts = null;
        }

        previous?.Cancel();
        previous?.Dispose();
    }

    private async Task ConsumeStreamAsync(
        HubConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TelemetryStreamItem item in connection
                .StreamAsync<TelemetryStreamItem>(
                    "subscribe", null, (long)0, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                // Only refresh on events that change the session list.
                // Heartbeats and gaps carry no new session data.
                if (item.Kind is "event" or "snapshot")
                {
                    SessionListChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on reconnection or disposal.
        }
        catch (Exception)
        {
            // Network drops, serialisation errors, and hub disconnects are
            // all benign here — the caller polls as a fallback, and
            // AutomaticReconnect will restore the connection separately.
        }
    }

    private Task OnConnectionClosed(Exception? exception)
    {
        // The connection closed. AutomaticReconnect will attempt to restore
        // it. Cancel the current stream so OnReconnected can start a new one.
        CancelStream();
        return Task.CompletedTask;
    }

    private Task OnReconnected(string? connectionId)
    {
        // Start a fresh stream subscription after reconnection.
        // Must synchronise with CancelStream and Dispose via _gate to
        // avoid racing on _connection and _streamCts.
        lock (_gate)
        {
            if (_disposed || _connection is null)
            {
                return Task.CompletedTask;
            }

            CancelStream();
            CancellationTokenSource streamCts = new();
            _streamCts = streamCts;
            _ = ConsumeStreamAsync(_connection, streamCts.Token).ContinueWith(static t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TelemetryStream] Reconnected stream failed: {t.Exception.InnerException?.Message}");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        return Task.CompletedTask;
    }

    private async Task DisposeConnectionAsync()
    {
        HubConnection? previous;
        lock (_gate)
        {
            previous = _connection;
            _connection = null;
        }

        if (previous is not null)
        {
            previous.Closed -= OnConnectionClosed;
            previous.Reconnected -= OnReconnected;
            await previous.DisposeAsync();
        }
    }

    /// <summary>
    /// Minimal shape for deserializing stream items from the hub.
    /// Only Kind is inspected; the rest exists so deserialization succeeds.
    /// </summary>
    private sealed record TelemetryStreamItem(
        string SchemaVersion,
        long Sequence,
        string Kind,
        string? SessionId,
        object? Event,
        object? Snapshot,
        DateTimeOffset PublishedAtUtc);
}
