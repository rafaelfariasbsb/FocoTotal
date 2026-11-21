# Distribuição pelo Microsoft Intune

O MSI é por máquina (`perMachine`), x64 e silencioso. Ele instala:

- `C:\Program Files\FocoTotal\FocoTotal.Service.exe`, como serviço `FocoTotal` (LocalSystem, início automático, reinício em caso de falha, também roda no Modo de Segurança)
- `C:\Program Files\FocoTotal\FocoTotal.App.exe`, com atalho no Menu Iniciar e ícone na bandeja em todo logon (`HKLM\...\Run`)

## Opção recomendada: app Win32 (.intunewin)

O app Win32 dá mais controle sobre detecção, reinício e dependências do que o tipo "Linha de negócios".

1. Empacote com o [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool):
   ```powershell
   IntuneWinAppUtil.exe -c .\artifacts -s FocoTotal-0.1.0-x64.msi -o .\intune
   ```
2. No Intune: **Apps > Windows > Adicionar > Aplicativo do Windows (Win32)** e envie o `.intunewin`.
3. Programa:
   - Instalação: `msiexec /i "FocoTotal-0.1.0-x64.msi" /qn /norestart`
   - Desinstalação: `msiexec /x {CÓDIGO-DO-PRODUTO} /qn /norestart` (o Intune preenche ao ler o MSI)
   - Comportamento: **Sistema**
4. Requisitos: Windows 10 1809+ x64.
5. Detecção: **MSI**, com o código do produto preenchido automaticamente.

> A desinstalação **falha de propósito** se houver um bloqueio travado em andamento (o MSI lê `HKLM\SOFTWARE\FocoTotal\Locked`). Ela funciona normalmente quando a trava termina.

## Opção simples: Linha de negócios (LOB)

**Apps > Windows > Adicionar > Aplicativo de linha de negócios**, envie o `.msi`, e em argumentos use `/qn`.

## Configuração gerenciada

### Somente administradores alteram

Crie o valor abaixo para que usuários comuns possam ver e iniciar bloqueios, mas não editar listas, agendas, configurações nem parar bloqueios travados. Só um administrador com o app aberto em modo elevado consegue alterar.

```
HKLM\SOFTWARE\Policies\FocoTotal
  AdminOnly (DWORD) = 1
```

No Intune, aplique com um script de **Remediação** (Dispositivos > Scripts e correções) ou um script de plataforma:

```powershell
New-Item -Path 'HKLM:\SOFTWARE\Policies\FocoTotal' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\FocoTotal' -Name AdminOnly -Value 1 -Type DWord
```

### DNS sobre HTTPS

O FocoTotal desliga o DoH no Chrome, Edge, Brave e Firefox **só se a política ainda não existir**. Se você já gerencia `DnsOverHttpsMode` pelo Intune (catálogo de configurações), mantenha como `off` para o bloqueio de sites funcionar.

<a id="defender"></a>
## Microsoft Defender

- **Alterações no hosts**: o Defender pode detectar `SettingsModifier:Win32/HostsFileHijack` quando o `hosts` redireciona certos domínios. Se acontecer, permita a detecção em **Segurança do ponto de extremidade > Antivírus** (ação de ameaça como "Permitir" para esse ID), em vez de excluir o arquivo `hosts` inteiro.
- **SmartScreen e reputação**: até haver assinatura de código, o Defender SmartScreen pode avisar ao executar o MSI fora do Intune. Pelo Intune (contexto de sistema), a instalação não é bloqueada.
- **ASR**: a regra "Bloquear executáveis a menos que atendam a critérios de prevalência, idade ou lista confiável" pode bloquear executáveis não assinados. Assine os binários ou adicione exclusão para `C:\Program Files\FocoTotal\`.

## Combinação com controles de rede

O FocoTotal age no dispositivo. Em ambiente corporativo, combine com:

- **Defender for Endpoint, filtro de conteúdo web** (categorias como Redes sociais e Streaming), que vale também fora da rede da empresa.
- **FortiGate, Web Filter** por categoria e por URL, com inspeção SSL para bloquear caminhos específicos.
- **Intune, políticas de navegador** (Edge e Chrome) para lista de bloqueio de URLs (`URLBlocklist`), que já bloqueia caminhos completos sem extensão.
