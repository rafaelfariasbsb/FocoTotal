using System.Text.Json;
using FocoTotal.Core;

namespace FocoTotal.App;

/// <summary>Pequenos auxiliares de layout e textos.</summary>
internal static class Ui
{
    public static Label Label(string text, bool bold = false, float size = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 6, 3, 3),
        Font = bold || size > 0 ? new Font(SystemFonts.MessageBoxFont!.FontFamily, size > 0 ? size : 9.75f, bold ? FontStyle.Bold : FontStyle.Regular) : null,
    };

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3) };
        b.Click += onClick;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight };
        p.Controls.AddRange(controls);
        return p;
    }

    public static NumericUpDown Number(int min, int max, int value) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = 70, Margin = new Padding(3) };

    public static ListView List(params (string Header, int Width)[] columns)
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
        foreach (var (h, w) in columns) lv.Columns.Add(h, w);
        return lv;
    }

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Json.Options), Json.Options)!;

    public static readonly string[] LockNames = { "Sem trava", "Trava de tempo", "Texto aleatório", "Senha" };

    public static string LockName(LockKind k) => LockNames[(int)k];

    public static string SourceName(BlockSource s) => s switch
    {
        BlockSource.Schedule => "Agenda",
        BlockSource.Pomodoro => "Pomodoro",
        _ => "Manual",
    };

    public static readonly (DayOfWeek Day, string Short)[] Days =
    {
        (DayOfWeek.Monday, "Seg"), (DayOfWeek.Tuesday, "Ter"), (DayOfWeek.Wednesday, "Qua"), (DayOfWeek.Thursday, "Qui"),
        (DayOfWeek.Friday, "Sex"), (DayOfWeek.Saturday, "Sáb"), (DayOfWeek.Sunday, "Dom"),
    };

    public static string DaysText(IEnumerable<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        if (set.Count == 7) return "Todos os dias";
        return string.Join(", ", Days.Where(d => set.Contains(d.Day)).Select(d => d.Short));
    }

    public static string Remaining(DateTimeOffset endUtc)
    {
        var r = endUtc - DateTimeOffset.UtcNow;
        if (r < TimeSpan.Zero) r = TimeSpan.Zero;
        return r.TotalDays >= 1 ? $"{(int)r.TotalDays}d {r.Hours}h {r.Minutes:00}min" : $"{(int)r.TotalHours}h {r.Minutes:00}min {r.Seconds:00}s";
    }

    public static void Error(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "FocoTotal", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Info(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "FocoTotal", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
