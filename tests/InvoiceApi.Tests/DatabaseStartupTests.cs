using System.Net.Sockets;
using FluentAssertions;
using InvoiceApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace InvoiceApi.Tests;

public class DatabaseStartupTests
{
    // What Npgsql throws while Postgres is still booting / not reachable yet
    private static NpgsqlException NotReachable() => new("Failed to connect", new SocketException());

    private static Task Run(Func<Task> migrate, int maxAttempts = 5) =>
        DatabaseStartup.MigrateWithRetryAsync(migrate, NullLogger.Instance, maxAttempts, TimeSpan.Zero);

    [Fact]
    public async Task ShouldRetry_UntilTheDatabaseIsReachable()
    {
        var calls = 0;

        await Run(() => ++calls < 3 ? throw NotReachable() : Task.CompletedTask);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task ShouldGiveUp_AfterTheLastAttempt()
    {
        var calls = 0;

        var act = () => Run(() => { calls++; throw NotReachable(); }, maxAttempts: 4);

        await act.Should().ThrowAsync<NpgsqlException>();
        calls.Should().Be(4);
    }

    [Fact]
    public async Task ShouldNotRetry_ErrorsThatWaitingCannotFix()
    {
        // e.g. a broken migration or wrong credentials — fail the boot at once
        var calls = 0;

        var act = () => Run(() => { calls++; throw new InvalidOperationException("broken migration"); });

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
    }
}
