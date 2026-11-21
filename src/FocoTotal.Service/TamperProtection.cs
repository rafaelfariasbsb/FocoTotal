using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FocoTotal.Service;

/// <summary>
/// Durante bloqueio travado:
/// - troca a ACL do serviço para negar parar, excluir e reconfigurar a quem está logado (interativo ou pela rede),
///   inclusive administradores. O próprio serviço (SYSTEM) não é afetado.
/// - grava HKLM\SOFTWARE\FocoTotal\Locked=1, que o MSI lê para recusar a desinstalação.
/// </summary>
public sealed partial class TamperProtection
{
    private readonly ILogger<TamperProtection> _log;
    private bool? _applied;

    // CC=query config, LC=query status, SW=enum deps, RP=start, WP=stop, DT=pause, LO=interrogate,
    // CR=user control, RC=read control, SD=delete, WD=write DAC, WO=write owner, DC=change config.
    private const string UnlockedSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private const string LockedSddl =
        "D:(D;;WPDTSDWDWODC;;;IU)(D;;WPDTSDWDWODC;;;NU)" +
        "(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public TamperProtection(ILogger<TamperProtection> log) => _log = log;

    public void Apply(bool locked)
    {
        if (_applied == locked) return;
        try
        {
            SetServiceSddl(ServiceInfo.Name, locked ? LockedSddl : UnlockedSddl);
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(ServiceInfo.RegistryKey);
            key.SetValue("Locked", locked ? 1 : 0, RegistryValueKind.DWord);
            _applied = locked;
            _log.LogInformation("Proteção contra burla {State}", locked ? "ativada" : "desativada");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao aplicar proteção contra burla");
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint DaclSecurityInformation = 0x00000004;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceObjectSecurity(IntPtr service, uint info, IntPtr descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSddl(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    private static void SetServiceSddl(string serviceName, string sddl)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var svc = OpenService(scm, serviceName, ReadControl | WriteDac);
            if (svc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!ConvertSddl(sddl, 1, out var sd, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
                try
                {
                    if (!SetServiceObjectSecurity(svc, DaclSecurityInformation, sd)) throw new System.ComponentModel.Win32Exception();
                }
                finally { LocalFree(sd); }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
