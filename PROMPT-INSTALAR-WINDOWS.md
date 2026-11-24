Quero instalar o programa FocoTotal neste computador Windows. Não entendo de informática, então faça o máximo possível sozinho e fale comigo em português simples, curto e sem termos técnicos.

O FocoTotal bloqueia sites e fecha programas que tiram a concentração, por tempo, por agenda ou em ciclos de Pomodoro. Ele é feito em **Python** (só a biblioteca padrão) justamente para funcionar com o **Controle Inteligente de Aplicativos** do Windows 11 ligado: roda pelo Python oficial, que é assinado, e não tem nenhum `.exe` próprio.

Use o Python oficial **3.12** instalado pelo `winget` **para todos os usuários**, e o código da pasta `python/` do repositório https://github.com/rafaelfariasbsb/FocoTotal (ramo `main`). Não instale nenhum outro programa nem pacote (`pip`), e não crie arquivos `.exe`, `.bat`, `.cmd`, `.ps1` ou `.vbs`: o Controle Inteligente bloqueia esse tipo de arquivo sem assinatura.

## 1. Descubra onde você está

- **Modo A: você roda comandos direto neste Windows.** É o caso do app Claude na aba Code (`</>`) com o ambiente "Local". Confira rodando `$PSVersionTable.PSVersion; [Environment]::OSVersion.VersionString` na ferramenta PowerShell: só é modo A se a resposta mostrar uma versão do PowerShell e "Microsoft Windows".
- **Modo B: qualquer outro caso** (conversa normal do app, sessão na nuvem, ambiente Linux isolado). Me explique em até três passos curtos como abrir a aba Code (`</>`) com o ambiente "Local", e pare. Não tente rodar comandos no meu computador por outro caminho.

## 2. Primeira mensagem (modo A)

Explique, com as suas palavras, em no máximo cinco linhas curtas:
- Vou instalar o Python (programa gratuito e oficial) e o FocoTotal. Leva uns 5 minutos.
- Quando o Windows perguntar se pode fazer alterações, clique em **SIM** (deve aparecer duas ou três vezes).
- O FocoTotal fica rodando escondido e liga sozinho com o computador. Você abre e configura pelo ícone **FocoTotal** no Menu Iniciar.
- O aplicativo vai me pedir permissão para rodar comandos: pode permitir.
- Não vou pedir senhas nem mexer nos seus arquivos.

Pergunte "Posso começar?" e espere a resposta.

## 3. Instalação (modo A)

Rode tudo pela ferramenta PowerShell. Cada etapa confere o estado antes de agir, então dá para recomeçar do início sem estragar nada. Use tempo limite de pelo menos 10 minutos nos comandos longos. Se uma etapa falhar, leia o erro, tente corrigir uma vez e, se não der, vá para a seção 6. Só considere uma etapa concluída se a última linha da saída for a palavra esperada.

### 3.1 Python oficial para todos os usuários

```powershell
$py = Join-Path $env:ProgramFiles 'Python312\python.exe'
if (-not (Test-Path $py)) {
  winget install -e --id Python.Python.3.12 --scope machine --silent --accept-package-agreements --accept-source-agreements
}
if (-not (Test-Path $py)) { throw 'Python nao encontrado em Arquivos de Programas' }
& $py -c "import sys, ctypes, tkinter, winreg, socketserver; print(sys.version)"
$sig = Get-AuthenticodeSignature $py
"Assinatura: $($sig.Status) - $($sig.SignerCertificate.Subject)"
'PYTHON OK'
```

Avise antes: "Vai aparecer o **SIM** do Windows para instalar o Python." Espere `PYTHON OK` e assinatura `Valid` da **Python Software Foundation**. Use sempre esse caminho completo (`$py`), nunca só `python`: no Windows, `python` pode ser um atalho da Microsoft Store.

- O Python precisa estar em **Arquivos de Programas** (para todos os usuários): é isso que impede alguém de trocar o Python e desligar o FocoTotal. Se já existir um Python 3.12 só do usuário (em `AppData`), não tem problema: instale este também.
- Se o `winget` não existir, me peça para abrir a **Microsoft Store**, procurar **Instalador de Aplicativo** (App Installer) e clicar em **Atualizar** ou **Obter**. Depois rode a etapa de novo.

### 3.2 Baixar o FocoTotal

```powershell
$tmp = Join-Path $env:TEMP 'FocoTotal-instalacao'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp 'focototal.zip'
Invoke-WebRequest 'https://github.com/rafaelfariasbsb/FocoTotal/archive/refs/heads/main.zip' -OutFile $zip -UseBasicParsing
Expand-Archive $zip -DestinationPath $tmp -Force
$src = Join-Path $tmp 'FocoTotal-main\python'
if (-not (Test-Path (Join-Path $src 'instalar.py'))) { throw 'Arquivos do FocoTotal nao encontrados' }
$py = Join-Path $env:ProgramFiles 'Python312\python.exe'
& $py -m compileall -q (Join-Path $src 'focototal') | Out-Null
'BAIXADO'
```

Espere `BAIXADO`.

### 3.3 Instalar

O instalador precisa de administrador, então abre com o "SIM" do Windows e grava o resultado num arquivo.

