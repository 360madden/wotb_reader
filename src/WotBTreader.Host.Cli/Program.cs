using WotBTreader.Host.Cli.Cli;

using CancellationTokenSource cancellation = new();

// Ctrl+C cancels the in-flight command cooperatively so storage shutdown and
// the error envelope still run instead of the process being torn down.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await CliEntryPoint.RunAsync(
    args,
    Console.Out,
    Console.Error,
    cancellation.Token);
