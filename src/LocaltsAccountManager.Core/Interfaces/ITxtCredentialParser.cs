namespace LocaltsAccountManager.Core.Interfaces;

public interface ITxtCredentialParser
{
    IReadOnlyList<Models.ParsedCredentialLine> ParseFile(string filePath, Stream content);
    Models.ParsedCredentialLine ParseLine(int lineNumber, string line);
    Models.CredentialStructureAnalysis AnalyzeStructure(Models.ParsedCredentialLine parsed);
}
