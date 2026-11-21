namespace FocoTotal.Core;

/// <summary>Decide se um processo corresponde a uma regra de aplicativo.</summary>
public static class AppRules
{
    /// <summary>Ferramentas encerradas durante bloqueio travado quando Settings.ProtectSystemTools está ligado.</summary>
    public static readonly IReadOnlyList<string> SystemTools = new[]
    {
        "taskmgr.exe", "resmon.exe", "perfmon.exe", "procexp.exe", "procexp64.exe",
        "processhacker.exe", "systeminformer.exe", "mmc.exe", "regedit.exe",
    };

    /// <summary>Processos que nunca são encerrados, mesmo que alguém os coloque numa lista.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "svchost.exe", "explorer.exe", "dwm.exe", "fontdrvhost.exe", "focototal.service.exe", "focototal.app.exe",
        "msmpeng.exe", "conhost.exe", "sihost.exe", "ctfmon.exe", "logonui.exe", "userinit.exe",
    };

    public static bool IsProtected(string processName) =>
        Protected.Contains(EnsureExe(processName));

    /// <param name="rule">Regra: "steam.exe", "steam", "C:\Games\steam.exe" ou "C:\Games\*".</param>
    /// <param name="processName">Nome do processo com ou sem ".exe".</param>
    /// <param name="fullPath">Caminho do executável, se conhecido.</param>
    public static bool Matches(string rule, string processName, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        rule = rule.Trim().Trim('"');
        var name = EnsureExe(processName);
        if (IsProtected(name)) return false;

        var looksLikePath = rule.Contains('\\') || rule.Contains('/');
        if (!looksLikePath)
        {
            return string.Equals(EnsureExe(rule), name, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrEmpty(fullPath)) return false;
        var normRule = rule.Replace('/', '\\');
        var normPath = fullPath.Replace('/', '\\');

        if (normRule.EndsWith("\\*", StringComparison.Ordinal) || normRule.EndsWith('\\'))
        {
            var folder = normRule.TrimEnd('*').TrimEnd('\\') + "\\";
            return normPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normRule, normPath, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".exe";
    }
}
