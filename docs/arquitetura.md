# Arquitetura do FocoTotal

## Componentes

### Serviço (`FocoTotal.Service`)

Roda como `LocalSystem`. A cada segundo:

1. Detecta se o relógio foi adiantado (compara com `GetTickCount64`) e, se foi, empurra o fim dos bloqueios travados pelo mesmo tanto.
2. Remove bloqueios vencidos e avança o Pomodoro.
3. Calcula a política efetiva: bloqueios manuais + agendas no horário + foco do Pomodoro.
4. Aplica:
   - **Sites**: escreve um bloco delimitado no `hosts` (`0.0.0.0` e `::`), com o domínio e subdomínios comuns (`www`, `m`, `mobile`, `web`, `app`, `api`), e limpa o cache DNS. Se alguém editar o bloco, ele volta no segundo seguinte.
   - **Apps**: lista os processos e encerra os que batem com as regras. Processos do sistema e o próprio FocoTotal nunca são encerrados.
   - **Navegadores**: políticas `DnsOverHttpsMode=off` (Chrome, Edge, Brave) e `DNSOverHTTPS` (Firefox).
   - **Anti-burla**, quando há trava: ACL do serviço e `HKLM\SOFTWARE\FocoTotal\Locked=1`.

Estado em `%ProgramData%\FocoTotal`:

| Arquivo | Conteúdo |
|---|---|
| `config.json` | listas, agendas e configurações |
| `state.json` | bloqueios em andamento, Pomodoro, hash de senhas das travas |
| `stats.json` | estatísticas por dia (até 365 dias) |

A pasta tem herança desligada: SYSTEM com controle total e Administradores só com leitura. Toda escrita é atômica, com cópia `.bak`.

Quando o serviço para sem trava ativa (ex.: desinstalação), ele remove o bloco do `hosts` e as políticas que criou. Com trava ativa, nada é desfeito.

### App (`FocoTotal.App`)

WinForms, roda sem elevação. Fala com o serviço por um pipe nomeado (`\\.\pipe\FocoTotal.Control`) com mensagens JSON prefixadas pelo tamanho. O pipe nega acesso pela rede e só aceita usuários autenticados da máquina.

Abas: Bloqueio, Listas, Agenda, Pomodoro, Estatísticas e Configurações. Ao fechar a janela, o app fica na bandeja e avisa quando fecha um app bloqueado.

### Núcleo (`FocoTotal.Core`)

Toda a lógica de decisão é pura e testada:

- `DomainRules`: normaliza entradas de site e gera o bloco do `hosts` de forma idempotente.
- `AppRules`: casa processo com regra (nome, caminho ou pasta).
- `BlockEngine`: agenda (inclusive após a meia-noite), política efetiva, Pomodoro e validação de mudanças durante trava.
- `LockPolicy`, `PasswordHasher` (PBKDF2-SHA256, 210 mil iterações), `ChallengeGenerator`, `ClockGuard`.

## Regras da trava

Enquanto uma lista está em bloqueio travado (manual, agenda travada ou Pomodoro com trava):

- Itens podem ser adicionados à lista, nunca removidos. A lista não pode ser excluída.
- Agenda travada no horário ativo não pode ser alterada nem desativada.
- As proteções (ferramentas do sistema, DoH, desinstalação) não podem ser desligadas.

## ACL do serviço durante a trava

```
D:(D;;WPDTSDWDWODC;;;IU)(D;;WPDTSDWDWODC;;;NU)
  (A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)
  (A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)
```

As ACEs de negação para INTERACTIVE (IU) e NETWORK (NU) pegam qualquer pessoa logada, inclusive administradores elevados, e bloqueiam parar, pausar, excluir, mudar a configuração e mudar a própria ACL. O serviço roda como SYSTEM, que não tem esses grupos, então continua conseguindo voltar a ACL ao normal quando a trava acaba.

## Burlas conhecidas

| Burla | Situação |
|---|---|
| Editar o `hosts` | Revertido em até 1 segundo |
| Parar o serviço ou mudar para desativado | Negado pela ACL durante a trava |
| Desinstalar pelo Painel ou `msiexec /x` | Recusado pelo MSI durante a trava |
| Gerenciador de Tarefas, regedit, services.msc | Fechados durante a trava (opção ligada por padrão) |
| Adiantar o relógio com o serviço rodando | Compensado |
| Reiniciar o PC | O estado é persistido e o serviço sobe com o Windows |
| Modo de Segurança | O serviço também sobe |
| DNS sobre HTTPS no navegador | Desligado por política |
| Navegador com proxy/VPN | **Não coberto** no v0.1. Bloqueie o app da VPN na lista. |
| Adiantar o relógio com o PC desligado | **Não coberto** no v0.1 |
| Rodar algo como SYSTEM (PsExec -s), editar o registro offline, pelo WinPE | **Não coberto**. Exige controle administrativo da máquina. |
