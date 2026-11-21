using Microsoft.Win32;

namespace FocoTotal.Service;

/// <summary>
/// Desliga o DNS sobre HTTPS dos navegadores via política, para que o arquivo hosts seja respeitado.
/// Só cria valores que não existem. Política já definida por GPO ou Intune nunca é sobrescrita.
/// </summary>
public sealed class BrowserPolicyEnforcer
{
    private readonly ILogger<BrowserPolicyEnforcer> _log;
    private DateTime _lastApply = DateTime.MinValue;
    private bool? _lastState;
    private const string OwnedValue = "OwnedBrowserPolicies";

    private sealed record PolicyValue(string Key, string Name, object Value, RegistryValueKind Kind);

    private static readonly PolicyValue[] Policies =
    {
        new(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off", RegistryValueKind.String),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off", RegistryValueKind.String),
        new(@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off", RegistryValueKind.String),
        new(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0, RegistryValueKind.DWord),
        new(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1, RegistryValueKind.DWord),
    };

    public BrowserPolicyEnforcer(ILogger<BrowserPolicyEnforcer> log) => _log = log;

    public void Apply(bool enabled)
    {
        // Reaplica a cada minuto ou quando o estado muda.
        if (_lastState == enabled && DateTime.UtcNow - _lastApply < TimeSpan.FromMinutes(1)) return;
        _lastState = enabled;
        _lastApply = DateTime.UtcNow;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var own = hklm.CreateSubKey(ServiceInfo.RegistryKey);
            var owned = new HashSet<string>((own.GetValue(OwnedValue) as string[]) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var p in Policies)
            {
                var id = $@"{p.Key}\{p.Name}";
                if (enabled)
                {
                    using var key = hklm.CreateSubKey(p.Key);
                    var existing = key.GetValue(p.Name);
                    if (existing is null) { key.SetValue(p.Name, p.Value, p.Kind); owned.Add(id); }
                    else if (owned.Contains(id) && !Equals(existing.ToString(), p.Value.ToString())) key.SetValue(p.Name, p.Value, p.Kind);
                }
                else if (owned.Contains(id))
                {
                    using var key = hklm.OpenSubKey(p.Key, writable: true);
                    key?.DeleteValue(p.Name, throwOnMissingValue: false);
                    owned.Remove(id);
                }
            }

            own.SetValue(OwnedValue, owned.ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao aplicar políticas de navegador");
        }
    }
}
