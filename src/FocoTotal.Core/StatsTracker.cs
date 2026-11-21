namespace FocoTotal.Core;

/// <summary>Contabiliza tentativas bloqueadas e minutos de foco por dia. Guarda até 365 dias.</summary>
public static class StatsTracker
{
    public const int MaxDays = 365;

    public static DayStats Today(StatsData data, DateTime localNow)
    {
        var key = localNow.ToString("yyyy-MM-dd");
        var day = data.Days.FirstOrDefault(d => d.Date == key);
        if (day is null)
        {
            day = new DayStats { Date = key };
            data.Days.Add(day);
            data.Days.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            if (data.Days.Count > MaxDays) data.Days.RemoveRange(0, data.Days.Count - MaxDays);
        }
        return day;
    }

    public static void RecordBlockedApp(StatsData data, DateTime localNow, string processName)
    {
        var day = Today(data, localNow);
        var key = AppRules.EnsureExe(processName).ToLowerInvariant();
        day.BlockedApps[key] = day.BlockedApps.GetValueOrDefault(key) + 1;
    }

    public static void AddFocusMinute(StatsData data, DateTime localNow) => Today(data, localNow).FocusMinutes++;
}
