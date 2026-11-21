using System.Globalization;

namespace FocoTotal.Core;

/// <summary>O que deve estar bloqueado agora.</summary>
public sealed class EffectivePolicy
{
    public HashSet<string> ActiveListIds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> LockedListIds { get; } = new(StringComparer.Ordinal);
    public List<string> Sites { get; } = new();
    public List<string> Apps { get; } = new();

    public bool AnyActive => ActiveListIds.Count > 0;
    public bool AnyLocked => LockedListIds.Count > 0;
}

/// <summary>Regras puras de bloqueio, agenda e Pomodoro. Não acessa disco nem o sistema.</summary>
public static class BlockEngine
{
    public static bool TryParseTime(string s, out TimeSpan time) =>
        TimeSpan.TryParseExact(s?.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    /// <summary>Verdadeiro se a agenda está no horário ativo no horário local informado.</summary>
    public static bool IsScheduleActive(ScheduleEntry entry, DateTime localNow)
    {
        if (!entry.Enabled || entry.Days.Count == 0) return false;
        if (!TryParseTime(entry.Start, out var start) || !TryParseTime(entry.End, out var end)) return false;

        var t = localNow.TimeOfDay;
        var today = localNow.DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);

        if (start < end)
            return entry.Days.Contains(today) && t >= start && t < end;

        // Passa da meia-noite (ou start == end: 24 horas a partir do início).
        return (entry.Days.Contains(today) && t >= start) || (entry.Days.Contains(yesterday) && t < end);
    }

    /// <summary>Remove bloqueios vencidos e avança o Pomodoro. Retorna true se o estado mudou.</summary>
    public static bool Tick(ServiceState state, DateTimeOffset nowUtc)
    {
        var changed = state.Blocks.RemoveAll(b => b.EndUtc <= nowUtc) > 0;

        var p = state.Pomodoro;
        while (p is not null && p.PhaseEndUtc <= nowUtc)
        {
            changed = true;
            if (p.Phase == PomodoroPhase.Focus)
            {
                if (p.CurrentCycle >= p.TotalCycles) { state.Pomodoro = p = null; break; }
                p.Phase = PomodoroPhase.Break;
                p.PhaseEndUtc = p.PhaseEndUtc.AddMinutes(p.BreakMinutes);
            }
            else
            {
                p.CurrentCycle++;
                p.Phase = PomodoroPhase.Focus;
                p.PhaseEndUtc = p.PhaseEndUtc.AddMinutes(p.FocusMinutes);
            }
        }
        return changed;
    }

    public static EffectivePolicy Compute(AppConfig config, ServiceState state, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var policy = new EffectivePolicy();

        foreach (var b in state.Blocks.Where(b => b.StartUtc <= nowUtc && b.EndUtc > nowUtc))
        {
            foreach (var id in b.ListIds)
            {
                policy.ActiveListIds.Add(id);
                if (b.IsLocked) policy.LockedListIds.Add(id);
            }
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        foreach (var s in config.Schedules.Where(s => IsScheduleActive(s, localNow)))
        {
            policy.ActiveListIds.Add(s.ListId);
            if (s.Locked) policy.LockedListIds.Add(s.ListId);
        }

        if (state.Pomodoro is { Phase: PomodoroPhase.Focus } p && p.PhaseEndUtc > nowUtc)
        {
            policy.ActiveListIds.Add(p.ListId);
            if (p.Lock != LockKind.None) policy.LockedListIds.Add(p.ListId);
        }

        // Pomodoro travado também protege as configurações durante a pausa.
        if (state.Pomodoro is { } pp && pp.Lock != LockKind.None)
            policy.LockedListIds.Add(pp.ListId);

        foreach (var list in config.Lists.Where(l => policy.ActiveListIds.Contains(l.Id)))
        {
            policy.Sites.AddRange(list.Sites);
            policy.Apps.AddRange(list.Apps);
        }
        return policy;
    }

    /// <summary>
    /// Valida uma nova configuração enviada pelo app. Durante um bloqueio travado, só é permitido
    /// endurecer as regras: adicionar itens, nunca remover. Retorna a mensagem de erro, ou null se permitido.
    /// </summary>
    public static string? ValidateConfigChange(AppConfig current, AppConfig updated, EffectivePolicy policy, DateTime localNow)
    {
        foreach (var listId in policy.LockedListIds)
        {
            var oldList = current.Lists.FirstOrDefault(l => l.Id == listId);
            if (oldList is null) continue;
            var newList = updated.Lists.FirstOrDefault(l => l.Id == listId);
            if (newList is null)
                return $"A lista \"{oldList.Name}\" está em um bloqueio travado e não pode ser excluída.";

            var newSites = new HashSet<string>(newList.Sites.Select(s => DomainRules.Normalize(s) ?? s.Trim().ToLowerInvariant()));
            var removedSite = oldList.Sites.FirstOrDefault(s => !newSites.Contains(DomainRules.Normalize(s) ?? s.Trim().ToLowerInvariant()));
            if (removedSite is not null)
                return $"O site \"{removedSite}\" está em um bloqueio travado e não pode ser removido agora.";

            var newApps = new HashSet<string>(newList.Apps.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            var removedApp = oldList.Apps.FirstOrDefault(a => !newApps.Contains(a.Trim()));
            if (removedApp is not null)
                return $"O app \"{removedApp}\" está em um bloqueio travado e não pode ser removido agora.";
        }

        foreach (var s in current.Schedules.Where(s => s.Locked && IsScheduleActive(s, localNow)))
        {
            var n = updated.Schedules.FirstOrDefault(x => x.Id == s.Id);
            if (n is null || !n.Enabled || !n.Locked || n.ListId != s.ListId || n.Start != s.Start || n.End != s.End
                || !s.Days.All(n.Days.Contains))
                return "Uma agenda travada está no horário ativo e não pode ser alterada até terminar.";
        }

        if (policy.AnyLocked)
        {
            var o = current.Settings; var n = updated.Settings;
            if ((o.ProtectSystemTools && !n.ProtectSystemTools) || (o.DisableBrowserDoH && !n.DisableBrowserDoH)
                || (o.ProtectUninstall && !n.ProtectUninstall))
                return "As proteções não podem ser desligadas durante um bloqueio travado.";
        }

        return null;
    }

    /// <summary>Valida o pedido de início de bloqueio. Retorna erro ou null.</summary>
    public static string? ValidateStart(AppConfig config, StartBlockRequest req)
    {
        if (req.ListIds is null || req.ListIds.Count == 0) return "Escolha pelo menos uma lista.";
        if (req.ListIds.Any(id => config.Lists.All(l => l.Id != id))) return "Lista não encontrada.";
        if (req.DurationMinutes < 1 || req.DurationMinutes > 60 * 24 * 60) return "A duração deve ficar entre 1 minuto e 60 dias.";
        if (req.Lock == LockKind.RandomText && (req.RandomTextLength < 10 || req.RandomTextLength > 5000))
            return "O texto aleatório deve ter entre 10 e 5000 caracteres.";
        if (req.Lock == LockKind.Password && string.IsNullOrEmpty(req.Password)) return "Informe a senha da trava.";
        return null;
    }
}
