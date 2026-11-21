using System.Text.Json.Serialization;

namespace FocoTotal.Core;

/// <summary>Uma lista de bloqueio: sites (domínios) e aplicativos.</summary>
public sealed class BlockList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nova lista";

    /// <summary>Domínios ou URLs. Ex.: "youtube.com", "*.reddit.com", "https://x.com/home".</summary>
    public List<string> Sites { get; set; } = new();

    /// <summary>Aplicativos: nome do executável ("steam.exe"), caminho completo ou pasta com curinga ("C:\Games\*").</summary>
    public List<string> Apps { get; set; } = new();
}

public enum LockKind
{
    /// <summary>Pode ser parado a qualquer momento.</summary>
    None = 0,
    /// <summary>Não pode ser parado até o fim.</summary>
    Timer = 1,
    /// <summary>Para parar, é preciso digitar um texto aleatório.</summary>
    RandomText = 2,
    /// <summary>Para parar, é preciso digitar a senha definida no início.</summary>
    Password = 3,
}

public enum BlockSource
{
    Manual = 0,
    Schedule = 1,
    Pomodoro = 2,
}

/// <summary>Um bloqueio em andamento.</summary>
public sealed class ActiveBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> ListIds { get; set; } = new();
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public LockKind Lock { get; set; }
    public BlockSource Source { get; set; }

    /// <summary>Tamanho do texto aleatório (LockKind.RandomText).</summary>
    public int RandomTextLength { get; set; }

    /// <summary>Hash PBKDF2 da senha (LockKind.Password).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Desafio de texto atual, gerado pelo serviço quando alguém pede para parar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingChallenge { get; set; }

    public bool IsLocked => Lock != LockKind.None;
}

/// <summary>Agenda semanal: bloqueia uma lista em dias e horários fixos.</summary>
public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ListId { get; set; } = "";
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>Horário local de início, "HH:mm".</summary>
    public string Start { get; set; } = "09:00";

    /// <summary>Horário local de fim, "HH:mm". Se for menor que o início, termina no dia seguinte.</summary>
    public string End { get; set; } = "18:00";

    /// <summary>Quando verdadeiro, a agenda não pode ser removida nem alterada durante o horário ativo.</summary>
    public bool Locked { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class Settings
{
    /// <summary>Durante um bloqueio travado, encerra Gerenciador de Tarefas e ferramentas parecidas.</summary>
    public bool ProtectSystemTools { get; set; } = true;

    /// <summary>Desliga o DNS sobre HTTPS no Chrome, Edge e Firefox (via política), para o arquivo hosts valer.</summary>
    public bool DisableBrowserDoH { get; set; } = true;

    /// <summary>Durante um bloqueio travado, impede desinstalar o FocoTotal e parar o serviço.</summary>
    public bool ProtectUninstall { get; set; } = true;
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<BlockList> Lists { get; set; } = new();
    public List<ScheduleEntry> Schedules { get; set; } = new();
    public Settings Settings { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Lists =
        {
            new BlockList
            {
                Id = "redes-sociais",
                Name = "Redes sociais",
                Sites = { "facebook.com", "instagram.com", "x.com", "twitter.com", "tiktok.com", "reddit.com" },
            },
            new BlockList
            {
                Id = "videos-jogos",
                Name = "Vídeos e jogos",
                Sites = { "youtube.com", "twitch.tv", "netflix.com" },
                Apps = { "steam.exe", "EpicGamesLauncher.exe", "Discord.exe" },
            },
        },
    };
}

/// <summary>Estado de execução do serviço, persistido para sobreviver a reinícios.</summary>
public sealed class ServiceState
{
    public List<ActiveBlock> Blocks { get; set; } = new();
    public PomodoroState? Pomodoro { get; set; }

    /// <summary>Última hora vista pelo serviço, para detectar relógio voltando.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }
}

public enum PomodoroPhase { Focus, Break }

public sealed class PomodoroState
{
    public string ListId { get; set; } = "";
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int TotalCycles { get; set; } = 4;
    public int CurrentCycle { get; set; } = 1;
    public PomodoroPhase Phase { get; set; }
    public DateTimeOffset PhaseEndUtc { get; set; }
    public LockKind Lock { get; set; }
}

public sealed class DayStats
{
    public string Date { get; set; } = "";
    public int FocusMinutes { get; set; }
    public Dictionary<string, int> BlockedApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StatsData
{
    public List<DayStats> Days { get; set; } = new();
}
