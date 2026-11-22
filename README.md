# FocoTotal

Bloqueador de distrações para Windows, em português. Bloqueia sites e aplicativos por tempo, por agenda ou em ciclos de Pomodoro, com travas que não deixam desistir no meio.

**Download:** [Releases](https://github.com/rafaelfariasbsb/FocoTotal/releases) · **Licença:** [MIT](LICENSE) · [Code signing policy](CODE_SIGNING.md)

## O que a versão 0.1 faz

- **Listas de bloqueio** com sites (`youtube.com`, `*.reddit.com`, URLs coladas do navegador) e aplicativos (`steam.exe`, caminho completo ou pasta inteira `C:\Jogos\*`).
- **Bloqueio de sites** pelo arquivo `hosts` do Windows, válido para todos os navegadores e apps. O serviço reaplica se alguém editar o arquivo.
- **Bloqueio de aplicativos**: o serviço encerra o processo assim que ele abre.
- **Bloqueio por tempo** com quatro tipos de trava:
  - Sem trava (pode parar quando quiser)
  - Trava de tempo (ninguém para até o fim)
  - Texto aleatório (digitar de 10 a 5.000 caracteres, sem colar)
  - Senha
- **Agenda semanal** (ex.: dias úteis das 9h às 18h, inclusive horários que passam da meia-noite). A agenda pode ser travada.
- **Pomodoro** com foco, pausa e número de ciclos, opcionalmente com trava de tempo.
- **Estatísticas locais**: minutos de foco e tentativas bloqueadas por dia, com exportação em JSON.
- **Proteções durante bloqueio travado**:
  - Serviço com ACL que nega parar, excluir ou reconfigurar a usuários logados, inclusive administradores.
  - O MSI recusa desinstalar, reparar ou atualizar enquanto há trava.
  - Gerenciador de Tarefas, Editor do Registro, MMC e similares são fechados.
  - Adiantar o relógio do Windows não encurta o bloqueio.
  - O bloqueio continua depois de reiniciar o PC, e o serviço também roda no Modo de Segurança.
  - Só é permitido endurecer uma lista travada (adicionar itens), nunca afrouxar.
- **DNS sobre HTTPS desligado** no Chrome, Edge, Brave e Firefox por política, para o `hosts` valer. Política já definida por GPO ou Intune não é sobrescrita.

## Arquitetura

```
┌──────────────────────┐   pipe nomeado local    ┌─────────────────────────────────┐
│ FocoTotal.App        │ ◄─────────────────────► │ FocoTotal.Service (SYSTEM)      │
│ WinForms, bandeja    │   JSON, só da máquina   │ regras, agenda, Pomodoro, travas│
└──────────────────────┘                         │  ├─ HostsEnforcer  (sites)      │
                                                 │  ├─ ProcessEnforcer (apps)      │
                                                 │  ├─ BrowserPolicyEnforcer (DoH) │
                                                 │  └─ TamperProtection            │
                                                 └───────────────┬─────────────────┘
                                                                 │
                                                  %ProgramData%\FocoTotal (só SYSTEM escreve)
```

| Projeto | O que é |
|---|---|
| `src/FocoTotal.Core` | Modelos, regras de bloqueio, agenda, travas e protocolo. Sem dependência de Windows, 100% testável. |
| `src/FocoTotal.Service` | Serviço do Windows (.NET 8 Worker) que aplica tudo. |
| `src/FocoTotal.App` | App de configuração com ícone na bandeja. |
| `installer` | MSI em WiX v5, pronto para Intune. |
| `tests/FocoTotal.Core.Tests` | Testes xUnit do núcleo. |

Mais detalhes em [docs/arquitetura.md](docs/arquitetura.md).

## Compilar

Requisitos: Windows 10/11 x64 e .NET 8 SDK.

```powershell
dotnet test tests/FocoTotal.Core.Tests
./scripts/build.ps1 -Version 0.1.0
# MSI em artifacts\FocoTotal-0.1.0-x64.msi
```

O GitHub Actions (`.github/workflows/build.yml`) roda os testes e gera o MSI a cada push. Uma tag `v0.1.0` publica uma release com o MSI.

## Instalar

```powershell
msiexec /i FocoTotal-0.1.0-x64.msi /qn
```

Para distribuir pelo Intune, veja [docs/intune.md](docs/intune.md).

## Limitações conhecidas

- O `hosts` bloqueia domínios inteiros. Bloquear só um caminho (`youtube.com/shorts`), palavra-chave ou título de página depende da extensão de navegador, que é a próxima etapa.
- Quem é administrador e sabe o que faz ainda consegue burlar (por exemplo, rodando algo como SYSTEM ou desligando o serviço offline). A proteção aumenta muito o esforço, mas não substitui controles de rede como FortiGate ou o filtro web do Defender for Endpoint.
- O Microsoft Defender pode classificar alterações no `hosts` como `SettingsModifier:Win32/HostsFileHijack` em alguns casos. Veja como tratar em [docs/intune.md](docs/intune.md#defender).

## Alterações que o FocoTotal faz no sistema

O FocoTotal mexe no Windows só para aplicar os bloqueios que você configurar. O app mostra esta lista na primeira vez que é aberto.

- Escreve um bloco delimitado no arquivo `hosts` enquanto há sites bloqueados, e remove o bloco quando o bloqueio termina ou na desinstalação.
- Desliga o DNS sobre HTTPS dos navegadores por política, se ela ainda não existir. Dá para desativar em Configurações.
- Durante bloqueio travado: fecha os apps da lista e ferramentas do sistema, restringe o serviço e recusa a desinstalação até o bloqueio acabar.
- Instala o serviço `FocoTotal` (também ativo no Modo de Segurança) e o ícone na bandeja ao entrar no Windows.

Para desinstalar: **Configurações > Aplicativos > FocoTotal > Desinstalar**, sem bloqueio travado em andamento.

## Privacidade

O FocoTotal não envia nenhuma informação pela rede. Configurações e estatísticas ficam só em `%ProgramData%\FocoTotal`, no próprio computador.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Papéis da equipe, política de privacidade e regras de assinatura: [CODE_SIGNING.md](CODE_SIGNING.md). Como a assinatura é configurada: [docs/assinatura.md](docs/assinatura.md).

## Próximas etapas

1. Extensão para Chrome e Edge: bloqueio por URL, palavra-chave, título e YouTube Shorts. O serviço vai fechar navegadores sem a extensão.
2. Lista branca (bloquear tudo menos alguns apps/sites).
3. Cotas diárias de uso por app ou site.
