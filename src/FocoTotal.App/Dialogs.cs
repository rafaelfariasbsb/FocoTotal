using System.Diagnostics;
using FocoTotal.Core;

namespace FocoTotal.App;

/// <summary>Pede a resposta da trava: texto aleatório (sem copiar e colar) ou senha.</summary>
internal sealed class ChallengeDialog : Form
{
    private readonly TextBox _answer;
    public string Answer => _answer.Text;

    public ChallengeDialog(string message, string? challenge, bool isPassword)
    {
        Text = "Encerrar bloqueio";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(620, challenge is null ? 170 : 420);
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.Controls.Add(Ui.Label(message));

        if (challenge is not null)
        {
            // Mostrado em um rótulo, que não permite selecionar nem copiar.
            var lbl = new Label
            {
                Text = challenge,
                Font = new Font(FontFamily.GenericMonospace, 11f),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(6),
            };
            var scroll = new Panel { Dock = DockStyle.Fill, Height = 160, AutoScroll = true };
            lbl.MaximumSize = new Size(570, 0);
            lbl.AutoSize = true;
            scroll.Controls.Add(lbl);
            layout.Controls.Add(scroll);
        }

        _answer = new NoPasteTextBox
        {
            Dock = DockStyle.Fill,
            Multiline = challenge is not null,
            Height = challenge is not null ? 120 : 28,
            UseSystemPasswordChar = isPassword,
            ScrollBars = challenge is not null ? ScrollBars.Vertical : ScrollBars.None,
        };
        layout.Controls.Add(_answer);

        var ok = new Button { Text = "Encerrar", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(Ui.Row(ok, cancel));
        AcceptButton = challenge is null ? ok : null;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    /// <summary>Caixa de texto que ignora colar (Ctrl+V, Shift+Insert e menu de contexto).</summary>
    private sealed class NoPasteTextBox : TextBox
    {
        private const int WmPaste = 0x0302;
        public NoPasteTextBox() { ShortcutsEnabled = false; }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPaste) return;
            base.WndProc(ref m);
        }
    }
}

/// <summary>Cria ou edita uma agenda semanal.</summary>
internal sealed class ScheduleDialog : Form
{
    private readonly ComboBox _list = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox[] _days;
    private readonly DateTimePicker _start = TimePicker();
    private readonly DateTimePicker _end = TimePicker();
    private readonly CheckBox _locked = new() { Text = "Travada (não pode ser alterada durante o horário)", AutoSize = true };
    private readonly CheckBox _enabled = new() { Text = "Ativa", AutoSize = true, Checked = true };
    private readonly List<BlockList> _lists;
    public ScheduleEntry Entry { get; }

    private static DateTimePicker TimePicker() => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };

    public ScheduleDialog(List<BlockList> lists, ScheduleEntry? existing)
    {
        _lists = lists;
        Entry = existing is null ? new ScheduleEntry { Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } } : Ui.Clone(existing);

        Text = existing is null ? "Nova agenda" : "Editar agenda";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, 260);
        Padding = new Padding(12);

        foreach (var l in lists) _list.Items.Add(l.Name);
        var idx = lists.FindIndex(l => l.Id == Entry.ListId);
        _list.SelectedIndex = idx >= 0 ? idx : (lists.Count > 0 ? 0 : -1);

        _days = Ui.Days.Select(d => new CheckBox { Text = d.Short, AutoSize = true, Checked = Entry.Days.Contains(d.Day) }).ToArray();
        _start.Value = DateTime.Today + (BlockEngine.TryParseTime(Entry.Start, out var s) ? s : TimeSpan.FromHours(9));
        _end.Value = DateTime.Today + (BlockEngine.TryParseTime(Entry.End, out var e) ? e : TimeSpan.FromHours(18));
        _locked.Checked = Entry.Locked;
        _enabled.Checked = Entry.Enabled;

        var ok = new Button { Text = "Salvar", AutoSize = true };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        CancelButton = cancel;

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(Ui.Row(Ui.Label("Lista:"), _list));
        layout.Controls.Add(Ui.Row(_days.Cast<Control>().ToArray()));
        layout.Controls.Add(Ui.Row(Ui.Label("Das"), _start, Ui.Label("às"), _end, Ui.Label("(se o fim for antes do início, termina no dia seguinte)")));
        layout.Controls.Add(Ui.Row(_enabled, _locked));
        layout.Controls.Add(Ui.Row(ok, cancel));
        Controls.Add(layout);
    }

    private void Save()
    {
        if (_list.SelectedIndex < 0) { Ui.Error(this, "Crie uma lista antes de agendar."); return; }
        var days = Ui.Days.Where((_, i) => _days[i].Checked).Select(d => d.Day).ToList();
        if (days.Count == 0) { Ui.Error(this, "Escolha pelo menos um dia."); return; }
        Entry.ListId = _lists[_list.SelectedIndex].Id;
        Entry.Days = days;
        Entry.Start = _start.Value.ToString("HH:mm");
        Entry.End = _end.Value.ToString("HH:mm");
        Entry.Locked = _locked.Checked;
        Entry.Enabled = _enabled.Checked;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Escolhe um ou mais apps entre os que estão abertos agora.</summary>
internal sealed class ProcessPickerDialog : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    public List<string> Selected { get; } = new();

    public ProcessPickerDialog()
    {
        Text = "Apps em execução";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 460);
        Padding = new Padding(8);

        var names = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .Select(p => AppRules.EnsureExe(p.ProcessName))
            .Where(n => !AppRules.IsProtected(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) _list.Items.Add(n);

        var ok = new Button { Text = "Adicionar", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) => Selected.AddRange(_list.CheckedItems.Cast<string>());
        var bottom = Ui.Row(ok, new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true });
        bottom.Dock = DockStyle.Bottom;
        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(new Label { Text = "Mostrando apps com janela aberta:", Dock = DockStyle.Top, AutoSize = true });
    }
}