```powershell
$tmp = Join-Path $env:TEMP 'FocoTotal-instalacao'
$src = Join-Path $tmp 'FocoTotal-main\python'
$py  = Join-Path $env:ProgramFiles 'Python312\python.exe'
$log = Join-Path $tmp 'instalacao.txt'
Remove-Item $log -ErrorAction SilentlyContinue
Start-Process $py -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList @("`"$src\instalar.py`"", '--log', "`"$log`"", 'instalar')
if (Test-Path $log) { Get-Content $log -Encoding UTF8 } else { 'SEM LOG' }
```

Avise antes: "Clique em **SIM** na pergunta do Windows." Espere a última linha `INSTALADO`.

- `SEM LOG`: pergunte se apareceu a pergunta do Windows e se eu cliquei em SIM. Se apareceu um aviso do **Controle Inteligente de Aplicativos**, peça um print e vá para a seção 6.
- "bloqueio travado em andamento": o FocoTotal já está instalado e com uma trava ativa. Só dá para atualizar quando ela terminar.
- `AVISO: não consegui criar o atalho`: siga em frente; na seção 4, diga para abrir pelo caminho que aparece no aviso.

Confira o serviço:

```powershell
$py  = Join-Path $env:ProgramFiles 'Python312\python.exe'
$pyw = Join-Path $env:ProgramFiles 'Python312\pythonw.exe'
Start-Process $pyw -ArgumentList "`"$env:ProgramFiles\FocoTotal\FocoTotal.pyw`""
& $py -c "import sys; sys.path.insert(0, r'$env:ProgramFiles\FocoTotal'); from focototal.instalador import status; status()"
```

Espere `Serviço: RODANDO`. O primeiro comando abre o FocoTotal: deve aparecer a janela com o aviso de boas-vindas.

### 3.4 Teste rápido comigo

Um passo por vez, esperando eu dizer "pronto":

1. "Na janela do FocoTotal, clique em **OK** no aviso. Na aba **Bloqueio**, em **Novo bloqueio**, clique em **Redes sociais**, coloque **0** h e **3** min, deixe a trava em **Sem trava** e clique em **Iniciar bloqueio**."
2. "Abra o navegador numa **janela anônima** (Ctrl + Shift + N) e entre em **instagram.com**. Ele **não deve abrir**. Se abrir, espere 30 segundos e tente de novo."
3. "Volte ao FocoTotal, clique no bloqueio da lista de cima e em **Parar bloqueio selecionado**."

Se o site abriu mesmo assim, rode `Resolve-DnsName instagram.com -ErrorAction SilentlyContinue | Select-Object -First 1 IPAddress` durante o bloqueio. O esperado é `0.0.0.0` ou `::`. Se for outro endereço, o navegador está usando "DNS seguro" por conta própria: peça para eu fechar e abrir o navegador de novo (o FocoTotal desliga isso por política) e testar outra vez.

## 4. Quando terminar, me diga em linguagem simples

- que deu certo e o FocoTotal abre pelo ícone **FocoTotal** no Menu Iniciar;
- que ele liga sozinho com o computador, mesmo com a janela fechada;
- como usar: **Listas** (o que bloquear), **Bloqueio** (agora, por um tempo), **Agenda** (dias e horários fixos), **Pomodoro** e **Estatísticas**;
- que a **trava de tempo** não deixa parar o bloqueio antes do fim, nem reiniciando o computador. É bom testar primeiro com poucos minutos;
- que para desinstalar é em **Configurações > Aplicativos > FocoTotal > Desinstalar**, quando não houver bloqueio travado.

## 5. Atualizar ou reinstalar

Mande esta mesma mensagem de novo: a etapa 3.1 já estará feita, e as 3.2 e 3.3 trocam pela versão mais nova sem perder listas, agenda nem estatísticas. Não funciona durante um bloqueio travado.

## 6. Se der erro

- **Aviso do Controle Inteligente de Aplicativos**: peça um print, anote o nome do arquivo bloqueado que aparece no aviso e diga que o Rafael precisa desse print. Não tente contornar.
- **Aviso do Defender sobre o arquivo hosts** (`HostsFileHijack` ou parecido): peça um print. Não desligue o Defender nem crie exceções.
- **`Serviço: PARADO`**: rode `schtasks /Run /TN FocoTotal`, espere 10 segundos e confira de novo. Se continuar parado, leia o fim de `C:\ProgramData\FocoTotal\servico.log` com `Get-Content -Tail 30` e corrija pelo que aparecer.
- **Internet**: rode a etapa de novo.
- **Desinstalar por comando** (só com o meu OK), com o "SIM" do Windows:
  ```powershell
  $pyw = Join-Path $env:ProgramFiles 'Python312\pythonw.exe'
  Start-Process $pyw -ArgumentList "`"$env:ProgramFiles\FocoTotal\desinstalar.pyw`""
  ```
  (abre uma janela pedindo confirmação e depois o "SIM" do Windows)
  ou, para apagar também listas e estatísticas, `instalar.py --log ... desinstalar --apagar-dados` da pasta baixada na 3.2.

Regras:
- Não instale nada além do Python 3.12 oficial (winget, para todos os usuários) e do código do repositório.
- Não crie `.exe`, `.bat`, `.cmd`, `.ps1` ou `.vbs`, e não use `pip`.
- Não peça senhas.
- Não desligue antivírus, Defender, SmartScreen nem o Controle Inteligente de Aplicativos, e não mude configurações de segurança do Windows.
