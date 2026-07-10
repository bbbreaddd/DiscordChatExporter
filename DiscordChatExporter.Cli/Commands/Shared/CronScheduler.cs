using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;

namespace DiscordChatExporter.Cli.Commands.Shared;

// Runs named jobs on cron schedules until cancelled. Schedules are evaluated in the supplied time zone
// (the container's local zone, honoring TZ). Jobs run sequentially -- a long job simply delays the
// next -- and a job that throws is logged and skipped, never stopping the scheduler. Jobs due at the
// same time all fire in one pass, so a shared trigger doesn't drop any of them.
public sealed class CronScheduler
{
    public sealed record Job(string Name, string Cron, Func<CancellationToken, Task> RunAsync);

    private sealed class JobState
    {
        public required string Name { get; init; }
        public required CronExpression Expression { get; init; }
        public required Func<CancellationToken, Task> RunAsync { get; init; }
        public DateTimeOffset? Next { get; set; }
    }

    private readonly IReadOnlyList<Job> _jobs;
    private readonly TimeZoneInfo _timeZone;
    private readonly Action<string> _log;

    public CronScheduler(IReadOnlyList<Job> jobs, TimeZoneInfo timeZone, Action<string> log)
    {
        _jobs = jobs;
        _timeZone = timeZone;
        _log = log;
    }

    public bool HasJobs => _jobs.Count > 0;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_jobs.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var states = _jobs
            .Select(j => new JobState
            {
                Name = j.Name,
                Expression = CronExpression.Parse(j.Cron),
                RunAsync = j.RunAsync,
                Next = null,
            })
            .ToList();
        foreach (var state in states)
            state.Next = state.Expression.GetNextOccurrence(now, _timeZone);

        while (!cancellationToken.IsCancellationRequested)
        {
            var earliest = states
                .Where(s => s.Next is not null)
                .OrderBy(s => s.Next!.Value)
                .FirstOrDefault();
            if (earliest is null)
                return; // No job has a future occurrence.

            var wait = earliest.Next!.Value - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            now = DateTimeOffset.UtcNow;
            var due = states
                .Where(s => s.Next is not null && s.Next.Value <= now)
                .OrderBy(s => s.Next!.Value)
                .ToList();

            foreach (var state in due)
            {
                try
                {
                    await state.RunAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Job '{state.Name}' failed: {ex.Message}");
                }

                // Recompute from the current time so a job that overran its own next slot doesn't
                // busy-fire, and so schedule advances even if the run took a while.
                state.Next = state.Expression.GetNextOccurrence(DateTimeOffset.UtcNow, _timeZone);
            }
        }
    }
}
