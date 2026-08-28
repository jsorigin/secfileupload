using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SecureUpload.Web.Security;

namespace SecureUpload.Web.Tests.Security;

public sealed class RateLimitTests
{
    [Fact]
    public void ForwardedAddressIsUsedOnlyFromTrustedPeer()
    {
        var partitioner = new ClientIpPartitioner(Options.Create(new ForwardedClientIpOptions
        {
            TrustedProxies = [IPAddress.Parse("10.0.0.4")]
        }));
        var trusted = Context("10.0.0.4", "198.51.100.66, 203.0.113.9, 10.0.0.4");
        var untrusted = Context("198.51.100.2", "203.0.113.9");

        Assert.Equal("203.0.113.9", partitioner.GetPartition(trusted));
        Assert.Equal("198.51.100.2", partitioner.GetPartition(untrusted));
    }

    [Fact]
    public async Task AdmissionRejectsKillSwitchBudgetsAndConcurrencyWithoutQueueing()
    {
        var store = new InMemoryUploadAdmissionStore();
        var controller = new UploadAdmissionController(Options.Create(new UploadAdmissionOptions
        {
            Enabled = true,
            MaximumConcurrentUploads = 1,
            RequestsPerWindow = 1,
            BytesPerWindow = 10,
            Window = TimeSpan.FromMinutes(1)
        }), store);

        await using var first = await controller.TryAcquireAsync(10);
        await using var concurrent = await controller.TryAcquireAsync(1);

        Assert.True(first.IsAcquired);
        Assert.False(concurrent.IsAcquired);
        Assert.Equal("concurrency", concurrent.Reason);
        await first.DisposeAsync();
        await using var budget = await controller.TryAcquireAsync(1);
        Assert.False(budget.IsAcquired);
        Assert.Equal("request-budget", budget.Reason);
    }

    [Fact]
    public async Task DisabledAdmissionIsKillSwitch()
    {
        var controller = new UploadAdmissionController(Options.Create(new UploadAdmissionOptions
        {
            Enabled = false
        }), new InMemoryUploadAdmissionStore());

        await using var lease = await controller.TryAcquireAsync(1);

        Assert.False(lease.IsAcquired);
        Assert.Equal("disabled", lease.Reason);
    }

    [Fact]
    public async Task AdmissionRejectsRequestThatExceedsRemainingByteBudget()
    {
        var store = new InMemoryUploadAdmissionStore();
        var controller = new UploadAdmissionController(Options.Create(new UploadAdmissionOptions
        {
            MaximumConcurrentUploads = 2,
            RequestsPerWindow = 10,
            BytesPerWindow = 10
        }), store);

        await using var first = await controller.TryAcquireAsync(6);
        await first.DisposeAsync();
        await using var rejected = await controller.TryAcquireAsync(5);

        Assert.False(rejected.IsAcquired);
        Assert.Equal("byte-budget", rejected.Reason);
    }

