"""
Instala, atualiza e remove o FocoTotal (Windows, como administrador).

    python -m focototal.instalador instalar
    python -m focototal.instalador desinstalar [--apagar-dados]
    python -m focototal.instalador status

Instalar:
- copia o pacote para C:\\Program Files\\FocoTotal (só administradores alteram);
- cria a tarefa agendada "FocoTotal" (SYSTEM, ao ligar e a cada minuto se não estiver rodando);
- cria o atalho "FocoTotal" no Menu Iniciar e a entrada em Configurações > Aplicativos.

Nada de .exe, .bat ou .ps1: só o Python oficial (assinado) e ferramentas do próprio Windows.
"""

from __future__ import annotations

import argparse
import ctypes
import os
import shutil
import sys
import time
from xml.sax.saxutils import escape

from . import caminhos, cliente, nucleo, winapi

CHAVE_DESINSTALAR = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FocoTotal"
ORIGEM = os.path.dirname(os.path.abspath(__file__))


def _pythonw() -> str:
    pasta = os.path.dirname(sys.executable)
    w = os.path.join(pasta, "pythonw.exe")
    return w if os.path.exists(w) else sys.executable


def _python() -> str:
    pasta = os.path.dirname(sys.executable)
    p = os.path.join(pasta, "python.exe")
    return p if os.path.exists(p) else sys.executable


def _menu_iniciar() -> str:
    return os.path.join(os.environ.get("ProgramData", r"C:\ProgramData"), "Microsoft", "Windows", "Start Menu",
                        "Programs")


