using System.Text.Json;
using FocoTotal.Core;

namespace FocoTotal.App;

internal sealed class MainForm : Form
{
    private readonly IpcClient _ipc = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly NotifyIcon _tray;
    private bool _exiting;
    private StatusInfo? _status;
    private int _lastBlockedToday = -1;

    // Bloqueio
    private readonly Label _lblStatus = Ui.Label("Conectando ao serviço…", bold: true, size: 12f);
    private readonly ListView _lvBlocks = Ui.List(("Listas", 260), ("Restante", 140), ("Termina", 120), ("Trava", 120), ("Origem", 90));
    private readonly CheckedListBox _clbStartLists = new() { CheckOnClick = true, Width = 300, Height = 110 };
    private readonly NumericUpDown _numHours = Ui.Number(0, 24 * 60, 1);
    private readonly NumericUpDown _numMinutes = Ui.Number(0, 59, 0);
    private readonly ComboBox _cbLock = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _numTextLen = Ui.Number(10, 5000, 300);
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true, Width = 160 };

    // Listas
    private AppConfig _edit = new();
    private bool _listsDirty;
    private bool _loadingList;
    private readonly ListBox _lbLists = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _txtName = new() { Width = 320 };
    private readonly TextBox _txtSites = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly TextBox _txtApps = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly Label _lblListLocked = Ui.Label("");

    // Agenda
    private readonly ListView _lvSchedules = Ui.List(("Lista", 200), ("Dias", 200), ("Início", 70), ("Fim", 70), ("Travada", 70), ("Situação", 110));

    // Pomodoro
    private readonly ComboBox _cbPomoList = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _numFocus = Ui.Number(1, 240, 25);
    private readonly NumericUpDown _numBreak = Ui.Number(1, 120, 5);
    private readonly NumericUpDown _numCycles = Ui.Number(1, 20, 4);
    private readonly CheckBox _chkPomoLock = new() { Text = "Trava de tempo (não dá para parar no meio)", AutoSize = true };
    private readonly Label _lblPomo = Ui.Label("Nenhum Pomodoro em andamento.", bold: true, size: 12f);

    // Estatísticas
    private readonly ListView _lvStats = Ui.List(("Data", 110), ("Minutos de foco", 120), ("Tentativas bloqueadas", 150), ("Mais bloqueado", 260));
    private StatsData? _stats;

    // Configurações
    private readonly CheckBox _chkTools = new() { Text = "Durante bloqueio travado, fechar Gerenciador de Tarefas, Editor do Registro e ferramentas parecidas", AutoSize = true };
    private readonly CheckBox _chkDoh = new() { Text = "Desligar DNS sobre HTTPS no Chrome, Edge, Brave e Firefox (necessário para bloquear sites)", AutoSize = true };
    private readonly CheckBox _chkUninstall = new() { Text = "Durante bloqueio travado, impedir desinstalar o FocoTotal e parar o serviço", AutoSize = true };
    private readonly Label _lblVersion = Ui.Label("");

    public MainForm(bool startInTray)
    {
        Text = "FocoTotal";
        Icon = SystemIcons.Shield;
        MinimumSize = new Size(820, 560);
        Size = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        tabs.TabPages.Add(BuildBlockTab());
        tabs.TabPages.Add(BuildListsTab());
        tabs.TabPages.Add(BuildScheduleTab());
        tabs.TabPages.Add(BuildPomodoroTab());
        tabs.TabPages.Add(BuildStatsTab());
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.SelectedIndexChanged += async (_, _) => { if (tabs.SelectedIndex == 4) await LoadStatsAsync(); };
        Controls.Add(tabs);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir FocoTotal", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sair", null, (_, _) => { _exiting = true; Close(); });
        _tray = new NotifyIcon { Icon = SystemIcons.Shield, Text = "FocoTotal", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _allowShow = !startInTray;
        HandleCreated += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await RefreshAsync();
            _timer.Start();
        };
    }

    private bool _allowShow;
    private bool _started;

    protected override void SetVisibleCore(bool value)
    {
        // Com --bandeja, a janela só aparece quando o usuário abre pelo ícone.
        if (!_allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmShowMe) ShowFromTray();
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, "FocoTotal", "O FocoTotal continua protegendo em segundo plano.", ToolTipIcon.Info);
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ShowFirstRunNotice();
    }

    /// <summary>Na primeira abertura, avisa o que o FocoTotal altera no sistema.</summary>
    private void ShowFirstRunNotice()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\FocoTotal");
            if (key.GetValue("AvisoSistemaMostrado") is int v && v == 1) return;
            MessageBox.Show(this,
                "Para bloquear o que você escolher, o FocoTotal faz estas alterações no Windows:\r\n\r\n" +
                "• Escreve um bloco no arquivo hosts enquanto há sites bloqueados (removido ao terminar).\r\n" +
                "• Desliga o DNS sobre HTTPS dos navegadores por política, se ainda não houver política (desativável em Configurações).\r\n" +
                "• Durante bloqueio travado: fecha os apps da lista e ferramentas como o Gerenciador de Tarefas, " +
                "impede parar o serviço e recusa a desinstalação até o bloqueio acabar.\r\n\r\n" +
                "Nada é enviado pela internet. Para remover: Configurações > Aplicativos > FocoTotal > Desinstalar.",
                "Bem-vindo ao FocoTotal", MessageBoxButtons.OK, MessageBoxIcon.Information);
            key.SetValue("AvisoSistemaMostrado", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch
        {
            // Sem acesso ao registro do usuário: só não mostra o aviso.
        }
    }

    private void ShowFromTray()
    {
        _allowShow = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------------- Comunicação com o serviço ----------------

    private async Task<IpcResponse?> SendAsync(IpcRequest req, bool showErrors = true)
    {
        try
        {
            var resp = await _ipc.SendAsync(req);
            if (resp.Status is not null) ApplyStatus(resp.Status);
            if (!resp.Ok && showErrors && resp.StopOutcome is null && !string.IsNullOrEmpty(resp.Message)) Ui.Error(this, resp.Message);
            return resp;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _lblStatus.Text = "O serviço do FocoTotal não está rodando. Reinstale ou inicie o serviço \"FocoTotal\".";
            _lblStatus.ForeColor = Color.Firebrick;
            if (showErrors && req.Command != IpcCommands.GetStatus) Ui.Error(this, "Não foi possível falar com o serviço do FocoTotal.");
            return null;
        }
    }

    private async Task RefreshAsync() => await SendAsync(new IpcRequest { Command = IpcCommands.GetStatus }, showErrors: false);

    private void ApplyStatus(StatusInfo s)
    {
        _status = s;
        var names = s.Config.Lists.ToDictionary(l => l.Id, l => l.Name);
        string ListNames(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, "(lista excluída)")));

        // Status geral
        if (s.ActiveListIds.Count == 0)
        {
            _lblStatus.Text = "Nenhum bloqueio ativo.";
            _lblStatus.ForeColor = SystemColors.ControlText;
        }
        else
        {
            _lblStatus.Text = $"Bloqueando: {ListNames(s.ActiveListIds)}" + (s.LockedListIds.Count > 0 ? "  (travado)" : "");
            _lblStatus.ForeColor = Color.SeaGreen;
        }
        _tray.Text = s.ActiveListIds.Count == 0 ? "FocoTotal: sem bloqueio" : "FocoTotal: bloqueando";

        if (_lastBlockedToday >= 0 && s.BlockedToday > _lastBlockedToday)
            _tray.ShowBalloonTip(2500, "FocoTotal", "Um app bloqueado foi fechado. Volte ao foco!", ToolTipIcon.Info);
        _lastBlockedToday = s.BlockedToday;

        // Bloqueios ativos
        var selected = _lvBlocks.SelectedItems.Count > 0 ? (string)_lvBlocks.SelectedItems[0].Tag! : null;
        _lvBlocks.BeginUpdate();
        _lvBlocks.Items.Clear();
        foreach (var b in s.Blocks.OrderBy(b => b.EndUtc))
        {
            var item = new ListViewItem(new[]
            {
                ListNames(b.ListIds), Ui.Remaining(b.EndUtc), b.EndUtc.ToLocalTime().ToString("dd/MM HH:mm"), Ui.LockName(b.Lock), Ui.SourceName(b.Source),
            }) { Tag = b.Id, Selected = b.Id == selected };
            _lvBlocks.Items.Add(item);
        }
        _lvBlocks.EndUpdate();

        // Listas disponíveis para iniciar bloqueio e Pomodoro
        SyncListChoices(s.Config.Lists);

        // Editor de listas: só recarrega se não houver edição pendente.
        if (!_listsDirty) LoadEditor(Ui.Clone(s.Config));

        // Agenda
        var localNow = DateTime.Now;
        _lvSchedules.BeginUpdate();
        _lvSchedules.Items.Clear();
        foreach (var e in s.Config.Schedules)
        {
            var state = !e.Enabled ? "Desativada" : BlockEngine.IsScheduleActive(e, localNow) ? "Bloqueando agora" : "Aguardando";
            _lvSchedules.Items.Add(new ListViewItem(new[]
            {
                names.GetValueOrDefault(e.ListId, "?"), Ui.DaysText(e.Days), e.Start, e.End, e.Locked ? "Sim" : "Não", state,
            }) { Tag = e.Id });
        }
        _lvSchedules.EndUpdate();

        // Pomodoro
        if (s.Pomodoro is { } p)
        {
            var phase = p.Phase == PomodoroPhase.Focus ? "Foco" : "Pausa";
            _lblPomo.Text = $"{phase}: ciclo {p.CurrentCycle} de {p.TotalCycles}, faltam {Ui.Remaining(p.PhaseEndUtc)} ({names.GetValueOrDefault(p.ListId, "?")})";
            _lblPomo.ForeColor = p.Phase == PomodoroPhase.Focus ? Color.SeaGreen : Color.SteelBlue;
        }
        else
        {
            _lblPomo.Text = "Nenhum Pomodoro em andamento.";
            _lblPomo.ForeColor = SystemColors.ControlText;
        }

        // Configurações
        if (!_settingsDirty)
        {
            _chkTools.Checked = s.Config.Settings.ProtectSystemTools;
            _chkDoh.Checked = s.Config.Settings.DisableBrowserDoH;
            _chkUninstall.Checked = s.Config.Settings.ProtectUninstall;
        }
        _lblVersion.Text = $"Serviço versão {s.ServiceVersion}" + (s.ConfigReadOnly ? ". A configuração é gerenciada pelo administrador (somente leitura)." : ".");
    }

    private void SyncListChoices(List<BlockList> lists)
    {
        var ids = lists.Select(l => l.Id).ToList();
        if (_clbStartLists.Tag is List<string> prev && prev.SequenceEqual(ids)
            && lists.Select(l => l.Name).SequenceEqual(_clbStartLists.Items.Cast<string>())) return;

        var checkedIds = _clbStartLists.Tag is List<string> p2
            ? _clbStartLists.CheckedIndices.Cast<int>().Where(i => i < p2.Count).Select(i => p2[i]).ToHashSet()
            : new HashSet<string>();
        _clbStartLists.Items.Clear();
        _cbPomoList.Items.Clear();
        foreach (var l in lists)
        {
            _clbStartLists.Items.Add(l.Name, checkedIds.Contains(l.Id));
            _cbPomoList.Items.Add(l.Name);
        }
        _clbStartLists.Tag = ids;
        if (_cbPomoList.Items.Count > 0) _cbPomoList.SelectedIndex = 0;
    }

    // ---------------- Aba Bloqueio ----------------

    private TabPage BuildBlockTab()
    {
        var page = new TabPage("Bloqueio") { Padding = new Padding(10) };

        _cbLock.Items.AddRange(Ui.LockNames);
        _cbLock.SelectedIndex = 0;
        _cbLock.SelectedIndexChanged += (_, _) => UpdateLockInputs();

        var start = new GroupBox { Text = "Novo bloqueio", Dock = DockStyle.Bottom, Height = 250, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        left.Controls.Add(Ui.Label("Listas a bloquear:"));
        left.Controls.Add(_clbStartLists);
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        right.Controls.Add(Ui.Row(Ui.Label("Duração:"), _numHours, Ui.Label("h"), _numMinutes, Ui.Label("min")));
        right.Controls.Add(Ui.Row(Ui.Label("Trava:"), _cbLock));
        right.Controls.Add(Ui.Row(Ui.Label("Tamanho do texto:"), _numTextLen));
        right.Controls.Add(Ui.Row(Ui.Label("Senha:"), _txtPassword));
        right.Controls.Add(Ui.Row(Ui.Button("Iniciar bloqueio", async (_, _) => await StartBlockAsync())));
        right.Controls.Add(Ui.Label("Trava de tempo: ninguém consegue parar até o fim, nem reiniciando o PC."));
        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        start.Controls.Add(grid);
        UpdateLockInputs();

        var activeBox = new GroupBox { Text = "Bloqueios em andamento", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var stopRow = Ui.Row(Ui.Button("Parar bloqueio selecionado", async (_, _) => await StopBlockAsync()));
        stopRow.Dock = DockStyle.Bottom;
        activeBox.Controls.Add(_lvBlocks);
        activeBox.Controls.Add(stopRow);

        var top = new Panel { Dock = DockStyle.Top, Height = 40 };
        _lblStatus.Dock = DockStyle.Fill;
        top.Controls.Add(_lblStatus);

        page.Controls.Add(activeBox);
        page.Controls.Add(start);
        page.Controls.Add(top);
        return page;
    }

    private void UpdateLockInputs()
    {
        _numTextLen.Enabled = _cbLock.SelectedIndex == (int)LockKind.RandomText;
        _txtPassword.Enabled = _cbLock.SelectedIndex == (int)LockKind.Password;
    }

    private async Task StartBlockAsync()
    {
        if (_status is null) return;
        var ids = (List<string>?)_clbStartLists.Tag ?? new();
        var chosen = _clbStartLists.CheckedIndices.Cast<int>().Where(i => i < ids.Count).Select(i => ids[i]).ToList();
        var minutes = (int)_numHours.Value * 60 + (int)_numMinutes.Value;
        var lockKind = (LockKind)_cbLock.SelectedIndex;

        if (chosen.Count == 0) { Ui.Error(this, "Marque pelo menos uma lista."); return; }
        if (minutes < 1) { Ui.Error(this, "Defina a duração."); return; }
        if (lockKind != LockKind.None)
        {
            var end = DateTime.Now.AddMinutes(minutes);
            var msg = lockKind == LockKind.Timer
                ? $"Este bloqueio NÃO poderá ser parado até {end:dd/MM HH:mm}. Continuar?"
                : $"Este bloqueio vai até {end:dd/MM HH:mm} e só para com a trava escolhida. Continuar?";
            if (MessageBox.Show(this, msg, "Confirmar bloqueio travado", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }

        var resp = await SendAsync(new IpcRequest
        {
            Command = IpcCommands.StartBlock,
            Start = new StartBlockRequest
            {
                ListIds = chosen, DurationMinutes = minutes, Lock = lockKind,
                RandomTextLength = (int)_numTextLen.Value, Password = _txtPassword.Text,
            },
        });
        if (resp?.Ok == true) _txtPassword.Clear();
    }

    private async Task StopBlockAsync()
    {
        if (_lvBlocks.SelectedItems.Count == 0) { Ui.Error(this, "Selecione um bloqueio."); return; }
        var id = (string)_lvBlocks.SelectedItems[0].Tag!;
        var block = _status?.Blocks.FirstOrDefault(b => b.Id == id);
        if (block is null) return;

        string? answer = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var resp = await SendAsync(new IpcRequest { Command = IpcCommands.StopBlock, Stop = new StopBlockRequest { BlockId = id, Answer = answer } });
            if (resp is null) return;
            switch (resp.StopOutcome)
            {
                case StopOutcome.Stopped:
                    Ui.Info(this, resp.Message ?? "Bloqueio encerrado.");
                    return;
                case StopOutcome.Denied:
                    Ui.Error(this, resp.Message ?? "Não é possível parar.");
                    return;
                case StopOutcome.ChallengeIssued:
                case StopOutcome.WrongAnswer:
                    using (var dlg = new ChallengeDialog(resp.Message ?? "", resp.Challenge, block.Lock == LockKind.Password))
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        answer = dlg.Answer;
                    }
                    break;
                default:
                    if (!string.IsNullOrEmpty(resp.Message)) Ui.Error(this, resp.Message);
                    return;
            }
        }
    }

    // ---------------- Aba Listas ----------------

    private TabPage BuildListsTab()
    {
        var page = new TabPage("Listas") { Padding = new Padding(10) };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220, FixedPanel = FixedPanel.Panel1 };

        var leftButtons = Ui.Row(
            Ui.Button("Nova", (_, _) => AddList()),
            Ui.Button("Excluir", (_, _) => DeleteList()));
        leftButtons.Dock = DockStyle.Bottom;
        split.Panel1.Controls.Add(_lbLists);
        split.Panel1.Controls.Add(leftButtons);
        _lbLists.SelectedIndexChanged += (_, _) => ShowSelectedList();

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(8, 0, 0, 0) };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var nameRow = Ui.Row(Ui.Label("Nome:"), _txtName);
        editor.Controls.Add(nameRow, 0, 0);
        editor.SetColumnSpan(nameRow, 2);
        editor.Controls.Add(Ui.Label("Sites (um por linha, ex.: youtube.com):"), 0, 1);
        editor.Controls.Add(Ui.Label("Apps (ex.: steam.exe ou C:\\Jogos\\*):"), 1, 1);
        editor.Controls.Add(_txtSites, 0, 2);
        editor.Controls.Add(_txtApps, 1, 2);
        editor.Controls.Add(Ui.Row(
            Ui.Button("Adicionar app aberto…", (_, _) => PickRunningApp()),
            Ui.Button("Procurar .exe…", (_, _) => BrowseExe())), 1, 3);
        var saveRow = Ui.Row(Ui.Button("Salvar listas", async (_, _) => await SaveListsAsync()),
            Ui.Button("Descartar alterações", (_, _) => { _listsDirty = false; if (_status is not null) LoadEditor(Ui.Clone(_status.Config)); }),
            _lblListLocked);
        editor.Controls.Add(saveRow, 0, 4);
        editor.SetColumnSpan(saveRow, 2);
        split.Panel2.Controls.Add(editor);

        _txtName.TextChanged += (_, _) => EditorChanged();
        _txtSites.TextChanged += (_, _) => EditorChanged();
        _txtApps.TextChanged += (_, _) => EditorChanged();

        page.Controls.Add(split);
        return page;
    }

    private BlockList? CurrentList => _lbLists.SelectedIndex >= 0 && _lbLists.SelectedIndex < _edit.Lists.Count ? _edit.Lists[_lbLists.SelectedIndex] : null;

    private void LoadEditor(AppConfig config)
    {
        var selectedId = CurrentList?.Id;
        _edit = config;
        _loadingList = true;
        _lbLists.BeginUpdate();
        _lbLists.Items.Clear();
        foreach (var l in _edit.Lists) _lbLists.Items.Add(l.Name);
        _lbLists.EndUpdate();
        var idx = _edit.Lists.FindIndex(l => l.Id == selectedId);
        _lbLists.SelectedIndex = idx >= 0 ? idx : (_edit.Lists.Count > 0 ? 0 : -1);
        _loadingList = false;
        ShowSelectedList();
    }

    private void ShowSelectedList()
    {
        var l = CurrentList;
        _loadingList = true;
        _txtName.Text = l?.Name ?? "";
        _txtSites.Text = l is null ? "" : string.Join("\r\n", l.Sites);
        _txtApps.Text = l is null ? "" : string.Join("\r\n", l.Apps);
        _txtName.Enabled = _txtSites.Enabled = _txtApps.Enabled = l is not null;
        _loadingList = false;
        var locked = l is not null && _status?.LockedListIds.Contains(l.Id) == true;
        _lblListLocked.Text = locked ? "Lista em bloqueio travado: você pode adicionar itens, mas não remover." : "";
        _lblListLocked.ForeColor = Color.DarkOrange;
    }

    private void EditorChanged()
    {
        if (_loadingList || CurrentList is not { } l) return;
        _listsDirty = true;
        l.Name = _txtName.Text;
        l.Sites = SplitLines(_txtSites.Text);
        l.Apps = SplitLines(_txtApps.Text);
        if (_lbLists.SelectedIndex >= 0 && (string)_lbLists.Items[_lbLists.SelectedIndex] != l.Name)
        {
            _loadingList = true;
            _lbLists.Items[_lbLists.SelectedIndex] = l.Name;
            _loadingList = false;
        }
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void AddList()
    {
        var l = new BlockList { Name = "Nova lista" };
        _edit.Lists.Add(l);
        _listsDirty = true;
        _lbLists.Items.Add(l.Name);
        _lbLists.SelectedIndex = _lbLists.Items.Count - 1;
        _txtName.Focus();
        _txtName.SelectAll();
    }

    private void DeleteList()
    {
        if (CurrentList is not { } l) return;
        if (MessageBox.Show(this, $"Excluir a lista \"{l.Name}\"? As agendas dela também serão removidas.", "FocoTotal",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _edit.Lists.Remove(l);
        _edit.Schedules.RemoveAll(s => s.ListId == l.Id);
        _listsDirty = true;
        LoadEditor(_edit);
    }

    private void AppendApps(IEnumerable<string> apps)
    {
        if (CurrentList is null) return;
        var lines = SplitLines(_txtApps.Text);
        foreach (var a in apps) if (!lines.Contains(a, StringComparer.OrdinalIgnoreCase)) lines.Add(a);
        _txtApps.Text = string.Join("\r\n", lines);
    }

    private void PickRunningApp()
    {
        if (CurrentList is null) { Ui.Error(this, "Selecione ou crie uma lista."); return; }
        using var dlg = new ProcessPickerDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK) AppendApps(dlg.Selected);
    }

    private void BrowseExe()
    {
        if (CurrentList is null) { Ui.Error(this, "Selecione ou crie uma lista."); return; }
        using var dlg = new OpenFileDialog { Filter = "Programas (*.exe)|*.exe", Multiselect = true, Title = "Escolha os programas a bloquear" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AppendApps(dlg.FileNames.Select(Path.GetFileName)!);
    }

    private async Task SaveListsAsync()
    {
        if (_status is null) return;
        // Envia as listas editadas com agenda e configurações atuais do serviço.
        var config = Ui.Clone(_status.Config);
        config.Lists = Ui.Clone(_edit.Lists);
        config.Schedules = config.Schedules.Where(s => config.Lists.Any(l => l.Id == s.ListId)).ToList();
        var resp = await SendAsync(new IpcRequest { Command = IpcCommands.SaveConfig, Config = config });
        if (resp?.Ok == true)
        {
            _listsDirty = false;
            if (resp.Status is not null) LoadEditor(Ui.Clone(resp.Status.Config));
        }
    }

    // ---------------- Aba Agenda ----------------

    private TabPage BuildScheduleTab()
    {
        var page = new TabPage("Agenda") { Padding = new Padding(10) };
        var buttons = Ui.Row(
            Ui.Button("Adicionar", async (_, _) => await EditScheduleAsync(null)),
            Ui.Button("Editar", async (_, _) => await EditScheduleAsync(SelectedScheduleId())),
            Ui.Button("Excluir", async (_, _) => await DeleteScheduleAsync()));
        buttons.Dock = DockStyle.Bottom;
        var help = Ui.Label("A agenda bloqueia a lista toda semana nos dias e horários escolhidos. Agenda travada não pode ser alterada durante o horário ativo.");
        help.Dock = DockStyle.Top;
        _lvSchedules.DoubleClick += async (_, _) => await EditScheduleAsync(SelectedScheduleId());
        page.Controls.Add(_lvSchedules);
        page.Controls.Add(buttons);
        page.Controls.Add(help);
        return page;
    }

    private string? SelectedScheduleId() => _lvSchedules.SelectedItems.Count > 0 ? (string)_lvSchedules.SelectedItems[0].Tag! : null;

    private async Task EditScheduleAsync(string? id)
    {
        if (_status is null) return;
        var config = Ui.Clone(_status.Config);
        if (config.Lists.Count == 0) { Ui.Error(this, "Crie uma lista primeiro."); return; }
        var existing = id is null ? null : config.Schedules.FirstOrDefault(s => s.Id == id);
        if (id is not null && existing is null) return;

        using var dlg = new ScheduleDialog(config.Lists, existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var idx = config.Schedules.FindIndex(s => s.Id == dlg.Entry.Id);
        if (idx >= 0) config.Schedules[idx] = dlg.Entry; else config.Schedules.Add(dlg.Entry);
        await SendAsync(new IpcRequest { Command = IpcCommands.SaveConfig, Config = config });
    }

    private async Task DeleteScheduleAsync()
    {
        if (_status is null || SelectedScheduleId() is not { } id) return;
        if (MessageBox.Show(this, "Excluir esta agenda?", "FocoTotal", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var config = Ui.Clone(_status.Config);
        config.Schedules.RemoveAll(s => s.Id == id);
        await SendAsync(new IpcRequest { Command = IpcCommands.SaveConfig, Config = config });
    }

    // ---------------- Aba Pomodoro ----------------

    private TabPage BuildPomodoroTab()
    {
        var page = new TabPage("Pomodoro") { Padding = new Padding(10) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(_lblPomo);
        layout.Controls.Add(Ui.Row(Ui.Label("Lista bloqueada durante o foco:"), _cbPomoList));
        layout.Controls.Add(Ui.Row(Ui.Label("Foco:"), _numFocus, Ui.Label("min   Pausa:"), _numBreak, Ui.Label("min   Ciclos:"), _numCycles));
        layout.Controls.Add(_chkPomoLock);
        layout.Controls.Add(Ui.Row(
            Ui.Button("Iniciar Pomodoro", async (_, _) => await StartPomodoroAsync()),
            Ui.Button("Parar", async (_, _) => await SendAsync(new IpcRequest { Command = IpcCommands.StopPomodoro }))));
        layout.Controls.Add(Ui.Label("Durante o foco a lista fica bloqueada. Na pausa, tudo é liberado até o próximo ciclo."));
        page.Controls.Add(layout);
        return page;
    }

    private async Task StartPomodoroAsync()
    {
        var ids = (List<string>?)_clbStartLists.Tag;
        if (ids is null || _cbPomoList.SelectedIndex < 0 || _cbPomoList.SelectedIndex >= ids.Count) { Ui.Error(this, "Escolha uma lista."); return; }
        await SendAsync(new IpcRequest
        {
            Command = IpcCommands.StartPomodoro,
            Pomodoro = new StartPomodoroRequest
            {
                ListId = ids[_cbPomoList.SelectedIndex],
                FocusMinutes = (int)_numFocus.Value,
                BreakMinutes = (int)_numBreak.Value,
                Cycles = (int)_numCycles.Value,
                Lock = _chkPomoLock.Checked ? LockKind.Timer : LockKind.None,
            },
        });
    }

    // ---------------- Aba Estatísticas ----------------

    private TabPage BuildStatsTab()
    {
        var page = new TabPage("Estatísticas") { Padding = new Padding(10) };
        var buttons = Ui.Row(
            Ui.Button("Atualizar", async (_, _) => await LoadStatsAsync()),
            Ui.Button("Exportar JSON…", (_, _) => ExportStats()));
        buttons.Dock = DockStyle.Bottom;
        var help = Ui.Label("Os dados ficam só neste computador. Mostrando os últimos 30 dias.");
        help.Dock = DockStyle.Top;
        page.Controls.Add(_lvStats);
        page.Controls.Add(buttons);
        page.Controls.Add(help);
        return page;
    }

    private async Task LoadStatsAsync()
    {
        var resp = await SendAsync(new IpcRequest { Command = IpcCommands.GetStats }, showErrors: false);
        if (resp?.Stats is null) return;
        _stats = resp.Stats;
        _lvStats.BeginUpdate();
        _lvStats.Items.Clear();
        foreach (var d in _stats.Days.OrderByDescending(d => d.Date).Take(30))
        {
            var top = d.BlockedApps.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var date = DateTime.TryParse(d.Date, out var dt) ? dt.ToString("dd/MM/yyyy") : d.Date;
            _lvStats.Items.Add(new ListViewItem(new[]
            {
                date, d.FocusMinutes.ToString(), d.BlockedApps.Values.Sum().ToString(), top.Key is null ? "" : $"{top.Key} ({top.Value})",
            }));
        }
        _lvStats.EndUpdate();
    }

    private void ExportStats()
    {
        if (_stats is null) { Ui.Error(this, "Clique em Atualizar primeiro."); return; }
        using var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"focototal-estatisticas-{DateTime.Now:yyyyMMdd}.json" };
        if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_stats, Json.Pretty));
    }

    // ---------------- Aba Configurações ----------------

    private bool _settingsDirty;

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Configurações") { Padding = new Padding(10) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var c in new[] { _chkTools, _chkDoh, _chkUninstall }) c.CheckedChanged += (_, _) => _settingsDirty = true;
        layout.Controls.Add(_chkDoh);
        layout.Controls.Add(_chkTools);
        layout.Controls.Add(_chkUninstall);
        layout.Controls.Add(Ui.Row(Ui.Button("Salvar configurações", async (_, _) => await SaveSettingsAsync())));
        layout.Controls.Add(_lblVersion);
        layout.Controls.Add(Ui.Label(
            "O bloqueio de sites usa o arquivo hosts do Windows e vale para todos os navegadores.\r\n" +
            "Bloqueio por endereço completo, palavra-chave e Shorts virá com a extensão de navegador."));
        page.Controls.Add(layout);
        return page;
    }

    private async Task SaveSettingsAsync()
    {
        if (_status is null) return;
        var config = Ui.Clone(_status.Config);
        config.Settings.ProtectSystemTools = _chkTools.Checked;
        config.Settings.DisableBrowserDoH = _chkDoh.Checked;
        config.Settings.ProtectUninstall = _chkUninstall.Checked;
        var resp = await SendAsync(new IpcRequest { Command = IpcCommands.SaveConfig, Config = config });
        if (resp?.Ok == true) { _settingsDirty = false; Ui.Info(this, "Configurações salvas."); }
        else { _settingsDirty = false; await RefreshAsync(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
