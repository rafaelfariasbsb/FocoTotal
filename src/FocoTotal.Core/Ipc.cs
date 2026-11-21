using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocoTotal.Core;

public static class IpcCommands
{
    public const string GetStatus = "status";
    public const string SaveConfig = "save-config";
    public const string StartBlock = "start-block";
    public const string StopBlock = "stop-block";
    public const string StartPomodoro = "start-pomodoro";
    public const string StopPomodoro = "stop-pomodoro";
    public const string GetStats = "stats";
}

public sealed class IpcRequest
{
    public string Command { get; set; } = "";
    public AppConfig? Config { get; set; }
    public StartBlockRequest? Start { get; set; }
    public StopBlockRequest? Stop { get; set; }
    public StartPomodoroRequest? Pomodoro { get; set; }
}

public sealed class StartBlockRequest
{
    public List<string> ListIds { get; set; } = new();
    public int DurationMinutes { get; set; }
    public LockKind Lock { get; set; }
    public int RandomTextLength { get; set; } = 200;
    public string? Password { get; set; }
}

public sealed class StopBlockRequest
{
    public string BlockId { get; set; } = "";
    public string? Answer { get; set; }
}

public sealed class StartPomodoroRequest
{
    public string ListId { get; set; } = "";
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int Cycles { get; set; } = 4;
    public LockKind Lock { get; set; }
}

public sealed class StatusInfo
{
    public AppConfig Config { get; set; } = new();
    public List<ActiveBlockInfo> Blocks { get; set; } = new();
    public PomodoroState? Pomodoro { get; set; }
    public List<string> ActiveListIds { get; set; } = new();
    public List<string> LockedListIds { get; set; } = new();
    public bool ConfigReadOnly { get; set; }
    public string ServiceVersion { get; set; } = "";
    public int BlockedToday { get; set; }
}

/// <summary>Bloqueio como o app vê: sem hash de senha nem desafio.</summary>
public sealed class ActiveBlockInfo
{
    public string Id { get; set; } = "";
    public List<string> ListIds { get; set; } = new();
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public LockKind Lock { get; set; }
    public BlockSource Source { get; set; }

    public static ActiveBlockInfo From(ActiveBlock b) => new()
    {
        Id = b.Id, ListIds = b.ListIds.ToList(), StartUtc = b.StartUtc, EndUtc = b.EndUtc, Lock = b.Lock, Source = b.Source,
    };
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public StatusInfo? Status { get; set; }
    public StopOutcome? StopOutcome { get; set; }
    public string? Challenge { get; set; }
    public StatsData? Stats { get; set; }

    public static IpcResponse Success(string? message = null) => new() { Ok = true, Message = message };
    public static IpcResponse Fail(string message) => new() { Ok = false, Message = message };
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}

/// <summary>Cliente do pipe nomeado usado pelo app de configuração.</summary>
public sealed class IpcClient
{
    public const string PipeName = "FocoTotal.Control";

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, ct);
        await WriteMessageAsync(pipe, request, ct);
        return await ReadMessageAsync<IpcResponse>(pipe, ct) ?? IpcResponse.Fail("Resposta vazia do serviço.");
    }

    /// <summary>Mensagens são JSON em UTF-8 precedidos de 4 bytes de tamanho.</summary>
    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        var len = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(len, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await stream.ReadExactlyAsync(lenBuf, ct);
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > MaxMessageBytes) throw new InvalidDataException("Tamanho de mensagem inválido.");
        var buf = new byte[len];
        await stream.ReadExactlyAsync(buf, ct);
        return JsonSerializer.Deserialize<T>(buf, Json.Options);
    }
}
