namespace LocaltsAccountManager.Core.Interfaces;

public interface IAuthenticationProfileStore
{
    Configuration.AuthenticationProfile Load();
    void Save(Configuration.AuthenticationProfile profile);
    string ProfilePath { get; }
}

public interface IAppSettingsStore
{
    Configuration.AppSettings Load();
    void Save(Configuration.AppSettings settings);
    string SettingsPath { get; }
}

public interface ICredentialFingerprinter
{
    string ComputeFingerprint(string credential);
    string FormatFingerprintPrefix(string fingerprint);
}

public interface INetworkDiagnosticsService
{
    Task<NetworkDiagnosticResult> TestMicrosoftAsync(CancellationToken cancellationToken = default);
    Task<NetworkDiagnosticResult> TestMinecraftAsync(CancellationToken cancellationToken = default);
    Task<NetworkDiagnosticResult> TestLocaltsAsync(CancellationToken cancellationToken = default);
}

public sealed class NetworkDiagnosticResult
{
    public string ServiceName { get; init; } = string.Empty;
    public bool IsReachable { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? FailureCategory { get; init; }
}
