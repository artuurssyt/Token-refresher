using System.Runtime.Versioning;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Infrastructure.Authentication;
using LocaltsAccountManager.Infrastructure.Configuration;
using LocaltsAccountManager.Infrastructure.Diagnostics;
using LocaltsAccountManager.Infrastructure.Export;
using LocaltsAccountManager.Infrastructure.Import;
using LocaltsAccountManager.Infrastructure.Localts;
using LocaltsAccountManager.Infrastructure.Minecraft;
using LocaltsAccountManager.Infrastructure.Persistence;
using LocaltsAccountManager.Infrastructure.Phase0;
using LocaltsAccountManager.Infrastructure.Processing;
using LocaltsAccountManager.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace LocaltsAccountManager.Infrastructure;

public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddLocaltsAccountManagerInfrastructure(this IServiceCollection services)
    {
        AuthenticationProfileStore.EnsureTemplateExists();

        services.AddSingleton<SecretSafeLogger>();
        services.AddSingleton<ICredentialFingerprinter, CredentialFingerprinter>();
        services.AddSingleton<ISecureCredentialStore, DpapiCredentialStore>();
        services.AddSingleton<IAuthenticationProfileStore, AuthenticationProfileStore>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<ITxtCredentialParser, TxtCredentialParser>();
        services.AddSingleton<IAccountRepository, SqliteAccountRepository>();
        services.AddSingleton<IBatchRepository, SqliteBatchRepository>();
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IMicrosoftAuthAdapter, MicrosoftAuthAdapter>();
        services.AddSingleton<IMinecraftIdentityAdapter, MinecraftIdentityAdapter>();
        services.AddSingleton<IMinecraftProfileLookupService, MinecraftProfileLookupService>();
        services.AddHttpClient<LocaltsApiClient>(client =>
        {
            client.BaseAddress = new Uri(LocaltsApiClient.ApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<ILocaltsService, LocaltsApiService>();
        services.AddSingleton<INetworkDiagnosticsService, NetworkDiagnosticsService>();
        services.AddSingleton<IPhase0Investigator, Phase0Investigator>();
        services.AddSingleton<RetryPolicy>();
        services.AddSingleton<ThrottleCoordinator>();
        services.AddSingleton<IBatchProcessor, BatchProcessor>();
        services.AddSingleton<IPoolProcessor, PoolProcessor>();

        services.AddHttpClient("Microsoft");
        services.AddHttpClient("Minecraft");
        services.AddHttpClient("MinecraftProfile", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LocaltsAccountManager/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("Diagnostics");

        return services;
    }

    public static async Task InitializeInfrastructureAsync(IServiceProvider services)
    {
        await services.GetRequiredService<IAccountRepository>().InitializeAsync().ConfigureAwait(false);
        await services.GetRequiredService<IBatchRepository>().InitializeAsync().ConfigureAwait(false);
    }
}
