using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Commands.Shared;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class CronSchedulerSpecs
{
    [Fact]
    public void A_scheduler_with_no_jobs_reports_no_work()
    {
        // Arrange
        var scheduler = new CronScheduler([], TimeZoneInfo.Utc, _ => { });

        // Assert
        scheduler.HasJobs.Should().BeFalse();
    }

    [Fact]
    public async Task A_scheduler_with_no_jobs_returns_immediately()
    {
        // Arrange
        var scheduler = new CronScheduler([], TimeZoneInfo.Utc, _ => { });

        // Act
        var run = scheduler.RunAsync(CancellationToken.None);

        // Assert -- completes without needing cancellation.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        run.IsCompletedSuccessfully.Should().BeTrue();
    }

    // A single timing test covers both firing on schedule and resilience to a throwing job (an
    // every-minute cron guarantees a fire within 60s). Kept as one test so the suite waits out only
    // one cron minute, not two.
    [Fact]
    public async Task A_job_fires_on_schedule_and_a_throw_does_not_stop_the_scheduler()
    {
        // Arrange -- the job throws every time it runs.
        var runs = 0;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var jobs = new List<CronScheduler.Job>
        {
            new(
                "flaky",
                "* * * * *",
                _ =>
                {
                    Interlocked.Increment(ref runs);
                    fired.TrySetResult();
                    throw new InvalidOperationException("boom");
                }
            ),
        };
        var errors = new List<string>();
        var scheduler = new CronScheduler(
            jobs,
            TimeZoneInfo.Utc,
            msg =>
            {
                lock (errors)
                    errors.Add(msg);
            }
        );

        // Act
        var run = Task.Run(() => scheduler.RunAsync(cts.Token));
        var didFire = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(75)));

        // Assert -- it fired within a cron minute, and the scheduler survived the throw.
        didFire.Should().BeSameAs(fired.Task, "the job should fire within one cron minute");
        Volatile.Read(ref runs).Should().BeGreaterThanOrEqualTo(1);
        run.IsCompleted.Should().BeFalse("a throwing job must not stop the scheduler");
        lock (errors)
            errors.Should().Contain(m => m.Contains("flaky") && m.Contains("boom"));

        // Cleanup
        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException) { }
    }
}
