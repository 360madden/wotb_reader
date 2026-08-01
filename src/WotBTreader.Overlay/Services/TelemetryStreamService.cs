using Microsoft.AspNetCore.SignalR.Client;
using WotBTreader.ApiContracts;

namespace WotBTreader.Overlay.Services;

/// <summary>
/// Connects to the web host's SignalR telemetry hub and surfaces session
/// lifecycle events so the overlay can refresh without polling.
/// Connection failures are silent — the caller must fall back to polling.
/// </summary>
public interface ITelemetryStreamService : IDisposable, IAsyncDisposable
{
    /// <summary>Raised when new telemetry arrives that may affect the session list.</summary>
    event EventHandler? SessionListChanged;

    /// <summary>Raised when a live memory observation is pushed from the host.</summary>
    event EventHandler<GameMemoryResponse>? MemoryObservationReceived;

    /// <summary>
    /// Opens a connection to the telemetry hub at <c>{baseUri}/api/v1/stream</c>.
    /// Safe to call multiple times; subsequent calls are no-ops if already connected.
    /// </summary>
    /// <param name="baseUri">The web host loopback base URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a connection to the telemetry hub at <c>{baseUri}/api/v1/stream</c>.
    /// The optional capability token is sent in the negotiation headers to satisfy
    /// the mutation-protection middleware.
    /// Safe to call multiple times; subsequent calls are no-ops if already connected.
    /// </summary>
    /// <param name="baseUri">The web host loopback base URI.</param>
    /// <param name="capability">Optional loopback capability token from the rendezvous record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(Uri baseUri, string? capability, CancellationToken cancellationToken = default);
}

