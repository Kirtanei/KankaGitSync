using System.Diagnostics;

namespace KankaGitSync.Presentation;

public readonly record struct FetchProgress(int Completed, int Total)
{
    public int Percentage => Total == 0 ? 0 : Completed * 100 / Total;
}

public sealed class FetchProgressWriter(TextWriter output)
{
    private const int BarWidth = 20;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public Task BeginAsync() => output.WriteAsync("Discovering Kanka resources...\r");

    public Task ReportAsync(FetchProgress progress)
    {
        var completed = Math.Clamp(progress.Completed, 0, progress.Total);
        var filled = progress.Total == 0 ? 0 : completed * BarWidth / progress.Total;
        var bar = new string('#', filled) + new string('-', BarWidth - filled);
        var elapsed = FormatDuration(stopwatch.Elapsed);
        var remaining = completed == 0 ? "calculating" : FormatDuration(TimeSpan.FromSeconds(
            stopwatch.Elapsed.TotalSeconds * (progress.Total - completed) / completed));
        return output.WriteAsync($"Fetching Kanka [{bar}] {completed}/{progress.Total} ({progress.Percentage}%) elapsed {elapsed}, remaining {remaining}\r");
    }

    public Task CompleteAsync() => output.WriteLineAsync();

    private static string FormatDuration(TimeSpan duration) => duration < TimeSpan.FromMinutes(1)
        ? $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))}s"
        : $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s";
}
