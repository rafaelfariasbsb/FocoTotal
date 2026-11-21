using System.Text;
using System.Text.RegularExpressions;

namespace FocoTotal.Core;

/// <summary>Normaliza entradas de sites e gera o bloco do arquivo hosts.</summary>
public static partial class DomainRules
{
    public const string HostsBeginMarker = "# >>> FocoTotal (nao edite este bloco) >>>";
    public const string HostsEndMarker = "# <<< FocoTotal <<<";

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostnameRegex();

    /// <summary>Subdomínios comuns adicionados junto com o domínio principal, já que o hosts não aceita curingas.</summary>
    private static readonly string[] CommonSubdomains = { "www", "m", "mobile", "web", "app", "api" };

    /// <summary>
    /// Converte "https://www.YouTube.com/watch?v=1", "*.reddit.com" ou "x.com" no domínio base.
    /// Retorna null se a entrada não for um domínio válido.
    /// </summary>
    public static string? Normalize(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var s = entry.Trim().ToLowerInvariant();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];

        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];

        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        s = s.Trim('.');

        return HostnameRegex().IsMatch(s) ? s : null;
    }

    /// <summary>Expande os domínios com as variações comuns de subdomínio.</summary>
    public static IReadOnlyList<string> ExpandForHosts(IEnumerable<string> entries)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var d = Normalize(e);
            if (d is null) continue;
            set.Add(d);
            foreach (var sub in CommonSubdomains) set.Add($"{sub}.{d}");
        }
        return set.ToList();
    }

    /// <summary>Remove o bloco do FocoTotal de um conteúdo de hosts.</summary>
    public static string RemoveBlock(string hostsContent)
    {
        var lines = SplitLines(hostsContent);
        var output = new List<string>(lines.Count);
        var inside = false;
        foreach (var line in lines)
        {
            if (line.Trim() == HostsBeginMarker) { inside = true; continue; }
            if (line.Trim() == HostsEndMarker) { inside = false; continue; }
            if (!inside) output.Add(line);
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return output.Count == 0 ? "" : string.Join("\r\n", output) + "\r\n";
    }

    /// <summary>Gera o novo conteúdo do hosts com o bloco do FocoTotal para os domínios informados.</summary>
    public static string ApplyBlock(string hostsContent, IReadOnlyCollection<string> expandedDomains)
    {
        var baseContent = RemoveBlock(hostsContent);
        if (expandedDomains.Count == 0) return baseContent;

        var sb = new StringBuilder(baseContent);
        if (sb.Length > 0) sb.Append("\r\n");
        sb.Append(HostsBeginMarker).Append("\r\n");
        foreach (var d in expandedDomains)
        {
            sb.Append("0.0.0.0 ").Append(d).Append("\r\n");
            sb.Append(":: ").Append(d).Append("\r\n");
        }
        sb.Append(HostsEndMarker).Append("\r\n");
        return sb.ToString();
    }

    private static List<string> SplitLines(string s) =>
        s.Replace("\r\n", "\n").Split('\n').ToList();
}
