# Assinatura de código com a SignPath Foundation

A SignPath Foundation assina projetos open source de graça. Com o MSI assinado, o Controle Inteligente de Aplicativos do Windows 11, o SmartScreen e o Defender passam a confiar no FocoTotal.

## Como funciona

1. Uma tag `v*` (ex.: `v0.1.1`) dispara o GitHub Actions.
2. O Actions compila, roda os testes, gera o MSI e envia como artefato "sem assinatura".
3. O passo **Assinar pela SignPath** manda o artefato para a SignPath. Um aprovador aprova o pedido no site da SignPath (exigência para toda versão).
4. A SignPath assina `FocoTotal.Service.exe` e `FocoTotal.App.exe` dentro do MSI e depois o próprio MSI (configuração em [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml)).
5. O Actions publica a release com o MSI assinado.

Enquanto a SignPath não estiver configurada, o passo de assinatura é pulado e a release sai sem assinatura.

## Configuração (uma vez)

1. Repositório público, com licença MIT e [política de assinatura](../CODE_SIGNING.md).
2. Uma versão já publicada sem assinatura (a SignPath exige que o projeto já esteja lançado). A página da release cita a política de assinatura ([`.github/release-notes.md`](../.github/release-notes.md)).
3. Inscrição em <https://signpath.org/apply>. Depois da aprovação, a SignPath cria a organização e o projeto.
4. No SignPath:
   - Ativar MFA na conta.
   - Em **Trusted Build Systems**, adicionar **GitHub.com** e ligar ao projeto.
   - Instalar o app SignPath no GitHub, no repositório FocoTotal.
   - Em **Artifact configurations**, colar o conteúdo de `.signpath/artifact-configuration.xml`.
   - Criar um usuário de CI e gerar um API token com permissão de submitter na política `release-signing`.
5. No GitHub, em **Settings > Secrets and variables > Actions**:
   - Secret `SIGNPATH_API_TOKEN`: o token do passo anterior.
   - Variable `SIGNPATH_ORGANIZATION_ID`: o ID da organização na SignPath.
   - Variable `SIGNPATH_PROJECT_SLUG`: o slug do projeto (padrão `FocoTotal`).
   - Variable `SIGNPATH_POLICY_SLUG`: o slug da política (padrão `release-signing`).

## Publicar uma versão assinada

```powershell
git tag v0.1.1
git push origin v0.1.1
```

Aprove o pedido de assinatura no SignPath. A release aparece no GitHub com o MSI assinado.