    [Fact]
    public async Task DefenderCapAccountsAcrossControllersSharingTheStore()
    {
        var store = new InMemoryUploadAdmissionStore();
        var options = Options.Create(new UploadAdmissionOptions
        {
            Enabled = true,
            MaximumConcurrentUploads = 2,
            RequestsPerWindow = 10,
            BytesPerWindow = 100,
            DefenderMonthlyBytesCap = 10,
            DefenderBytesUsed = 4
        });
        var firstController = new UploadAdmissionController(options, store);
        var secondController = new UploadAdmissionController(options, store);

        await using var first = await firstController.TryAcquireAsync(6);
        await using var second = await secondController.TryAcquireAsync(1);

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);
        Assert.Equal("defender-cap", second.Reason);
    }

    [Fact]
    public async Task RequestAndByteBudgetsAreEnforcedAcrossControllers()
    {
        var store = new InMemoryUploadAdmissionStore();
        var options = Options.Create(new UploadAdmissionOptions
        {
            MaximumConcurrentUploads = 2,
            RequestsPerWindow = 2,
            BytesPerWindow = 10,
            DefenderMonthlyBytesCap = 100
        });
        var firstController = new UploadAdmissionController(options, store);
        var secondController = new UploadAdmissionController(options, store);

        await using var first = await firstController.TryAcquireAsync(6);
        first.Commit();
        await first.DisposeAsync();
        await using var byteRejected = await secondController.TryAcquireAsync(5);
        await using var second = await secondController.TryAcquireAsync(4);
        second.Commit();
        await second.DisposeAsync();
        await using var requestRejected = await firstController.TryAcquireAsync(0);

        Assert.True(first.IsAcquired);
        Assert.False(byteRejected.IsAcquired);
        Assert.Equal("byte-budget", byteRejected.Reason);
        Assert.True(second.IsAcquired);
        Assert.False(requestRejected.IsAcquired);
        Assert.Equal("request-budget", requestRejected.Reason);
    }

    [Fact]
    public async Task FailedUploadReleasesOnlyDefenderReservation()
    {
        var store = new InMemoryUploadAdmissionStore();
        var options = Options.Create(new UploadAdmissionOptions
        {
            MaximumConcurrentUploads = 2,
            RequestsPerWindow = 2,
            BytesPerWindow = 10,
            DefenderMonthlyBytesCap = 6
        });
        var controller = new UploadAdmissionController(options, store);

        await using var failed = await controller.TryAcquireAsync(6);
        await failed.DisposeAsync();
        await using var retry = await controller.TryAcquireAsync(4);

        Assert.True(retry.IsAcquired);
        await retry.DisposeAsync();
        await using var requestRejected = await controller.TryAcquireAsync(1);
        Assert.False(requestRejected.IsAcquired);
        Assert.Equal("request-budget", requestRejected.Reason);
    }

    [Fact]
    public async Task StoreFailureFailsAdmissionClosed()
    {
        var store = new InMemoryUploadAdmissionStore { FailReservations = true };
        var controller = new UploadAdmissionController(
            Options.Create(new UploadAdmissionOptions()),
            store);

        await using var lease = await controller.TryAcquireAsync(1);

        Assert.False(lease.IsAcquired);
        Assert.Equal("admission-store-unavailable", lease.Reason);
    }

    [Fact]
    public async Task CompletionRetriesBeforeReleasingLocalConcurrency()
    {
        var store = new InMemoryUploadAdmissionStore { RemainingCompletionFailures = 2 };
        var controller = new UploadAdmissionController(
            Options.Create(new UploadAdmissionOptions
            {
                MaximumConcurrentUploads = 1
            }),
            store);

        await using var lease = await controller.TryAcquireAsync(1);
        await lease.DisposeAsync();
        await using var next = await controller.TryAcquireAsync(1);

        Assert.Equal(3, store.CompletionAttempts);
        Assert.True(next.IsAcquired);
    }

    [Fact]
    public async Task PriorMonthFailureDoesNotReleaseCurrentMonthDefenderBytes()
    {
        var store = new InMemoryUploadAdmissionStore();
        var january = new DateTimeOffset(2026, 1, 31, 23, 59, 0, TimeSpan.Zero);
        var february = january.AddMinutes(2);
        var limits = new UploadAdmissionBudget(
            january,
            TimeSpan.FromMinutes(1),
            100,
            100,
            6,
            0);

        var oldReservation = await store.TryReserveAsync(6, limits);
        var currentReservation = await store.TryReserveAsync(6, limits with { Now = february });
        await store.CompleteAsync(oldReservation.ReservationId!, uploadCommitted: false);
        var extra = await store.TryReserveAsync(1, limits with { Now = february });

        Assert.True(oldReservation.IsAcquired);
        Assert.True(currentReservation.IsAcquired);
        Assert.False(extra.IsAcquired);
        Assert.Equal("defender-cap", extra.Reason);
    }

    private static DefaultHttpContext Context(string peer, string forwarded)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = forwarded;
        return context;
    }
}
