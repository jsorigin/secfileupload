using SecureUpload.Web.Security;

namespace SecureUpload.Web.Tests;

internal sealed class InMemoryUploadAdmissionStore : IUploadAdmissionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Reservation> _reservations = [];
    private DateTimeOffset? _windowStarted;
    private DateTimeOffset? _monthStarted;
    private long _requests;
    private long _windowBytes;
    private long _defenderBytes;

    public bool FailReservations { get; set; }
    public bool FailCompletion { get; set; }
    public int RemainingCompletionFailures { get; set; }
    public int CompletionAttempts { get; private set; }

    public Task<UploadAdmissionStoreResult> TryReserveAsync(
        long bytes,
        UploadAdmissionBudget budget,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FailReservations)
            {
                throw new InvalidOperationException("admission store unavailable");
            }

            if (_windowStarted is null || budget.Now - _windowStarted >= budget.Window)
            {
                _windowStarted = budget.Now;
                _requests = 0;
                _windowBytes = 0;
            }

            var monthStart = new DateTimeOffset(
                budget.Now.Year,
                budget.Now.Month,
                1,
                0,
                0,
                0,
                TimeSpan.Zero);
            if (_monthStarted != monthStart)
            {
                _monthStarted = monthStart;
                _defenderBytes = 0;
            }

            if (_requests >= budget.RequestsPerWindow)
            {
                return Task.FromResult(UploadAdmissionStoreResult.Rejected("request-budget"));
            }

            if (bytes > budget.BytesPerWindow - _windowBytes)
            {
                return Task.FromResult(UploadAdmissionStoreResult.Rejected("byte-budget"));
            }

            if (bytes > budget.DefenderMonthlyBytesCap -
                budget.DefenderBytesUsed -
                _defenderBytes)
            {
                return Task.FromResult(UploadAdmissionStoreResult.Rejected("defender-cap"));
            }

            var reservationId = Guid.NewGuid().ToString("N");
            _requests++;
            _windowBytes += bytes;
            _defenderBytes += bytes;
            _reservations.Add(reservationId, new Reservation(bytes, monthStart));
            return Task.FromResult(UploadAdmissionStoreResult.Acquired(reservationId));
        }
    }

    public Task CompleteAsync(
        string reservationId,
        bool uploadCommitted,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            CompletionAttempts++;
            if (FailCompletion || RemainingCompletionFailures-- > 0)
            {
                throw new InvalidOperationException("admission completion unavailable");
            }

            if (_reservations.Remove(reservationId, out var reservation) &&
                !uploadCommitted &&
                reservation.MonthStarted == _monthStarted)
            {
                _defenderBytes = Math.Max(0, _defenderBytes - reservation.Bytes);
            }

            return Task.CompletedTask;
        }
    }

    private sealed record Reservation(long Bytes, DateTimeOffset MonthStarted);
}
