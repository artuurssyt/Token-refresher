using LocaltsAccountManager.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  LocaltsAccountManager.Phase0 --file <path> [--output <report.md>]");
    Console.WriteLine("  LocaltsAccountManager.Phase0 --line \"username:token\" [--output <report.md>]");
    return 1;
}

var services = new ServiceCollection();
services.AddLocaltsAccountManagerInfrastructure();
await using var provider = services.BuildServiceProvider();
await DependencyInjection.InitializeInfrastructureAsync(provider);

var investigator = provider.GetRequiredService<LocaltsAccountManager.Core.Interfaces.IPhase0Investigator>();
var output = GetArg(args, "--output") ??
             Path.Combine(
                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 "LocaltsAccountManager",
                 "phase0_report.md");

try
{
    string reportPath;
    var file = GetArg(args, "--file");
    var line = GetArg(args, "--line");
    if (!string.IsNullOrWhiteSpace(file))
    {
        reportPath = await investigator.InvestigateFileAsync(file, output).ConfigureAwait(false);
    }
    else if (!string.IsNullOrWhiteSpace(line))
    {
        reportPath = await investigator.InvestigateLineAsync(line, output).ConfigureAwait(false);
    }
    else
    {
        Console.WriteLine("Provide --file or --line.");
        return 1;
    }

    Console.WriteLine($"Phase 0 report written to: {reportPath}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Phase 0 investigation failed: {ex.Message}");
    return 1;
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}
