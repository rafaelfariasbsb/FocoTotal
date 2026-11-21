using System.Diagnostics;
using System.Runtime.InteropServices;
using FocoTotal.Core;

namespace FocoTotal.Service;

/// <summary>Encerra processos que correspondem às regras de aplicativo ativas.</summary>
public sealed partial class ProcessEnforcer
{
    private readonly ILogger<ProcessEnforcer> _log;
    private readonly int _selfPid = Environment.ProcessId;

    public ProcessEnforcer(ILogger<ProcessEnforcer> log) => _log = log;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    private static string? GetImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <returns>Nomes dos processos encerrados.</returns>
    public List<string> Enforce(IReadOnlyList<string> appRules, bool protectSystemTools)
    {
        var killed = new List<string>();
        if (appRules.Count == 0 && !protectSystemTools) return killed;

        var anyPathRule = appRules.Any(r => r.Contains('\\') || r.Contains('/'));

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4 || p.Id == _selfPid) continue;
                    var name = AppRules.EnsureExe(p.ProcessName);
                    if (AppRules.IsProtected(name)) continue;

                    var path = anyPathRule ? GetImagePath(p.Id) : null;
                    var match = appRules.Any(r => AppRules.Matches(r, name, path))
                        || (protectSystemTools && AppRules.SystemTools.Contains(name, StringComparer.OrdinalIgnoreCase));
                    if (!match) continue;

                    p.Kill(entireProcessTree: true);
                    killed.Add(name);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // O processo já terminou ou não pode ser encerrado.
                }
            }
        }

        if (killed.Count > 0) _log.LogInformation("Encerrados: {Apps}", string.Join(", ", killed));
        return killed;
    }
}
