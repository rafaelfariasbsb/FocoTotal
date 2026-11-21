using FocoTotal.Core;
using Microsoft.Win32;

namespace FocoTotal.Service;

/// <summary>Laço principal: calcula o que deve estar bloqueado a cada segundo e aplica.</summary>
public sealed class Coordinator : BackgroundService
{
    private readonly ILogger<Coordinator> _log;
    private readonly Storage _storage;
    private readonly HostsEnforcer _hosts;
    private readonly ProcessEnforcer _processes;
    private readonly BrowserPolicyEnforcer _browser;
    private readonly TamperProtection _tamper;

    private readonly object _gate = new();
    private readonly ClockGuard _clock = new();
    private AppConfig _config = new();
    private ServiceState _state = new();
    private StatsData _stats = new();
    private EffectivePolicy _policy = new();
    private int _focusSeconds;
    private DateTime _lastStateSave = DateTime.MinValue;
    private DateTime _lastStatsSave = DateTime.MinValue;
    private bool _statsDirty;

    public Coordinator(ILogger<Coordinator> log, Storage storage, HostsEnforcer hosts, ProcessEnforcer processes,
        BrowserPolicyEnforcer browser, TamperProtection tamper)
    {
        _log = log; _storage = storage; _hosts = hosts; _processes = processes; _browser = browser; _tamper = tamper;
    }

