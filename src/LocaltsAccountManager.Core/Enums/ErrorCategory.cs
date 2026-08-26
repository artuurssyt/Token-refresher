namespace LocaltsAccountManager.Core.Enums;

public enum ErrorCategory
{
    None,
    MalformedInput,
    MissingCredential,
    UnsupportedCredentialFormat,
    CredentialRejected,
    UserInteractionRequired,
    AuthenticationTimeout,
    NetworkUnavailable,
    DnsFailure,
    TlsFailure,
    ProxyFailure,
    RateLimited,
    MicrosoftServiceError,
    MinecraftServiceError,
    MinecraftProfileUnavailable,
    VendorUnavailable,
    VendorAuthenticationFailure,
    VendorRateLimited,
    ConfigurationRequired,
    ClientBindingMismatch,
    Cancelled,
    Unknown
}
