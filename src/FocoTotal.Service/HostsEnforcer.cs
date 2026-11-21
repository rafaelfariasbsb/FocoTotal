using System.Runtime.InteropServices;
using System.Text;
using FocoTotal.Core;

namespace FocoTotal.Service;

/// <summary>Mantém o bloco do FocoTotal no arquivo hosts e reaplica se alguém mexer.</summary>
public sealed partial class HostsEnforcer
{
    private readonly ILogger<HostsEnforcer> _log;
    private readonly string _path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public HostsEnforcer(ILogger<HostsEnforcer> log) => _log = log;

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    public void Apply(IEnumerable<string> sites)
    {
        try
        {
            var expanded = DomainRules.ExpandForHosts(sites);
            var current = File.Exists(_path) ? ReadShared(_path) : "";
            var desired = DomainRules.ApplyBlock(current, expanded);
            if (desired == current) return;

            var info = new FileInfo(_path);
            if (info.Exists && info.IsReadOnly) info.IsReadOnly = false;
            File.WriteAllText(_path, desired, Utf8NoBom);
            DnsFlushResolverCache();
            _log.LogInformation("Arquivo hosts atualizado: {Count} nomes bloqueados", expanded.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao atualizar o arquivo hosts");
        }
    }

    public void Clear() => Apply(Array.Empty<string>());

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
