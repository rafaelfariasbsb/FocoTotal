using System.Security.Cryptography;

namespace FocoTotal.Core;

public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class ChallengeGenerator
{
    // Sem caracteres ambíguos (0/O, 1/l/I).
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}

public enum StopOutcome { Stopped, Denied, ChallengeIssued, WrongAnswer }

public sealed record StopResult(StopOutcome Outcome, string Message, string? Challenge = null);

public static class LockPolicy
{
    /// <summary>Decide se um bloqueio pode ser parado com a resposta informada. Pode alterar block.PendingChallenge.</summary>
    public static StopResult TryStop(ActiveBlock block, string? answer)
    {
        switch (block.Lock)
        {
            case LockKind.None:
                return new(StopOutcome.Stopped, "Bloqueio encerrado.");

            case LockKind.Timer:
                return new(StopOutcome.Denied, $"Trava de tempo: este bloqueio só termina em {block.EndUtc.ToLocalTime():dd/MM HH:mm}.");

            case LockKind.RandomText:
                if (block.PendingChallenge is null || string.IsNullOrEmpty(answer))
                {
                    block.PendingChallenge ??= ChallengeGenerator.Generate(Math.Max(10, block.RandomTextLength));
                    return new(StopOutcome.ChallengeIssued, "Digite o texto abaixo exatamente como aparece para encerrar.", block.PendingChallenge);
                }
                if (string.Equals(answer, block.PendingChallenge, StringComparison.Ordinal))
                    return new(StopOutcome.Stopped, "Texto correto. Bloqueio encerrado.");
                return new(StopOutcome.WrongAnswer, "O texto não confere. Tente de novo.", block.PendingChallenge);

            case LockKind.Password:
                if (string.IsNullOrEmpty(answer)) return new(StopOutcome.ChallengeIssued, "Digite a senha da trava.");
                return PasswordHasher.Verify(answer, block.PasswordHash)
                    ? new(StopOutcome.Stopped, "Senha correta. Bloqueio encerrado.")
                    : new(StopOutcome.WrongAnswer, "Senha incorreta.");

            default:
                return new(StopOutcome.Denied, "Tipo de trava desconhecido.");
        }
    }
}

/// <summary>
/// Detecta o relógio do sistema sendo adiantado enquanto o serviço roda, comparando com um
/// contador monotônico. Quando detecta, o fim dos bloqueios travados é empurrado pelo mesmo tanto.
/// </summary>
public sealed class ClockGuard
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);
    private long _lastTicks;
    private DateTimeOffset _lastUtc;
    private bool _initialized;

    /// <summary>Retorna quanto o relógio foi adiantado desde a última chamada (zero se nada suspeito).</summary>
    public TimeSpan Observe(DateTimeOffset nowUtc, long monotonicMs)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastTicks = monotonicMs;
            _lastUtc = nowUtc;
            return TimeSpan.Zero;
        }

        var realElapsed = TimeSpan.FromMilliseconds(monotonicMs - _lastTicks);
        var wallElapsed = nowUtc - _lastUtc;
        _lastTicks = monotonicMs;
        _lastUtc = nowUtc;

        var jump = wallElapsed - realElapsed;
        return jump > Tolerance ? jump : TimeSpan.Zero;
    }

    /// <summary>Empurra o fim dos bloqueios travados e do Pomodoro travado.</summary>
    public static bool Compensate(ServiceState state, TimeSpan jump)
    {
        if (jump <= TimeSpan.Zero) return false;
        var changed = false;
        foreach (var b in state.Blocks.Where(b => b.IsLocked)) { b.EndUtc += jump; changed = true; }
        if (state.Pomodoro is { } p && p.Lock != LockKind.None) { p.PhaseEndUtc += jump; changed = true; }
        return changed;
    }
}
