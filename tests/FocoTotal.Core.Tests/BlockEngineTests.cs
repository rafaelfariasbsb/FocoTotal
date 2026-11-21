using FocoTotal.Core;

namespace FocoTotal.Core.Tests;

public class BlockEngineTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero); // segunda-feira

    private static AppConfig Config() => new()
    {
        Lists =
        {
            new BlockList { Id = "a", Name = "A", Sites = { "youtube.com" }, Apps = { "steam.exe" } },
            new BlockList { Id = "b", Name = "B", Sites = { "x.com" } },
        },
    };

    [Theory]
    [InlineData("09:00", "18:00", 10, 0, true)]
    [InlineData("09:00", "18:00", 18, 0, false)]
    [InlineData("22:00", "06:00", 23, 0, true)]   // segunda 23h, agenda de segunda
    [InlineData("22:00", "06:00", 5, 0, false)]   // segunda 5h: agenda começou no domingo, que não está marcado
    public void Agenda_respeita_horario(string start, string end, int h, int m, bool expected)
    {
        var e = new ScheduleEntry { ListId = "a", Days = { DayOfWeek.Monday }, Start = start, End = end };
        Assert.Equal(expected, BlockEngine.IsScheduleActive(e, new DateTime(2026, 11, 23, h, m, 0)));
    }

    [Fact]
    public void Agenda_noturna_continua_no_dia_seguinte()
    {
        var e = new ScheduleEntry { ListId = "a", Days = { DayOfWeek.Monday }, Start = "22:00", End = "06:00" };
        Assert.True(BlockEngine.IsScheduleActive(e, new DateTime(2026, 11, 24, 5, 0, 0))); // terça 5h
    }

    [Fact]
    public void Compute_junta_bloqueios_e_marca_travados()
    {
        var state = new ServiceState
        {
            Blocks =
            {
                new ActiveBlock { ListIds = { "a" }, StartUtc = Now.AddMinutes(-5), EndUtc = Now.AddMinutes(5), Lock = LockKind.Timer },
                new ActiveBlock { ListIds = { "b" }, StartUtc = Now.AddMinutes(-5), EndUtc = Now.AddMinutes(-1) },
            },
        };
        var p = BlockEngine.Compute(Config(), state, Now, Utc);
        Assert.Equal(new[] { "a" }, p.ActiveListIds);
        Assert.True(p.AnyLocked);
        Assert.Contains("youtube.com", p.Sites);
        Assert.Contains("steam.exe", p.Apps);
        Assert.DoesNotContain("x.com", p.Sites);
    }

    [Fact]
    public void Tick_remove_vencidos_e_avanca_pomodoro()
    {
        var state = new ServiceState
        {
            Blocks = { new ActiveBlock { ListIds = { "a" }, StartUtc = Now.AddHours(-1), EndUtc = Now.AddMinutes(-1) } },
            Pomodoro = new PomodoroState { ListId = "a", FocusMinutes = 25, BreakMinutes = 5, TotalCycles = 2, Phase = PomodoroPhase.Focus, PhaseEndUtc = Now.AddSeconds(-1) },
        };
        Assert.True(BlockEngine.Tick(state, Now));
        Assert.Empty(state.Blocks);
        Assert.Equal(PomodoroPhase.Break, state.Pomodoro!.Phase);
        Assert.False(BlockEngine.Compute(Config(), state, Now, Utc).AnyActive);

        BlockEngine.Tick(state, Now.AddMinutes(5));
        Assert.Equal(PomodoroPhase.Focus, state.Pomodoro!.Phase);
        Assert.Equal(2, state.Pomodoro.CurrentCycle);

        BlockEngine.Tick(state, Now.AddMinutes(31));
        Assert.Null(state.Pomodoro);
    }

    [Fact]
    public void Lista_travada_aceita_adicionar_mas_nao_remover()
    {
        var current = Config();
        var state = new ServiceState { Blocks = { new ActiveBlock { ListIds = { "a" }, StartUtc = Now, EndUtc = Now.AddHours(1), Lock = LockKind.Timer } } };
        var policy = BlockEngine.Compute(current, state, Now, Utc);

        var added = Config();
        added.Lists[0].Sites.Add("twitch.tv");
        Assert.Null(BlockEngine.ValidateConfigChange(current, added, policy, Now.DateTime));

        var sameDifferentForm = Config();
        sameDifferentForm.Lists[0].Sites[0] = "https://www.youtube.com/";
        Assert.Null(BlockEngine.ValidateConfigChange(current, sameDifferentForm, policy, Now.DateTime));

        var removed = Config();
        removed.Lists[0].Sites.Clear();
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, removed, policy, Now.DateTime));

        var removedApp = Config();
        removedApp.Lists[0].Apps.Clear();
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, removedApp, policy, Now.DateTime));

        var deleted = Config();
        deleted.Lists.RemoveAt(0);
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, deleted, policy, Now.DateTime));

        var otherList = Config();
        otherList.Lists[1].Sites.Clear();
        Assert.Null(BlockEngine.ValidateConfigChange(current, otherList, policy, Now.DateTime));

        var protectionOff = Config();
        protectionOff.Settings.ProtectSystemTools = false;
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, protectionOff, policy, Now.DateTime));
    }

    [Fact]
    public void Agenda_travada_ativa_nao_pode_ser_alterada()
    {
        var current = Config();
        current.Schedules.Add(new ScheduleEntry { Id = "s", ListId = "a", Days = { DayOfWeek.Monday }, Start = "09:00", End = "18:00", Locked = true });
        var policy = BlockEngine.Compute(current, new ServiceState(), Now, Utc);
        Assert.Contains("a", policy.LockedListIds);

        var changed = Config();
        changed.Schedules.Add(new ScheduleEntry { Id = "s", ListId = "a", Days = { DayOfWeek.Monday }, Start = "09:00", End = "09:30", Locked = true });
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, changed, policy, Now.DateTime));

        var removed = Config();
        Assert.NotNull(BlockEngine.ValidateConfigChange(current, removed, policy, Now.DateTime));

        // Fora do horário, pode alterar.
        var evening = new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero);
        var policyEvening = BlockEngine.Compute(current, new ServiceState(), evening, Utc);
        Assert.Null(BlockEngine.ValidateConfigChange(current, removed, policyEvening, evening.DateTime));
    }

    [Fact]
    public void ValidateStart_checa_entrada()
    {
        var c = Config();
        Assert.NotNull(BlockEngine.ValidateStart(c, new StartBlockRequest { DurationMinutes = 10 }));
        Assert.NotNull(BlockEngine.ValidateStart(c, new StartBlockRequest { ListIds = { "zzz" }, DurationMinutes = 10 }));
        Assert.NotNull(BlockEngine.ValidateStart(c, new StartBlockRequest { ListIds = { "a" }, DurationMinutes = 0 }));
        Assert.NotNull(BlockEngine.ValidateStart(c, new StartBlockRequest { ListIds = { "a" }, DurationMinutes = 10, Lock = LockKind.Password }));
        Assert.Null(BlockEngine.ValidateStart(c, new StartBlockRequest { ListIds = { "a" }, DurationMinutes = 10, Lock = LockKind.RandomText, RandomTextLength = 50 }));
    }
}
