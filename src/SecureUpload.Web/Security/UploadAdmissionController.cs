using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SecureUpload.Web.Security;

public sealed class UploadAdmissionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaximumConcurrentUploads { get; set; } = 4;
    public int RequestsPerWindow { get; set; } = 100;
    public long BytesPerWindow { get; set; } = 500L * 1024 * 1024;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public long DefenderMonthlyBytesCap { get; set; } = long.MaxValue;
    public long DefenderBytesUsed { get; set; }
    public int MaximumStoreAttempts { get; set; } = 5;
}

public sealed record UploadAdmissionBudget(
    DateTimeOffset Now,
    TimeSpan Window,
    int RequestsPerWindow,
    long BytesPerWindow,
    long DefenderMonthlyBytesCap,
    long DefenderBytesUsed);

public sealed record UploadAdmissionStoreResult(bool IsAcquired, string? Reason, string? ReservationId)
{
    public static UploadAdmissionStoreResult Acquired(string reservationId) =>
        new(true, null, reservationId);

    public static UploadAdmissionStoreResult Rejected(string reason) =>
        new(false, reason, null);
}

public interface IUploadAdmissionStore
{
    Task<UploadAdmissionStoreResult> TryReserveAsync(
        long bytes,
        UploadAdmissionBudget budget,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string reservationId,
        bool uploadCommitted,
        CancellationToken cancellationToken = default);
}

