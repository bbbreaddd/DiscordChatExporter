using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Utils;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Utils.Extensions;

internal static class ConsoleExtensions
{
    // Shared between ToExportProgress and StartTaskAsync via ProgressTask.State, so that
    // time spent waiting out a rate limit doesn't get counted as part of the fetch rate below.
    private const string ThrottledDurationStateKey = "ThrottledDuration";

    extension(IConsole console)
    {
        public IAnsiConsole CreateAnsiConsole() =>
            AnsiConsole.Create(
                new AnsiConsoleSettings
                {
                    Ansi = AnsiSupport.Detect,
                    ColorSystem = ColorSystemSupport.Detect,
                    Out = new AnsiConsoleOutput(console.Output),
                }
            );

        public Status CreateStatusTicker() =>
            console.CreateAnsiConsole().Status().AutoRefresh(true);

        public Progress CreateProgressTicker() =>
            console
                .CreateAnsiConsole()
                .Progress()
                .AutoClear(false)
                .AutoRefresh(true)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn(),
                    new PercentageColumn()
                );
    }

    public static IProgress<ExportProgress> ToExportProgress(
        this ProgressTask progressTask,
        string baseDescription
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var messageCount = 0;

        return new Progress<ExportProgress>(p =>
        {
            progressTask.Value = p.Percentage.Fraction;
            if (p.CurrentTimestamp is { } ts)
            {
                messageCount++;

                // Messages arrive in bursts (one HTTP page at a time), so the rate between
                // two individual reports is meaningless. Averaging over the task's whole
                // lifetime smooths that out into a representative messages/sec figure. Time
                // spent waiting out a rate limit is excluded, since that's dead time, not slow
                // fetching -- otherwise a single big pause would tank the average for good.
                var throttledDuration = progressTask.State.Get<TimeSpan>(ThrottledDurationStateKey);
                var activeElapsed = stopwatch.Elapsed - throttledDuration;
                var rate = messageCount / Math.Max(activeElapsed.TotalSeconds, 0.001);

                progressTask.Description = $"{baseDescription} ({ts:yyyy-MM-dd}, {rate:F1} msg/s)";
            }
        });
    }

    public static async ValueTask StartTaskAsync(
        this ProgressContext context,
        string description,
        Func<ProgressTask, ValueTask> performOperationAsync
    )
    {
        // Description cannot be empty
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/1133
        var actualDescription = !string.IsNullOrWhiteSpace(description) ? description : "...";

        var progressTask = context.AddTask(
            actualDescription,
            new ProgressTaskSettings { MaxValue = 1 }
        );

        // While this task's request is being delayed (rate limited or retried), reflect that
        // in its description so a long pause doesn't look indistinguishable from a hang.
        // The very next real progress update (see ToExportProgress) overwrites this again,
        // so there's nothing to restore once the request goes through.
        void OnThrottled(TimeSpan delay, string reason)
        {
            progressTask.State.Update<TimeSpan>(ThrottledDurationStateKey, d => d + delay);
            progressTask.Description =
                $"{actualDescription} ({reason}, waiting ~{Math.Ceiling(delay.TotalSeconds)}s)";
        }

        using (Http.OnThrottled(OnThrottled))
        {
            try
            {
                await performOperationAsync(progressTask);
            }
            finally
            {
                progressTask.Value = progressTask.MaxValue;
                progressTask.StopTask();
            }
        }
    }
}
