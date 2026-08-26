namespace LocaltsAccountManager.Core.Interfaces;

public interface IPhase0Investigator
{
    Task<string> InvestigateFileAsync(string inputPath, string outputReportPath, CancellationToken cancellationToken = default);
    Task<string> InvestigateLineAsync(string line, string outputReportPath, CancellationToken cancellationToken = default);
}