internal sealed class TelemetryStreamService : ITelemetryStreamService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private HubConnection? _connection;
    private Uri? _connectedUri;
    private CancellationTokenSource? _streamCts;
    private readonly Dictionary<HubConnection, ConnectionHandlers> _connectionHandlers = [];
    private CancellationTokenSource? _connectOperationCts;
    private Task? _disposeTask;
    private bool _disposed;

    public event EventHandler? SessionListChanged;
    public event EventHandler<GameMemoryResponse>? MemoryObservationReceived;

    public Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        return ConnectAsync(baseUri, capability: null, cancellationToken);
    }

    public async Task ConnectAsync(Uri baseUri, string? capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? connectOperationCts = null;
        try
        {
            HubConnection? previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Own a cancellation source for the whole connection attempt.
                // Disposal can cancel negotiation even when the caller supplied
                // CancellationToken.None, so DisposeAsync cannot wait forever on
                // a stalled transport handshake.
                connectOperationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _connectOperationCts = connectOperationCts;
                cancellationToken = connectOperationCts.Token;

                if (_connectedUri == baseUri && _connection?.State == HubConnectionState.Connected)
                {
                    return;
                }

                _connectedUri = null;
                previous = _connection;
                _connection = null;
            }

            CancelStream();
            await DisposeConnectionAsync(previous).ConfigureAwait(false);

            Uri hubUri = new(baseUri, "/api/v1/stream");
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(hubUri, options =>
                {
                    if (!string.IsNullOrEmpty(capability))
                    {
                        options.Headers.Add("X-WotBTreader-Capability", capability);
                    }
                })
                .WithAutomaticReconnect()
                .Build();

            // Capture the owning connection in each callback. A callback from
            // an old connection can arrive after replacement; identity checks
            // below must not cancel or restart the current stream.
            Func<Exception?, Task> closedHandler = exception => OnConnectionClosed(connection, exception);
            Func<string?, Task> reconnectedHandler = connectionId => OnReconnected(connection, connectionId);
            connection.Closed += closedHandler;
            connection.Reconnected += reconnectedHandler;
            connection.On<GameMemoryResponse>("MemoryObservation", OnMemoryObservation);

            bool ownsConnection;
            lock (_gate)
            {
                ownsConnection = !_disposed;
                if (ownsConnection)
                {
                    _connectionHandlers[connection] = new ConnectionHandlers(
                        closedHandler,
                        reconnectedHandler);
                    // Publish the in-flight connection before StartAsync. Dispose
                    // can therefore close it if teardown wins during negotiation.
                    _connection = connection;
                }
            }

            if (!ownsConnection)
            {
                // No dictionary entry exists when disposal won before publish;
                // detach the local delegates before releasing the connection.
                connection.Closed -= closedHandler;
                connection.Reconnected -= reconnectedHandler;
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                return;
            }

            try
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                bool disposeHere;
                lock (_gate)
                {
                    disposeHere = !_disposed && ReferenceEquals(_connection, connection);
                    if (disposeHere)
                    {
                        _connection = null;
                        _connectedUri = null;
                    }
                }

                // If Dispose won the race, its serialized cleanup owns the
                // connection. Otherwise this failed start owns cleanup.
                if (disposeHere)
                {
                    await DisposeConnectionAsync(connection).ConfigureAwait(false);
                }

                throw;
            }

            CancellationTokenSource? previousStream;
            CancellationTokenSource streamCts = new();
            bool accepted;
            lock (_gate)
            {
                accepted = !_disposed && ReferenceEquals(_connection, connection);
                if (accepted)
                {
                    _connectedUri = baseUri;
                    previousStream = _streamCts;
                    _streamCts = streamCts;
                }
                else
                {
                    previousStream = null;
                }
            }

            if (!accepted)
            {
                streamCts.Dispose();
                bool disposeHere;
                lock (_gate)
                {
                    // DisposeCoreAsync owns a connection it clears while the
                    // start is in flight; do not double-dispose it here.
                    disposeHere = !_disposed && ReferenceEquals(_connection, connection);
                    if (disposeHere)
                    {
                        _connection = null;
                        _connectedUri = null;
                    }
                }

                if (disposeHere)
                {
                    await DisposeConnectionAsync(connection).ConfigureAwait(false);
                }

                return;
            }

            previousStream?.Cancel();
            previousStream?.Dispose();
            _ = ConsumeStreamAsync(connection, streamCts.Token);
        }
        finally
        {
            if (connectOperationCts is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connectOperationCts, connectOperationCts))
                    {
                        _connectOperationCts = null;
                    }
                }

                connectOperationCts.Dispose();
            }

            _connectGate.Release();
        }
    }

    public void Dispose()
    {
        GetDisposeTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() => new(GetDisposeTask());

    private Task GetDisposeTask()
    {
        TaskCompletionSource<object?>? completion = null;
        CancellationTokenSource? connectOperationCts;
        Task task;
        bool startCleanup = false;
        lock (_gate)
        {
            _disposed = true;
            connectOperationCts = _connectOperationCts;
            if (_disposeTask is null)
            {
                completion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                startCleanup = true;
            }

            task = _disposeTask;
        }

        // Cancel outside _gate: cancellation callbacks are allowed to run user
        // or transport code and must never be able to deadlock lifecycle state.
        // The connect attempt may finish and dispose its CTS between the lock
        // release and this call, so treat that race as already-cancelled.
        try
        {
            connectOperationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection attempt completed its cleanup concurrently.
        }

        if (startCleanup)
        {
            _ = CompleteDisposeAsync(completion!);
        }

        return task;
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            HubConnection? connection;
            lock (_gate)
            {
                _connectedUri = null;
                connection = _connection;
                _connection = null;
            }

            CancelStream();
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void CancelStream(HubConnection? owner = null)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (owner is not null && !ReferenceEquals(_connection, owner))
            {
                return;
            }

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
        catch (Exception ex)
        {
            // Network drops, serialisation errors, and hub disconnects are
            // all benign here — the caller polls as a fallback, and
            // AutomaticReconnect will restore the connection separately.
            System.Diagnostics.Debug.WriteLine(
                $"[TelemetryStream] Stream consume failed: {ex.GetType().Name}");
        }
    }

    private Task OnConnectionClosed(HubConnection closedConnection, Exception? exception)
    {
        // The connection closed. AutomaticReconnect will attempt to restore
        // it. An old connection must not cancel the replacement stream.
        CancelStream(closedConnection);
        return Task.CompletedTask;
    }

    private Task OnReconnected(HubConnection reconnectedConnection, string? connectionId)
    {
        // Start a fresh stream subscription after reconnection. Never call
        // CancelStream while holding _gate: CancelStream takes the same lock.
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_connection, reconnectedConnection))
            {
                return Task.CompletedTask;
            }
        }

        CancelStream(reconnectedConnection);
        CancellationTokenSource streamCts = new();
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_connection, reconnectedConnection))
            {
                streamCts.Dispose();
                return Task.CompletedTask;
            }

            _streamCts = streamCts;
        }

        _ = ConsumeStreamAsync(reconnectedConnection, streamCts.Token).ContinueWith(static t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TelemetryStream] Reconnected stream failed: {t.Exception.InnerException?.Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }

    private void OnMemoryObservation(GameMemoryResponse observation)
    {
        MemoryObservationReceived?.Invoke(this, observation);
    }

    private async Task DisposeConnectionAsync(HubConnection? previous)
    {
        if (previous is null)
        {
            return;
        }

        ConnectionHandlers? handlers;
        lock (_gate)
        {
            _connectionHandlers.Remove(previous, out handlers);
        }

        if (handlers is not null)
        {
            previous.Closed -= handlers.Closed;
            previous.Reconnected -= handlers.Reconnected;
        }

        await previous.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record ConnectionHandlers(
        Func<Exception?, Task> Closed,
        Func<string?, Task> Reconnected);

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
