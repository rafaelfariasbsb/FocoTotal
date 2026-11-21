using FocoTotal.Core;

namespace FocoTotal.Core.Tests;

public class DomainRulesTests
{
    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("https://www.YouTube.com/watch?v=1", "youtube.com")]
    [InlineData("*.reddit.com", "reddit.com")]
    [InlineData("http://user@x.com:8080/home", "x.com")]
    [InlineData("  instagram.com/  ", "instagram.com")]
    [InlineData("sub.domain.com.br", "sub.domain.com.br")]
    public void Normalize_extrai_dominio(string input, string expected) =>
        Assert.Equal(expected, DomainRules.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("não é site")]
    [InlineData("exa_mple.com")]
    public void Normalize_rejeita_invalido(string input) => Assert.Null(DomainRules.Normalize(input));

    [Fact]
    public void ExpandForHosts_inclui_subdominios_comuns()
    {
        var list = DomainRules.ExpandForHosts(new[] { "youtube.com", "https://www.youtube.com" });
        Assert.Contains("youtube.com", list);
        Assert.Contains("www.youtube.com", list);
        Assert.Contains("m.youtube.com", list);
        Assert.Equal(list.Count, list.Distinct().Count());
    }

    [Fact]
    public void ApplyBlock_preserva_conteudo_e_e_idempotente()
    {
        const string original = "# hosts do Windows\r\n127.0.0.1 intranet.local\r\n";
        var domains = DomainRules.ExpandForHosts(new[] { "x.com" });
        var once = DomainRules.ApplyBlock(original, domains);
        var twice = DomainRules.ApplyBlock(once, domains);

        Assert.Equal(once, twice);
        Assert.Contains("127.0.0.1 intranet.local", once);
        Assert.Contains("0.0.0.0 x.com", once);
        Assert.Contains(":: www.x.com", once);
        Assert.Equal(original, DomainRules.RemoveBlock(once));
    }

    [Fact]
    public void ApplyBlock_sem_dominios_remove_bloco()
    {
        var withBlock = DomainRules.ApplyBlock("127.0.0.1 a.local\n", DomainRules.ExpandForHosts(new[] { "x.com" }));
        var cleared = DomainRules.ApplyBlock(withBlock, Array.Empty<string>());
        Assert.DoesNotContain("FocoTotal", cleared);
        Assert.Contains("a.local", cleared);
    }
}
