Quero testar o programa FocoTotal neste computador Windows. Não entendo de informática, então faça o máximo possível sozinho e fale comigo em português simples, curto e sem termos técnicos.

O FocoTotal bloqueia sites e fecha programas que tiram a concentração. Esta é a **versão de teste**: um script em Python que, por alguns minutos, bloqueia um site e fecha um programa, e depois desfaz tudo sozinho. Ela serve para confirmar que o **Controle Inteligente de Aplicativos** do Windows 11 deixa o FocoTotal funcionar sem desligar nenhuma proteção.

Use o Python oficial **3.12**, instalado pelo `winget`, e o script `prototipo-python/focototal_teste.py` do repositório https://github.com/rafaelfariasbsb/FocoTotal (ramo `main`). Não instale nenhum outro programa e não crie arquivos `.exe`, `.bat`, `.cmd`, `.ps1` ou `.vbs`: o Controle Inteligente bloqueia esse tipo de arquivo sem assinatura, e o objetivo é justamente não depender disso.

## 1. Descubra onde você está

- **Modo A: você roda comandos direto neste Windows.** É o caso do app Claude na aba Code (`</>`) com o ambiente "Local". Confira rodando `$PSVersionTable.PSVersion; [Environment]::OSVersion.VersionString` na ferramenta PowerShell: só é modo A se a resposta mostrar uma versão do PowerShell e "Microsoft Windows".
- **Modo B: qualquer outro caso** (conversa normal do app, sessão na nuvem, ambiente Linux isolado). Me explique em até três passos curtos como abrir a aba Code (`</>`) com o ambiente "Local", e pare. Não tente rodar comandos no meu computador por outro caminho.

## 2. Primeira mensagem (modo A)

Me pergunte: "Qual **site** você quer bloquear no teste e qual **programa** quer que eu feche? Se não tiver preferência, responda **PADRÃO** (bloqueio o instagram.com e fecho o Bloco de Notas por 3 minutos)."

Aceite o site como endereço (`youtube.com`) e o programa pelo nome do arquivo (`notepad.exe`, `chrome.exe`). Se eu disser só o nome do programa ("Bloco de Notas", "Spotify"), descubra o nome do arquivo e confirme comigo. Não aceite programas do sistema (`explorer.exe`, `svchost.exe`, `winlogon.exe` e parecidos) nem o navegador que vou usar para testar o site.

Depois explique, com as suas palavras, em no máximo quatro linhas curtas:
- Vou instalar o Python (um programa gratuito e oficial), baixar o teste do FocoTotal e rodar por alguns minutos.
- Quando o Windows perguntar se pode fazer alterações, clique em **SIM**. Vai abrir uma janela preta: **não feche** até o teste acabar.
- O aplicativo vai me pedir permissão para rodar comandos: pode permitir.
- Não vou pedir senhas, não vou mexer nos seus arquivos e, no fim, tudo volta como estava.

Espere a resposta e comece.

## 3. Instalação (modo A)

Rode tudo pela ferramenta PowerShell. Cada etapa confere o estado antes de agir, então dá para recomeçar do início sem estragar nada. Use tempo limite de pelo menos 10 minutos nos comandos longos. Se uma etapa falhar, leia o erro, tente corrigir uma vez e, se não der, vá para a seção 6. Só considere uma etapa concluída se a última linha da saída for a palavra esperada.

### 3.1 Python oficial

```powershell
$py = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) {
  winget install -e --id Python.Python.3.12 --scope user --silent --accept-package-agreements --accept-source-agreements
}
if (-not (Test-Path $py)) { throw 'Python nao encontrado depois da instalacao' }
& $py -c "import sys, ctypes; print(sys.version)"
$sig = Get-AuthenticodeSignature $py
"Assinatura: $($sig.Status) - $($sig.SignerCertificate.Subject)"
'PYTHON OK'
```

Espere `PYTHON OK` e assinatura `Valid` da **Python Software Foundation**. Use sempre esse caminho completo (`$py`), nunca só `python`: no Windows, `python` pode ser um atalho da Microsoft Store que abre a loja em vez de rodar.

Se o `winget` não existir, me peça para abrir a **Microsoft Store**, procurar **Instalador de Aplicativo** (App Installer) e clicar em **Atualizar** ou **Obter**. Depois rode a etapa de novo.

### 3.2 Baixar o teste

```powershell
$pasta = Join-Path $env:LOCALAPPDATA 'FocoTotal'
New-Item -ItemType Directory -Force -Path $pasta | Out-Null
$script = Join-Path $pasta 'focototal_teste.py'
Invoke-WebRequest 'https://raw.githubusercontent.com/rafaelfariasbsb/FocoTotal/main/prototipo-python/focototal_teste.py' -OutFile $script -UseBasicParsing
$py = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
& $py -m py_compile $script
'BAIXADO'
```

Espere `BAIXADO`.

### 3.3 Rodar o teste

O teste precisa de permissão de administrador para editar o arquivo `hosts` do Windows. Por isso ele abre numa janela separada, com o "SIM" do Windows. Troque `<SITE>`, `<PROGRAMA>` e `<MINUTOS>` pela minha resposta (no PADRÃO: `instagram.com`, `notepad.exe` e `3`).

