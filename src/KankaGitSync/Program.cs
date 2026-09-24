using KankaGitSync;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};
return await CommandLine.RunAsync(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error, cancellation.Token);
