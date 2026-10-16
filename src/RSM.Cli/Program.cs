using RSM.Cli;
using RSM.Safety.Files;

var options = CliOptions.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // stop gracefully: the live view is closed and history is flushed
    cts.Cancel();
};

var dataFolder = DataFolder.CreateDefault(AppContext.BaseDirectory, options.Portable);
return await new CliRunner(dataFolder, Console.Out).RunAsync(options, cts.Token);
