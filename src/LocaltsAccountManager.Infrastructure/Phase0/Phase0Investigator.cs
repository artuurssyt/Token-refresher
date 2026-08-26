using System.Text;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Phase0;

public sealed class Phase0Investigator : IPhase0Investigator
{
    private readonly ITxtCredentialParser _parser;
    private readonly ICredentialFingerprinter _fingerprinter;
    private readonly IMicrosoftAuthAdapter _microsoftAuthAdapter;
    private readonly IMinecraftIdentityAdapter _minecraftIdentityAdapter;
    private readonly ILocaltsService _localtsService;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IAuthenticationProfileStore _profileStore;

    public Phase0Investigator(
        ITxtCredentialParser parser,
        ICredentialFingerprinter fingerprinter,
        IMicrosoftAuthAdapter microsoftAuthAdapter,
        IMinecraftIdentityAdapter minecraftIdentityAdapter,
        ILocaltsService localtsService,
        ISecureCredentialStore credentialStore,
        IAuthenticationProfileStore profileStore)
    {
        _parser = parser;
        _fingerprinter = fingerprinter;
        _microsoftAuthAdapter = microsoftAuthAdapter;
        _minecraftIdentityAdapter = minecraftIdentityAdapter;
        _localtsService = localtsService;
        _credentialStore = credentialStore;
        _profileStore = profileStore;
    }

    public async Task<string> InvestigateFileAsync(string inputPath, string outputReportPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file was not found.", inputPath);
        }

        await using var stream = File.OpenRead(inputPath);
        var lines = _parser.ParseFile(inputPath, stream);
        var firstParsed = lines.FirstOrDefault(l => l.ParseStatus == ParseStatus.Parsed);
        if (firstParsed == null)
        {
            throw new InvalidOperationException("No parsable credential line was found in the input file.");
        }

        return await InvestigateParsedAsync(firstParsed, outputReportPath, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> InvestigateLineAsync(string line, string outputReportPath, CancellationToken cancellationToken = default) =>
        InvestigateParsedAsync(_parser.ParseLine(1, line), outputReportPath, cancellationToken);

    private async Task<string> InvestigateParsedAsync(ParsedCredentialLine parsed, string outputReportPath, CancellationToken cancellationToken)
    {
        var analysis = _parser.AnalyzeStructure(parsed);
        var profile = _profileStore.Load();
        var builder = new StringBuilder();

        builder.AppendLine("# Phase 0 Investigation Report");
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        builder.AppendLine("## A. Structure");
        builder.AppendLine($"- Line number: {analysis.LineNumber}");
        builder.AppendLine($"- Input form: {analysis.InputForm}");
        builder.AppendLine($"- Provided username: {analysis.ProvidedUsername ?? "(none)"}");
        builder.AppendLine($"- Credential length: {analysis.CredentialLength}");
        builder.AppendLine($"- Prefix (first 8 chars): {analysis.CredentialPrefix}");
        builder.AppendLine($"- Suffix (last 12 chars): {analysis.CredentialSuffix}");
        builder.AppendLine($"- Token fingerprint: {_fingerprinter.FormatFingerprintPrefix(analysis.TokenFingerprint)}");
        builder.AppendLine($"- Starts with M.C prefix: {analysis.StartsWithMcPrefix}");
        builder.AppendLine($"- Contains MsaArtifacts marker: {analysis.ContainsMsaArtifactsMarker}");
        builder.AppendLine($"- Notes: {analysis.ObservedStructureNotes}");
        builder.AppendLine();

        builder.AppendLine("## B. Origin / Client Context");
        if (profile.IsVerified && profile.Microsoft.IsConfigured)
        {
            builder.AppendLine($"- Configured client ID present: yes");
            builder.AppendLine($"- Configured authority: {profile.Microsoft.Authority}");
            builder.AppendLine($"- Configured scopes: {string.Join(", ", profile.Microsoft.Scopes)}");
        }
        else
        {
            builder.AppendLine("- BLOCKED: OAuth client/application context has not been established.");
        }

        builder.AppendLine();

        builder.AppendLine("## C. Localts Connectivity (informational only)");
        var localts = await _localtsService.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);
        builder.AppendLine($"- Reachable: {localts.IsReachable}");
        builder.AppendLine($"- Summary: {localts.DiagnosticSummary}");
        builder.AppendLine("- Note: Localts connectivity does not determine credential validity.");
        builder.AppendLine();

        builder.AppendLine("## D. Authentication Probe");
        if (parsed.ParseStatus != ParseStatus.Parsed || string.IsNullOrWhiteSpace(parsed.CredentialPayload))
        {
            builder.AppendLine("- Skipped: credential line did not parse successfully.");
        }
        else if (!_microsoftAuthAdapter.IsConfigured)
        {
            builder.AppendLine("- BLOCKED: Microsoft authentication profile is not verified/configured.");
        }
        else
        {
            var msResult = await _microsoftAuthAdapter.AuthenticateAsync(parsed.CredentialPayload, cancellationToken)
                .ConfigureAwait(false);
            builder.AppendLine($"- Microsoft success: {msResult.Success}");
            builder.AppendLine($"- Error category: {msResult.ErrorCategory}");
            builder.AppendLine($"- OAuth error code: {msResult.OAuthErrorCode ?? "(none)"}");
            builder.AppendLine($"- Detail: {msResult.ServiceErrorDetail ?? "(none)"}");

            if (msResult.Success && _minecraftIdentityAdapter.IsConfigured && !string.IsNullOrWhiteSpace(msResult.MicrosoftAccessToken))
            {
                var mcResult = await _minecraftIdentityAdapter.RetrieveIdentityAsync(msResult.MicrosoftAccessToken, cancellationToken)
                    .ConfigureAwait(false);
                builder.AppendLine($"- Minecraft username: {mcResult.AuthenticatedMinecraftUsername ?? "(none)"}");
                builder.AppendLine($"- Minecraft success: {mcResult.Success}");
                builder.AppendLine($"- Minecraft error category: {mcResult.ErrorCategory}");
            }
            else if (msResult.Success)
            {
                builder.AppendLine("- BLOCKED: Minecraft identity chain is not verified/configured.");
            }

            if (msResult.Success && !string.IsNullOrWhiteSpace(msResult.ReplacementRefreshToken))
            {
                var oldRef = await _credentialStore.StoreAsync(parsed.CredentialPayload!, cancellationToken).ConfigureAwait(false);
                var newRef = await _credentialStore.ReplaceAsync(oldRef, msResult.ReplacementRefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                builder.AppendLine($"- Replacement token persistence test: success (reference {newRef[..8]}...)");
            }
            else
            {
                builder.AppendLine("- Replacement token persistence test: not performed (no replacement token returned).");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## E. Outcome");
        builder.AppendLine("- This report contains no live credential values.");
        builder.AppendLine("- Populate authentication_profile.json only after confirming client/scopes/Minecraft chain.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputReportPath)!);
        await File.WriteAllTextAsync(outputReportPath, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return outputReportPath;
    }
}
