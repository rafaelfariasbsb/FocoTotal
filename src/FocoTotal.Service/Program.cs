using FocoTotal.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInfo.Name);
builder.Services.AddSingleton<Storage>();
builder.Services.AddSingleton<HostsEnforcer>();
builder.Services.AddSingleton<ProcessEnforcer>();
builder.Services.AddSingleton<BrowserPolicyEnforcer>();
builder.Services.AddSingleton<TamperProtection>();
builder.Services.AddSingleton<Coordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Coordinator>());
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();

namespace FocoTotal.Service
{
    public static class ServiceInfo
    {
        public const string Name = "FocoTotal";
        public const string RegistryKey = @"SOFTWARE\FocoTotal";
        public const string PolicyKey = @"SOFTWARE\Policies\FocoTotal";
        public static string Version =>
            typeof(ServiceInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