```powershell
$pasta  = Join-Path $env:LOCALAPPDATA 'FocoTotal'
$py     = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
$script = Join-Path $pasta 'focototal_teste.py'
$log    = Join-Path $pasta 'resultado-teste.txt'
Remove-Item $log -ErrorAction SilentlyContinue
$argumentos = @("`"$script`"", '--site', '<SITE>', '--app', '<PROGRAMA>', '--minutos', '<MINUTOS>', '--log', "`"$log`"")
Start-Process $py -Verb RunAs -ArgumentList $argumentos
Start-Sleep 15
if (Test-Path $log) { Get-Content $log -Encoding UTF8 } else { 'SEM LOG' }
```

Logo depois de rodar, me diga: "Clique em **SIM** na pergunta do Windows. Vai abrir uma janela preta: deixe aberta."

- Se o log mostrar `[ok] hosts atualizado`, o teste está rodando. Vá para 3.4.
- Se der `SEM LOG`, pergunte se apareceu a pergunta do Windows e se eu cliquei em SIM. Se apareceu um aviso do **Controle Inteligente de Aplicativos** ou do **Defender**, peça um print e vá para a seção 6.

### 3.4 Eu testo

Um passo por vez, esperando eu dizer "pronto":

1. "Abra o navegador numa **janela anônima** (Ctrl + Shift + N no Chrome e no Edge) e entre em **<SITE>**. Ele **não deve abrir**. Se abrir, espere 30 segundos e tente de novo."
2. "Agora abra o **<PROGRAMA>**. Ele deve **fechar sozinho** em até 2 segundos. Pode tentar mais de uma vez."

Depois de cada passo, leia o log de novo (`Get-Content $log -Encoding UTF8`) e confira:
- Para o programa: deve aparecer `[ok] <PROGRAMA> fechado`.
- Para o site: rode `Resolve-DnsName <SITE> -ErrorAction SilentlyContinue | Select-Object -First 1 IPAddress`. O esperado é `0.0.0.0` ou `::`. Se aparecer outro endereço, o navegador pode estar usando "DNS seguro": peça para eu desligar em Configurações > Privacidade e segurança > Segurança > **Usar DNS seguro** e testar de novo.

### 3.5 Fim do teste

Espere os minutos combinados (ou, se eu quiser parar antes, peça para eu clicar na janela preta e apertar **Ctrl + C**). Depois confira:

```powershell
$log = Join-Path $env:LOCALAPPDATA 'FocoTotal\resultado-teste.txt'
Get-Content $log -Encoding UTF8
$hosts = Get-Content "$env:SystemRoot\System32\drivers\etc\hosts" -Raw
if ($hosts -match 'FocoTotal teste') { 'HOSTS AINDA BLOQUEADO' } else { 'HOSTS LIMPO' }
```

O esperado é a última linha do log `Teste encerrado.` e `HOSTS LIMPO`.

## 4. Quando terminar, me diga em linguagem simples

- se o **site foi bloqueado** e se o **programa foi fechado** (sim ou não para cada um);
- se o Windows mostrou algum aviso do Controle Inteligente de Aplicativos ou do Defender;
- que tudo voltou ao normal e o site já abre de novo;
- que o resultado fica em `%LOCALAPPDATA%\FocoTotal\resultado-teste.txt`, para mandar ao Rafael se ele pedir.

Se os dois funcionaram, diga: "O teste passou: o FocoTotal em Python funciona com o Controle Inteligente ligado."

## 5. Rodar de novo

Para testar outro site ou programa, mande esta mesma mensagem de novo: as etapas 3.1 e 3.2 já estarão feitas e só a 3.3 em diante roda.

## 6. Se der erro

- **Aviso do Controle Inteligente de Aplicativos bloqueando o Python ou o script**: peça um print, anote o nome do arquivo bloqueado que aparece no aviso e diga que o Rafael precisa desse print. Não tente contornar.
- **Aviso do Defender sobre o arquivo hosts** (`HostsFileHijack` ou parecido): peça um print. Não desligue o Defender nem crie exceções.
- **`HOSTS AINDA BLOQUEADO`** depois do fim: rode, numa janela com "SIM" do Windows, o script com `--minutos 0`, que só limpa o bloqueio:
  ```powershell
  $py = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
  $script = Join-Path $env:LOCALAPPDATA 'FocoTotal\focototal_teste.py'
  Start-Process $py -Verb RunAs -ArgumentList @("`"$script`"", '--minutos', '0') -Wait
  ```
  e confira o `hosts` de novo.
- **Internet**: rode a etapa de novo.
- Para remover tudo (só com o meu OK): apague a pasta `%LOCALAPPDATA%\FocoTotal` e, se eu quiser, desinstale o Python com `winget uninstall -e --id Python.Python.3.12`.

Regras:
- Não instale nada além do Python 3.12 oficial (winget) e do script do repositório.
- Não crie `.exe`, `.bat`, `.cmd`, `.ps1` ou `.vbs`.
- Não peça senhas.
- Não desligue antivírus, Defender, SmartScreen nem o Controle Inteligente de Aplicativos, e não mude configurações de segurança do Windows.
