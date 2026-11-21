using FocoTotal.Core;

namespace FocoTotal.Core.Tests;

public class LocksTests
{
    [Fact]
    public void Senha_hash_e_verificacao()
    {
        var h = PasswordHasher.Hash("segredo123");
        Assert.True(PasswordHasher.Verify("segredo123", h));
        Assert.False(PasswordHasher.Verify("Segredo123", h));
        Assert.False(PasswordHasher.Verify("x", null));
    }

    [Fact]
    public void Trava_de_tempo_nunca_para()
    {
        var b = new ActiveBlock { Lock = LockKind.Timer, EndUtc = DateTimeOffset.UtcNow.AddHours(1) };
        Assert.Equal(StopOutcome.Denied, LockPolicy.TryStop(b, "qualquer").Outcome);
    }

    [Fact]
    public void Texto_aleatorio_exige_texto_exato()
    {
        var b = new ActiveBlock { Lock = LockKind.RandomText, RandomTextLength = 40 };
        var first = LockPolicy.TryStop(b, null);
        Assert.Equal(StopOutcome.ChallengeIssued, first.Outcome);
        Assert.Equal(40, first.Challenge!.Length);

        Assert.Equal(StopOutcome.WrongAnswer, LockPolicy.TryStop(b, first.Challenge.ToUpperInvariant() + "x").Outcome);
        Assert.Equal(first.Challenge, b.PendingChallenge);
        Assert.Equal(StopOutcome.Stopped, LockPolicy.TryStop(b, first.Challenge).Outcome);
    }

    [Fact]
    public void Senha_para_com_senha_certa()
    {
        var b = new ActiveBlock { Lock = LockKind.Password, PasswordHash = PasswordHasher.Hash("abc") };
        Assert.Equal(StopOutcome.ChallengeIssued, LockPolicy.TryStop(b, null).Outcome);
        Assert.Equal(StopOutcome.WrongAnswer, LockPolicy.TryStop(b, "abd").Outcome);
        Assert.Equal(StopOutcome.Stopped, LockPolicy.TryStop(b, "abc").Outcome);
    }

    [Fact]
    public void ClockGuard_detecta_relogio_adiantado()
    {
        var g = new ClockGuard();
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, g.Observe(t0, 1_000));
        Assert.Equal(TimeSpan.Zero, g.Observe(t0.AddSeconds(1), 2_000));
        var jump = g.Observe(t0.AddDays(3), 3_000);
        Assert.True(jump > TimeSpan.FromDays(2.9));

        var state = new ServiceState
        {
            Blocks =
            {
                new ActiveBlock { Lock = LockKind.Timer, EndUtc = t0.AddHours(1) },
                new ActiveBlock { Lock = LockKind.None, EndUtc = t0.AddHours(1) },
            },
        };
        Assert.True(ClockGuard.Compensate(state, jump));
        Assert.Equal(t0.AddHours(1) + jump, state.Blocks[0].EndUtc);
        Assert.Equal(t0.AddHours(1), state.Blocks[1].EndUtc);
    }

    [Fact]
    public async Task Ipc_serializa_ida_e_volta()
    {
        var req = new IpcRequest { Command = IpcCommands.StartBlock, Start = new StartBlockRequest { ListIds = { "a" }, DurationMinutes = 5, Lock = LockKind.RandomText } };
        using var ms = new MemoryStream();
        await IpcClient.WriteMessageAsync(ms, req, default);
        ms.Position = 0;
        var back = (await IpcClient.ReadMessageAsync<IpcRequest>(ms, default))!;
        Assert.Equal(LockKind.RandomText, back.Start!.Lock);
        Assert.Equal("a", back.Start.ListIds.Single());
    }
}
