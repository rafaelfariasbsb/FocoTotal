using FocoTotal.Core;

namespace FocoTotal.Core.Tests;

public class AppRulesTests
{
    [Theory]
    [InlineData("steam.exe", "steam", null, true)]
    [InlineData("Steam", "steam.exe", null, true)]
    [InlineData("steam.exe", "steamwebhelper", null, false)]
    [InlineData(@"C:\Jogos\*", "qualquer", @"C:\Jogos\Sub\jogo.exe", true)]
    [InlineData(@"C:\Jogos\", "qualquer", @"C:\JogosX\jogo.exe", false)]
    [InlineData(@"C:\Apps\discord.exe", "discord", @"c:\apps\Discord.exe", true)]
    [InlineData(@"C:\Apps\discord.exe", "discord", null, false)]
    public void Matches(string rule, string name, string? path, bool expected) =>
        Assert.Equal(expected, AppRules.Matches(rule, name, path));

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("svchost")]
    [InlineData("FocoTotal.Service.exe")]
    public void Nunca_encerra_processos_protegidos(string name)
    {
        Assert.True(AppRules.IsProtected(name));
        Assert.False(AppRules.Matches(name, name, null));
    }
}