    private static TimeZoneInfo Tz => TimeZoneInfo.Local;
    private static DateTime LocalNow => DateTime.Now;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _config = _storage.LoadConfig();
            _state = _storage.LoadState();
            _stats = _storage.LoadStats();
            _storage.SaveConfig(_config);
        }
        _log.LogInformation("FocoTotal {Version} iniciado", ServiceInfo.Version);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try { Step(); }
            catch (Exception ex) { _log.LogError(ex, "Erro no laço principal"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Step()
    {
        EffectivePolicy policy;
        Settings settings;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var changed = ClockGuard.Compensate(_state, _clock.Observe(now, Environment.TickCount64));
            changed |= BlockEngine.Tick(_state, now);
            _state.LastSeenUtc = now;
            _policy = policy = BlockEngine.Compute(_config, _state, now, Tz);
            settings = _config.Settings;

            if (changed || DateTime.UtcNow - _lastStateSave > TimeSpan.FromSeconds(30))
            {
                _storage.SaveState(_state);
                _lastStateSave = DateTime.UtcNow;
            }
        }

        _hosts.Apply(policy.Sites);
        var killed = _processes.Enforce(policy.Apps, settings.ProtectSystemTools && policy.AnyLocked);
        _browser.Apply(settings.DisableBrowserDoH);
        _tamper.Apply(settings.ProtectUninstall && policy.AnyLocked);

        lock (_gate)
        {
            foreach (var name in killed) { StatsTracker.RecordBlockedApp(_stats, LocalNow, name); _statsDirty = true; }
            if (policy.AnyActive && ++_focusSeconds >= 60)
            {
                _focusSeconds = 0;
                StatsTracker.AddFocusMinute(_stats, LocalNow);
                _statsDirty = true;
            }
            if (_statsDirty && DateTime.UtcNow - _lastStatsSave > TimeSpan.FromSeconds(60))
            {
                _storage.SaveStats(_stats);
                _lastStatsSave = DateTime.UtcNow;
                _statsDirty = false;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        bool locked;
        lock (_gate)
        {
            _storage.SaveState(_state);
            _storage.SaveStats(_stats);
            locked = _policy.AnyLocked;
        }

        // Com bloqueio travado, nada é desfeito: o hosts continua valendo mesmo sem o serviço.
        if (!locked)
        {
            _hosts.Clear();
            _browser.Apply(false);
            _tamper.Apply(false);
        }
    }

    private static bool AdminOnlyPolicy()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ServiceInfo.PolicyKey);
            return key?.GetValue("AdminOnly") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Atende um pedido do app. callerIsAdmin indica um administrador com token elevado.</summary>
    public IpcResponse Handle(IpcRequest req, bool callerIsAdmin)
    {
        var readOnly = AdminOnlyPolicy() && !callerIsAdmin;
        lock (_gate)
        {
            // Copia a resposta ainda dentro da trava: ela será serializada fora dela.
            var response = HandleCore(req, readOnly);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, Json.Options);
            return System.Text.Json.JsonSerializer.Deserialize<IpcResponse>(bytes, Json.Options)!;
        }
    }

    private IpcResponse HandleCore(IpcRequest req, bool readOnly)
    {
        {
            switch (req.Command)
            {
                case IpcCommands.GetStatus:
                    return new IpcResponse { Ok = true, Status = BuildStatus(readOnly) };

                case IpcCommands.GetStats:
                    return new IpcResponse { Ok = true, Stats = _stats };

                case IpcCommands.SaveConfig:
                {
                    if (readOnly) return IpcResponse.Fail("A configuração é gerenciada pelo administrador.");
                    if (req.Config is null) return IpcResponse.Fail("Configuração ausente.");
                    var error = Sanitize(req.Config) ?? BlockEngine.ValidateConfigChange(_config, req.Config, _policy, LocalNow);
                    if (error is not null) return IpcResponse.Fail(error);
                    _config = req.Config;
                    _storage.SaveConfig(_config);
                    _policy = BlockEngine.Compute(_config, _state, DateTimeOffset.UtcNow, Tz);
                    return new IpcResponse { Ok = true, Message = "Configuração salva.", Status = BuildStatus(readOnly) };
                }

                case IpcCommands.StartBlock:
                {
                    var s = req.Start;
                    if (s is null) return IpcResponse.Fail("Pedido inválido.");
                    var error = BlockEngine.ValidateStart(_config, s);
                    if (error is not null) return IpcResponse.Fail(error);
                    var now = DateTimeOffset.UtcNow;
                    _state.Blocks.Add(new ActiveBlock
                    {
                        ListIds = s.ListIds.Distinct().ToList(),
                        StartUtc = now,
                        EndUtc = now.AddMinutes(s.DurationMinutes),
                        Lock = s.Lock,
                        Source = BlockSource.Manual,
                        RandomTextLength = s.RandomTextLength,
                        PasswordHash = s.Lock == LockKind.Password ? PasswordHasher.Hash(s.Password!) : null,
                    });
                    _storage.SaveState(_state);
                    _policy = BlockEngine.Compute(_config, _state, now, Tz);
                    return new IpcResponse { Ok = true, Message = "Bloqueio iniciado.", Status = BuildStatus(readOnly) };
                }

                case IpcCommands.StopBlock:
                {
                    var block = _state.Blocks.FirstOrDefault(b => b.Id == req.Stop?.BlockId);
                    if (block is null) return IpcResponse.Fail("Bloqueio não encontrado.");
                    if (readOnly && block.Lock != LockKind.None) return IpcResponse.Fail("Somente o administrador pode encerrar este bloqueio.");
                    var result = LockPolicy.TryStop(block, req.Stop?.Answer);
                    if (result.Outcome == StopOutcome.Stopped) _state.Blocks.Remove(block);
                    _storage.SaveState(_state);
                    _policy = BlockEngine.Compute(_config, _state, DateTimeOffset.UtcNow, Tz);
                    return new IpcResponse
                    {
                        Ok = result.Outcome == StopOutcome.Stopped,
                        Message = result.Message,
                        StopOutcome = result.Outcome,
                        Challenge = result.Challenge,
                        Status = BuildStatus(readOnly),
                    };
                }

                case IpcCommands.StartPomodoro:
                {
                    var p = req.Pomodoro;
                    if (p is null || _config.Lists.All(l => l.Id != p.ListId)) return IpcResponse.Fail("Escolha uma lista.");
                    if (_state.Pomodoro is not null) return IpcResponse.Fail("Já existe um Pomodoro em andamento.");
                    if (p.FocusMinutes is < 1 or > 240 || p.BreakMinutes is < 1 or > 120 || p.Cycles is < 1 or > 20)
                        return IpcResponse.Fail("Valores de Pomodoro fora do limite.");
                    if (p.Lock is LockKind.RandomText or LockKind.Password)
                        return IpcResponse.Fail("No Pomodoro, use \"Sem trava\" ou \"Trava de tempo\".");
                    _state.Pomodoro = new PomodoroState
                    {
                        ListId = p.ListId, FocusMinutes = p.FocusMinutes, BreakMinutes = p.BreakMinutes,
                        TotalCycles = p.Cycles, CurrentCycle = 1, Phase = PomodoroPhase.Focus,
                        PhaseEndUtc = DateTimeOffset.UtcNow.AddMinutes(p.FocusMinutes), Lock = p.Lock,
                    };
                    _storage.SaveState(_state);
                    _policy = BlockEngine.Compute(_config, _state, DateTimeOffset.UtcNow, Tz);
                    return new IpcResponse { Ok = true, Message = "Pomodoro iniciado.", Status = BuildStatus(readOnly) };
                }

                case IpcCommands.StopPomodoro:
                {
                    if (_state.Pomodoro is null) return IpcResponse.Fail("Nenhum Pomodoro em andamento.");
                    if (_state.Pomodoro.Lock != LockKind.None) return IpcResponse.Fail("Este Pomodoro tem trava de tempo e vai até o fim.");
                    _state.Pomodoro = null;
                    _storage.SaveState(_state);
                    _policy = BlockEngine.Compute(_config, _state, DateTimeOffset.UtcNow, Tz);
                    return new IpcResponse { Ok = true, Message = "Pomodoro encerrado.", Status = BuildStatus(readOnly) };
                }

                default:
                    return IpcResponse.Fail("Comando desconhecido.");
            }
        }
    }

    private StatusInfo BuildStatus(bool readOnly) => new()
    {
        Config = _config,
        Blocks = _state.Blocks.Select(ActiveBlockInfo.From).ToList(),
        Pomodoro = _state.Pomodoro,
        ActiveListIds = _policy.ActiveListIds.ToList(),
        LockedListIds = _policy.LockedListIds.ToList(),
        ConfigReadOnly = readOnly,
        ServiceVersion = ServiceInfo.Version,
        BlockedToday = StatsTracker.Today(_stats, LocalNow).BlockedApps.Values.Sum(),
    };

    /// <summary>Limpa e valida a configuração recebida. Retorna erro ou null.</summary>
    private static string? Sanitize(AppConfig c)
    {
        c.Lists ??= new(); c.Schedules ??= new(); c.Settings ??= new();
        if (c.Lists.Count > 200 || c.Schedules.Count > 200) return "Limite de listas ou agendas excedido.";
        foreach (var l in c.Lists)
        {
            if (string.IsNullOrWhiteSpace(l.Id)) l.Id = Guid.NewGuid().ToString("N");
            l.Name = string.IsNullOrWhiteSpace(l.Name) ? "Sem nome" : l.Name.Trim();
            l.Sites = (l.Sites ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            l.Apps = (l.Apps ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bad = l.Sites.FirstOrDefault(s => DomainRules.Normalize(s) is null);
            if (bad is not null) return $"\"{bad}\" não é um site válido (lista {l.Name}).";
        }
        if (c.Lists.Select(l => l.Id).Distinct().Count() != c.Lists.Count) return "Há listas com o mesmo identificador.";
        foreach (var s in c.Schedules)
        {
            if (c.Lists.All(l => l.Id != s.ListId)) return "Uma agenda aponta para uma lista que não existe.";
            if (!BlockEngine.TryParseTime(s.Start, out _) || !BlockEngine.TryParseTime(s.End, out _)) return "Horário de agenda inválido. Use HH:mm.";
        }
        return null;
    }
}