def _xml_tarefa(pythonw: str) -> str:
    return f"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>FocoTotal: aplica os bloqueios de sites e aplicativos.</Description>
    <URI>\\{caminhos.NOME_TAREFA}</URI>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </BootTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>999</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escape(pythonw)}</Command>
      <Arguments>-E -s -m focototal.servico</Arguments>
      <WorkingDirectory>{escape(caminhos.PASTA_PROGRAMA)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"""


LANCADOR_APP = '''import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from focototal.app import main
main()
'''

LANCADOR_DESINSTALAR = '''import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from focototal.instalador import desinstalar_com_janela
desinstalar_com_janela()
'''


def _travado() -> bool:
    """Há bloqueio travado com a proteção de desinstalação ligada?"""
    if winapi.reg_get(caminhos.CHAVE_REGISTRO, "Locked") != 1:
        return False
    try:
        return bool(cliente.enviar({"cmd": "status"}, timeout=3)["status"]["locked"])
    except (cliente.ServicoIndisponivel, KeyError):
        pass
    # Serviço parado: confere o estado salvo.
    import json
    from datetime import datetime
    try:
        with open(os.path.join(caminhos.PASTA_DADOS, "config.json"), encoding="utf-8") as f:
            config = json.load(f)
        with open(os.path.join(caminhos.PASTA_DADOS, "state.json"), encoding="utf-8") as f:
            estado = json.load(f)
        return bool(nucleo.compute_policy(config, estado, time.time(), datetime.now())["locked"])
    except (OSError, ValueError, KeyError):
        return False


def _criar_atalho(destino: str, alvo: str, argumentos: str, pasta: str) -> bool:
    """Cria um .lnk usando o PowerShell (comando, não arquivo .ps1)."""
    q = lambda s: s.replace("'", "''")
    cmd = (f"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{q(destino)}');"
           f"$s.TargetPath='{q(alvo)}';$s.Arguments='{q(argumentos)}';$s.WorkingDirectory='{q(pasta)}';"
           f"$s.Description='Bloqueio de sites e aplicativos';$s.Save()")
    r = winapi.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd])
    return r.returncode == 0 and os.path.exists(destino)


def _servico_responde(segundos: int) -> bool:
    fim = time.monotonic() + segundos
    while time.monotonic() < fim:
        try:
            if cliente.enviar({"cmd": "status"}, timeout=2).get("ok"):
                return True
        except cliente.ServicoIndisponivel:
            pass
        time.sleep(1)
    return False


def _parar_servico() -> None:
    winapi.run(["schtasks", "/End", "/TN", caminhos.NOME_TAREFA])
    fim = time.monotonic() + 15
    while time.monotonic() < fim:
        try:
            cliente.enviar({"cmd": "status"}, timeout=1)
        except cliente.ServicoIndisponivel:
            return
        time.sleep(1)


def instalar() -> int:
    if not winapi.IS_WINDOWS:
        print("O FocoTotal só roda no Windows.")
        return 1
    if not winapi.is_admin():
        print("Rode como ADMINISTRADOR.")
        return 1
    if _travado():
        print("Há um bloqueio travado em andamento. Instale ou atualize depois que ele terminar.")
        return 1

    pythonw = _pythonw()
    perfil = os.environ.get("USERPROFILE", "").lower()
    if perfil and pythonw.lower().startswith(perfil):
        print("AVISO: o Python está instalado só para o seu usuário. Para o serviço ficar protegido, instale o "
              "Python para todos os usuários (winget install -e --id Python.Python.3.12 --scope machine).")

    print("1/5 Parando a versão anterior, se houver…")
    _parar_servico()

    print(f"2/5 Copiando arquivos para {caminhos.PASTA_PROGRAMA}…")
    destino_pacote = os.path.join(caminhos.PASTA_PROGRAMA, "focototal")
    if os.path.abspath(ORIGEM).lower() != os.path.abspath(destino_pacote).lower():
        shutil.rmtree(destino_pacote, ignore_errors=True)
        shutil.copytree(ORIGEM, destino_pacote, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    for nome, conteudo in (("FocoTotal.pyw", LANCADOR_APP), ("desinstalar.pyw", LANCADOR_DESINSTALAR)):
        with open(os.path.join(caminhos.PASTA_PROGRAMA, nome), "w", encoding="utf-8") as f:
            f.write(conteudo)
    os.makedirs(caminhos.PASTA_DADOS, exist_ok=True)

    print("3/5 Criando a tarefa agendada (roda como SYSTEM ao ligar o PC)…")
    xml = os.path.join(caminhos.PASTA_PROGRAMA, "tarefa.xml")
    with open(xml, "w", encoding="utf-16") as f:
        f.write(_xml_tarefa(pythonw))
    r = winapi.run(["schtasks", "/Create", "/TN", caminhos.NOME_TAREFA, "/XML", xml, "/F"])
    if r.returncode != 0:
        print("ERRO ao criar a tarefa:", r.stderr.decode("cp850", "replace") or r.stdout.decode("cp850", "replace"))
        return 1

    print("4/5 Criando atalho no Menu Iniciar e entrada em Aplicativos…")
    lanc = os.path.join(caminhos.PASTA_PROGRAMA, "FocoTotal.pyw")
    atalho = os.path.join(_menu_iniciar(), "FocoTotal.lnk")
    if not _criar_atalho(atalho, pythonw, f'"{lanc}"', caminhos.PASTA_PROGRAMA):
        print("AVISO: não consegui criar o atalho. Abra o app por:", lanc)
    winapi.reg_set(CHAVE_DESINSTALAR, "DisplayName", "FocoTotal")
    winapi.reg_set(CHAVE_DESINSTALAR, "DisplayVersion", caminhos.VERSAO)
    winapi.reg_set(CHAVE_DESINSTALAR, "Publisher", "FocoTotal")
    winapi.reg_set(CHAVE_DESINSTALAR, "InstallLocation", caminhos.PASTA_PROGRAMA)
    winapi.reg_set(CHAVE_DESINSTALAR, "DisplayIcon", pythonw)
    winapi.reg_set(CHAVE_DESINSTALAR, "UninstallString",
                   f'"{pythonw}" "{os.path.join(caminhos.PASTA_PROGRAMA, "desinstalar.pyw")}"')
    winapi.reg_set(CHAVE_DESINSTALAR, "NoModify", 1)
    winapi.reg_set(CHAVE_DESINSTALAR, "NoRepair", 1)

    print("5/5 Iniciando o serviço…")
    winapi.run(["schtasks", "/Run", "/TN", caminhos.NOME_TAREFA])
    if _servico_responde(30):
        print("INSTALADO")
        return 0
    print(f"ERRO: o serviço não respondeu. Veja {os.path.join(caminhos.PASTA_DADOS, 'servico.log')}")
    return 1


def desinstalar(apagar_dados: bool = False) -> tuple[int, str]:
    if not winapi.IS_WINDOWS:
        return 1, "O FocoTotal só roda no Windows."
    if not winapi.is_admin():
        return 1, "Rode como ADMINISTRADOR."
    if _travado():
        return 1, ("O FocoTotal está com um bloqueio travado em andamento. Ele só pode ser desinstalado "
                   "depois que o bloqueio terminar.")

    _parar_servico()
    winapi.run(["schtasks", "/Delete", "/TN", caminhos.NOME_TAREFA, "/F"])

    # Desfaz o que o serviço aplicou.
    try:
        with open(caminhos.HOSTS, encoding="utf-8", errors="replace") as f:
            atual = f.read()
        limpo = nucleo.hosts_remove_block(atual)
        if limpo != atual:
            os.chmod(caminhos.HOSTS, 0o666)
            with open(caminhos.HOSTS, "w", encoding="utf-8", newline="") as f:
                f.write(limpo)
            winapi.flush_dns()
    except OSError:
        pass
    from .servico import PoliticasNavegador
    PoliticasNavegador().aplicar(False)

    for caminho in (os.path.join(_menu_iniciar(), "FocoTotal.lnk"),):
        try:
            os.remove(caminho)
        except OSError:
            pass
    _apagar_chave(CHAVE_DESINSTALAR)
    _apagar_chave(caminhos.CHAVE_REGISTRO)
    os.chdir(os.environ.get("SystemRoot", "C:\\"))
    shutil.rmtree(caminhos.PASTA_PROGRAMA, ignore_errors=True)
    if apagar_dados:
        winapi.run(["icacls", caminhos.PASTA_DADOS, "/reset", "/T", "/Q"])
        shutil.rmtree(caminhos.PASTA_DADOS, ignore_errors=True)
    return 0, "FocoTotal desinstalado." + ("" if apagar_dados else f" Os dados ficaram em {caminhos.PASTA_DADOS}.")


def _apagar_chave(caminho: str) -> None:
    if winapi.IS_WINDOWS:
        import winreg
        try:
            winreg.DeleteKeyEx(winreg.HKEY_LOCAL_MACHINE, caminho, winreg.KEY_WOW64_64KEY)
        except OSError:
            pass


def desinstalar_com_janela() -> None:
    """Usado por Configurações > Aplicativos > FocoTotal > Desinstalar."""
    import tkinter as tk
    from tkinter import messagebox
    raiz = tk.Tk()
    raiz.withdraw()
    if not winapi.is_admin():
        # Pede o "SIM" do Windows e roda de novo como administrador.
        ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, f'"{sys.argv[0]}"', None, 1)
        return
    if not messagebox.askyesno("FocoTotal", "Desinstalar o FocoTotal?"):
        return
    codigo, msg = desinstalar()
    (messagebox.showinfo if codigo == 0 else messagebox.showwarning)("FocoTotal", msg)


def status() -> int:
    try:
        s = cliente.enviar({"cmd": "status"})["status"]
    except cliente.ServicoIndisponivel:
        print("Serviço: PARADO")
        return 1
    print(f"Serviço: RODANDO (versão {s['version']})")
    nomes = {l["id"]: l["name"] for l in s["config"]["lists"]}
    print("Bloqueando:", ", ".join(nomes.get(i, i) for i in s["active"]) or "nada")
    print("Travado:", "sim" if s["locked"] else "não")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(prog="python instalar.py")
    p.add_argument("--log", help="grava a saída também neste arquivo (útil ao rodar com o SIM do Windows)")
    sub = p.add_subparsers(dest="acao", required=True)
    sub.add_parser("instalar")
    d = sub.add_parser("desinstalar")
    d.add_argument("--apagar-dados", action="store_true")
    sub.add_parser("status")
    a = p.parse_args()
    if a.log:
        sys.stdout = _Duplo(sys.stdout, open(a.log, "w", encoding="utf-8"))
    try:
        return _executar(a)
    except Exception as e:
        print(f"ERRO: {type(e).__name__}: {e}")
        return 1
    finally:
        sys.stdout.flush()


class _Duplo:
    def __init__(self, *saidas):
        self.saidas = [s for s in saidas if s is not None]

    def write(self, texto):
        for s in self.saidas:
            s.write(texto)
            s.flush()

    def flush(self):
        for s in self.saidas:
            s.flush()


def _executar(a) -> int:
    if a.acao == "instalar":
        return instalar()
    if a.acao == "desinstalar":
        codigo, msg = desinstalar(a.apagar_dados)
        print(msg)
        if codigo == 0:
            print("DESINSTALADO")
        return codigo
    return status()


if __name__ == "__main__":
    sys.exit(main())
