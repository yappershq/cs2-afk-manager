using Microsoft.Extensions.DependencyInjection;
using AfkManager.Configuration;
using AfkManager.Modules;

namespace AfkManager;

internal static class ModuleDependencyInjection
{
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // Config (constructs ConVars on instantiation — not an IModule)
        services.AddSingleton<IAfkConfig, AfkConfig>();

        // Core AFK logic
        services.AddSingleton<AfkModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<AfkModule>());

        return services;
    }
}
