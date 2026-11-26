# Code signing policy / Política de assinatura de código

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Assinatura de código gratuita fornecida pela [SignPath.io](https://about.signpath.io), com certificado da [SignPath Foundation](https://signpath.org).

## What is signed / O que é assinado

Only release builds produced by the public GitHub Actions workflow (`.github/workflows/build.yml`) from a `v*` tag of this repository are signed:
`FocoTotal-<version>-x64.msi` and the executables inside it (`FocoTotal.Service.exe`, `FocoTotal.App.exe`).

Somente as versões geradas pelo GitHub Actions público deste repositório, a partir de uma tag `v*`, são assinadas.

## Team roles / Papéis da equipe

FocoTotal is maintained by one person, Rafael Farias, who uses two GitHub accounts.

O FocoTotal é mantido por uma pessoa, Rafael Farias, que usa duas contas no GitHub.

| Role / Papel | Members / Membros |
|---|---|
| Committers and reviewers / Autores e revisores | Rafael Farias ([rafaelfariasbsb](https://github.com/rafaelfariasbsb), [rafaelfariassenac](https://github.com/rafaelfariassenac)) |
| Approvers / Aprovadores | Rafael Farias ([rafaelfariasbsb](https://github.com/rafaelfariasbsb)) |

All changes from people outside the team are reviewed before merging. Every signing request is approved manually by an approver.
All team members use multi-factor authentication on GitHub and SignPath.

## Privacy policy / Política de privacidade

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Este programa não envia nenhuma informação para outros sistemas em rede, a menos que o usuário ou quem o instala ou opera peça isso explicitamente. Configurações e estatísticas ficam só em `%ProgramData%\FocoTotal`, no próprio computador.

## System changes / Alterações no sistema

FocoTotal changes the system only to enforce blocks the user configures, and the app shows these changes on first run:

- Writes a delimited block to `C:\Windows\System32\drivers\etc\hosts` while a site block is active, and removes it when the block ends or on uninstall.
- Sets `DnsOverHttpsMode`/`DNSOverHTTPS` browser policies under `HKLM\SOFTWARE\Policies` only if they are not already set (optional, can be disabled).
- During a locked block: closes listed apps and system tools (Task Manager, Registry Editor, MMC), restricts the FocoTotal service ACL and refuses uninstall until the block ends.
- Installs the `FocoTotal` Windows service (also enabled in Safe Mode) and a logon entry for the tray icon.

Uninstall: *Settings > Apps > FocoTotal > Uninstall* or `msiexec /x {product-code}` (when no locked block is running).