public sealed class AzureTableUploadAdmissionStore(
    TableClient table,
    IOptions<UploadAdmissionOptions> options) : IUploadAdmissionStore
{
    private const string PartitionKey = "upload-admission";
    private const string AggregateRowKey = "budget";
    private readonly int _maximumAttempts = Math.Max(1, options.Value.MaximumStoreAttempts);

    public async Task<UploadAdmissionStoreResult> TryReserveAsync(
        long bytes,
        UploadAdmissionBudget budget,
        CancellationToken cancellationToken = default)
    {
        var reservationId = Guid.NewGuid().ToString("N");
        var reservationRowKey = ReservationRowKey(reservationId);

        for (var attempt = 0; attempt < _maximumAttempts; attempt++)
        {
            var existingReservation = await GetAsync(reservationRowKey, cancellationToken);
            if (existingReservation is not null)
            {
                return UploadAdmissionStoreResult.Acquired(reservationId);
            }

            var current = await GetAsync(AggregateRowKey, cancellationToken);
            var aggregate = current is null
                ? NewAggregate(budget.Now)
                : new TableEntity(current);
            ResetPeriods(aggregate, budget);

            var requests = GetInt64(aggregate, "Requests");
            var windowBytes = GetInt64(aggregate, "WindowBytes");
            var defenderBytes = GetInt64(aggregate, "DefenderBytes");
            if (requests >= budget.RequestsPerWindow)
            {
                return UploadAdmissionStoreResult.Rejected("request-budget");
            }

            if (bytes > budget.BytesPerWindow - windowBytes)
            {
                return UploadAdmissionStoreResult.Rejected("byte-budget");
            }

            if (bytes > budget.DefenderMonthlyBytesCap - budget.DefenderBytesUsed - defenderBytes)
            {
                return UploadAdmissionStoreResult.Rejected("defender-cap");
            }

            aggregate["Requests"] = requests + 1;
            aggregate["WindowBytes"] = windowBytes + bytes;
            aggregate["DefenderBytes"] = defenderBytes + bytes;
            var reservation = new TableEntity(PartitionKey, reservationRowKey)
            {
                ["Bytes"] = bytes,
                ["MonthStarted"] = MonthStart(budget.Now)
            };

            var actions = current is null
                ? new[]
                {
                    new TableTransactionAction(TableTransactionActionType.Add, aggregate),
                    new TableTransactionAction(TableTransactionActionType.Add, reservation)
                }
                : new[]
                {
                    new TableTransactionAction(
                        TableTransactionActionType.UpdateReplace,
                        aggregate,
                        current.ETag),
                    new TableTransactionAction(TableTransactionActionType.Add, reservation)
                };

            try
            {
                await table.SubmitTransactionAsync(actions, cancellationToken);
                return UploadAdmissionStoreResult.Acquired(reservationId);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                if (attempt + 1 == _maximumAttempts)
                {
                    break;
                }
            }
        }

        return UploadAdmissionStoreResult.Rejected("admission-store-unavailable");
    }

    public async Task CompleteAsync(
        string reservationId,
        bool uploadCommitted,
        CancellationToken cancellationToken = default)
    {
        var reservationRowKey = ReservationRowKey(reservationId);
        for (var attempt = 0; attempt < _maximumAttempts; attempt++)
        {
            var reservation = await GetAsync(reservationRowKey, cancellationToken);
            if (reservation is null)
            {
                return;
            }

            var current = await GetAsync(AggregateRowKey, cancellationToken)
                ?? throw new InvalidOperationException("Upload admission aggregate is missing.");
            var aggregate = new TableEntity(current);
            if (!uploadCommitted)
            {
                var bytes = GetInt64(reservation, "Bytes");
                var reservationMonth = reservation.GetDateTimeOffset("MonthStarted");
                var aggregateMonth = aggregate.GetDateTimeOffset("MonthStarted");
                if (reservationMonth == aggregateMonth)
                {
                    aggregate["DefenderBytes"] = Math.Max(
                        0,
                        GetInt64(aggregate, "DefenderBytes") - bytes);
                }
            }

            try
            {
                await table.SubmitTransactionAsync(
                    [
                        new TableTransactionAction(
                            TableTransactionActionType.UpdateReplace,
                            aggregate,
                            current.ETag),
                        new TableTransactionAction(
                            TableTransactionActionType.Delete,
                            reservation,
                            reservation.ETag)
                    ],
                    cancellationToken);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 409 or 412)
            {
                if (attempt + 1 == _maximumAttempts)
                {
                    throw;
                }
            }
        }
    }

    private async Task<TableEntity?> GetAsync(string rowKey, CancellationToken cancellationToken)
    {
        try
        {
            return (await table.GetEntityAsync<TableEntity>(
                PartitionKey,
                rowKey,
                cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    private static TableEntity NewAggregate(DateTimeOffset now) =>
        new(PartitionKey, AggregateRowKey)
        {
            ["WindowStarted"] = now,
            ["MonthStarted"] = MonthStart(now),
            ["Requests"] = 0L,
            ["WindowBytes"] = 0L,
            ["DefenderBytes"] = 0L
        };

    private static void ResetPeriods(TableEntity aggregate, UploadAdmissionBudget budget)
    {
        var windowStarted = aggregate.GetDateTimeOffset("WindowStarted") ?? budget.Now;
        if (budget.Now - windowStarted >= budget.Window)
        {
            aggregate["WindowStarted"] = budget.Now;
            aggregate["Requests"] = 0L;
            aggregate["WindowBytes"] = 0L;
        }

        var monthStarted = aggregate.GetDateTimeOffset("MonthStarted") ?? MonthStart(budget.Now);
        var currentMonth = MonthStart(budget.Now);
        if (monthStarted != currentMonth)
        {
            aggregate["MonthStarted"] = currentMonth;
            aggregate["DefenderBytes"] = 0L;
        }
    }

    private static long GetInt64(TableEntity entity, string property) =>
        entity.TryGetValue(property, out var value) ? Convert.ToInt64(value) : 0;

    private static DateTimeOffset MonthStart(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static string ReservationRowKey(string reservationId) => $"reservation-{reservationId}";
}

public sealed class UploadAdmissionController
{
    private readonly object _gate = new();
    private readonly UploadAdmissionOptions _options;
    private readonly IUploadAdmissionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UploadAdmissionController> _logger;
    private int _active;

    public UploadAdmissionController(
        IOptions<UploadAdmissionOptions> options,
        IUploadAdmissionStore store,
        TimeProvider timeProvider,
        ILogger<UploadAdmissionController> logger)
    {
        _options = options.Value;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public UploadAdmissionController(
        IOptions<UploadAdmissionOptions> options,
        IUploadAdmissionStore store,
        TimeProvider? timeProvider = null)
        : this(
            options,
            store,
            timeProvider ?? TimeProvider.System,
            NullLogger<UploadAdmissionController>.Instance)
    {
    }

    public async Task<UploadAdmissionLease> TryAcquireAsync(
        long? requestBytes,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return UploadAdmissionLease.Rejected("disabled");
        }

        lock (_gate)
        {
            if (_active >= _options.MaximumConcurrentUploads)
            {
                return UploadAdmissionLease.Rejected("concurrency");
            }

            _active++;
        }

        try
        {
            var bytes = Math.Max(0, requestBytes ?? _options.BytesPerWindow);
            UploadAdmissionStoreResult result;
            try
            {
                result = await _store.TryReserveAsync(
                    bytes,
                    new UploadAdmissionBudget(
                        _timeProvider.GetUtcNow(),
                        _options.Window,
                        _options.RequestsPerWindow,
                        _options.BytesPerWindow,
                        _options.DefenderMonthlyBytesCap,
                        _options.DefenderBytesUsed),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Distributed upload admission store is unavailable.");
                result = UploadAdmissionStoreResult.Rejected("admission-store-unavailable");
            }

            if (!result.IsAcquired || result.ReservationId is null)
            {
                ReleaseActive();
                return UploadAdmissionLease.Rejected(result.Reason ?? "admission-store-unavailable");
            }

            return UploadAdmissionLease.Acquired(async (committed, releaseToken) =>
            {
                try
                {
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            await _store.CompleteAsync(result.ReservationId, committed, releaseToken);
                            return;
                        }
                        catch (Exception exception) when (attempt < 3)
                        {
                            _logger.LogWarning(
                                exception,
                                "Upload admission reservation {ReservationId} completion attempt {Attempt} failed.",
                                result.ReservationId,
                                attempt);
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Upload admission reservation {ReservationId} could not be completed after retries; budgets remain conservatively reserved.",
                        result.ReservationId);
                }
                finally
                {
                    ReleaseActive();
                }
            });
        }
        catch
        {
            ReleaseActive();
            throw;
        }
    }

    private void ReleaseActive()
    {
        lock (_gate)
        {
            _active--;
        }
    }
}

public sealed class UploadAdmissionLease : IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, ValueTask>? _release;
    private int _disposed;
    private bool _committed;

    private UploadAdmissionLease(
        bool acquired,
        string? reason,
        Func<bool, CancellationToken, ValueTask>? release)
    {
        IsAcquired = acquired;
        Reason = reason;
        _release = release;
    }

    public bool IsAcquired { get; }
    public string? Reason { get; }

    internal static UploadAdmissionLease Acquired(Func<bool, CancellationToken, ValueTask> release) =>
        new(true, null, release);

    internal static UploadAdmissionLease Rejected(string reason) => new(false, reason, null);

    public void Commit()
    {
        if (IsAcquired)
        {
            _committed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _release is not null)
        {
            await _release(_committed, CancellationToken.None);
        }
    }
}
