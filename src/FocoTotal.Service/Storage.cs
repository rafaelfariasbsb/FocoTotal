using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FocoTotal.Core;

namespace FocoTotal.Service;

/// <summary>
/// Persistência em %ProgramData%\FocoTotal. A pasta fica com permissão de escrita só para SYSTEM:
/// administradores e usuários apenas leem. Toda alteração passa pelo serviço.
/// </summary>
public sealed class Storage
{
    private readonly ILogger<Storage> _log;
    public string Directory { get; }

    public Storage(ILogger<Storage> log)
    {
        _log = log;
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocoTotal");
        System.IO.Directory.CreateDirectory(Directory);
        Secure();
    }

    private string ConfigPath => Path.Combine(Directory, "config.json");
    private string StatePath => Path.Combine(Directory, "state.json");
    private string StatsPath => Path.Combine(Directory, "stats.json");

    public AppConfig LoadConfig() => Load(ConfigPath, AppConfig.CreateDefault);
    public ServiceState LoadState() => Load(StatePath, () => new ServiceState());
    public StatsData LoadStats() => Load(StatsPath, () => new StatsData());

    public void SaveConfig(AppConfig c) => Save(ConfigPath, c);
    public void SaveState(ServiceState s) => Save(StatePath, s);
    public void SaveStats(StatsData s) => Save(StatsPath, s);

    private T Load<T>(string path, Func<T> fallback)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Json.Options);
                if (value is not null) return value;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Falha ao ler {Path}", candidate);
            }
        }
        return fallback();
    }

    private void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Pretty));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    private void Secure()
    {
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.SetOwner(system);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Directory).SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível proteger a pasta de dados");
        }
    }
}
