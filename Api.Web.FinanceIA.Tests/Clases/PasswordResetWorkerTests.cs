// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The worker that drains the password-reset queue: each item runs in its own scope, and a failure
/// in one never escapes to stop the loop (the caller already got its 202).
/// </summary>
public sealed class PasswordResetWorkerTests
{
    [Fact]
    public async Task ProcessOne_RunsTheServiceInAScope()
    {
        var service = new RecordingService();
        var worker  = NewWorker(service);

        await worker.ProcessOneAsync(Item(), CancellationToken.None);

        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task ProcessOne_SwallowsAFailure()
    {
        var worker = NewWorker(new RecordingService { Throw = new InvalidOperationException("boom") });

        await worker.ProcessOneAsync(Item(), CancellationToken.None);   // must not throw
    }

    [Fact]
    public async Task QueuedItem_IsDrainedByTheLoop()
    {
        var service = new RecordingService();
        var queue   = new PasswordResetQueue(Options.Create(new PasswordResetOptions()));
        var worker  = NewWorker(service, queue);

        Assert.True(queue.TryEnqueue(Item()));
        await worker.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100 && service.CallCount == 0; i++)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, service.CallCount);
    }

    private static PasswordResetWorkItem Item() => new(
        new TenantContext
        {
            TenantId               = "TenantA",
            FirebaseProjectId      = "project-a",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        },
        "analyst@example.com",
        null,
        null);

    private static PasswordResetWorker NewWorker(RecordingService service, IPasswordResetQueue? queue = null)
    {
        var provider = new ServiceCollection()
            .AddScoped<IPasswordResetService>(_ => service)
            .BuildServiceProvider();

        return new PasswordResetWorker(
            queue ?? new PasswordResetQueue(Options.Create(new PasswordResetOptions())),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PasswordResetWorker>.Instance);
    }

    private sealed class RecordingService : IPasswordResetService
    {
        private int _callCount;

        public Exception? Throw { get; set; }
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<PasswordResetOutcome> ProcessAsync(PasswordResetWorkItem item, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            if (Throw is not null) throw Throw;
            return Task.FromResult(PasswordResetOutcome.Sent);
        }
    }
}
