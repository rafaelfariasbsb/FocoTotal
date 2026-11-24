"""
FocoTotal - teste de viabilidade em Python (só biblioteca padrão).

Objetivo: verificar se o Controle Inteligente de Aplicativos do Windows 11
deixa um script Python bloquear um site e fechar um app.

Uso (PowerShell como administrador):
    python focototal_teste.py                      # instagram.com e notepad.exe por 2 minutos
    python focototal_teste.py --site x.com --app notepad.exe --minutos 5

Ao terminar (ou com Ctrl+C), o bloqueio no hosts é removido.
"""

import argparse
import ctypes
import os
import subprocess
import sys
import time

HOSTS = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "drivers", "etc", "hosts")
INICIO = "# >>> FocoTotal teste >>>"
FIM = "# <<< FocoTotal teste <<<"
SUBDOMINIOS = ("", "www.", "m.", "web.", "api.")
SEM_JANELA = 0x08000000  # CREATE_NO_WINDOW


ARQUIVO_LOG: str | None = None


def log(msg: str) -> None:
    """Mostra na tela e, se pedido com --log, grava no arquivo (para o instalador conferir)."""
    print(msg, flush=True)
    if ARQUIVO_LOG:
        with open(ARQUIVO_LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + msg + "\n")


def eh_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def limpar_dns() -> None:
    try:
        ctypes.windll.dnsapi.DnsFlushResolverCache()
    except Exception:
        pass


def ler_hosts() -> str:
    with open(HOSTS, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def remover_bloco(texto: str) -> str:
    saida, dentro = [], False
    for linha in texto.splitlines():
        if linha.strip() == INICIO:
            dentro = True
            continue
        if linha.strip() == FIM:
            dentro = False
            continue
        if not dentro:
            saida.append(linha)
    while saida and not saida[-1].strip():
        saida.pop()
    return "\n".join(saida) + "\n"


def aplicar_hosts(site: str | None) -> None:
    texto = remover_bloco(ler_hosts())
    if site:
        linhas = [INICIO]
        for sub in SUBDOMINIOS:
            linhas.append(f"0.0.0.0 {sub}{site}")
            linhas.append(f":: {sub}{site}")
        linhas.append(FIM)
        texto = texto + "\n" + "\n".join(linhas) + "\n"
    with open(HOSTS, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(texto)
    limpar_dns()


def fechar_app(nome_exe: str) -> int:
    """Fecha o processo pelo nome usando o taskkill do Windows. Retorna 1 se fechou algo."""
    r = subprocess.run(
        ["taskkill", "/F", "/T", "/IM", nome_exe],
        capture_output=True, creationflags=SEM_JANELA,
    )
    return 1 if r.returncode == 0 else 0


def normalizar_site(s: str) -> str:
    s = s.strip().lower()
    for prefixo in ("https://", "http://"):
        if s.startswith(prefixo):
            s = s[len(prefixo):]
    s = s.split("/")[0]
    return s[4:] if s.startswith("www.") else s


def main() -> int:
    p = argparse.ArgumentParser(description="Teste de bloqueio do FocoTotal em Python")
    p.add_argument("--site", default="instagram.com")
    p.add_argument("--app", default="notepad.exe")
    p.add_argument("--minutos", type=float, default=2)
    p.add_argument("--log", help="arquivo onde gravar o resultado do teste")
    a = p.parse_args()
    global ARQUIVO_LOG
    ARQUIVO_LOG = a.log

    if sys.platform != "win32":
        log("Este teste é só para Windows.")
        return 1
    if not eh_admin():
        log("Abra o PowerShell como ADMINISTRADOR (precisa para editar o arquivo hosts).")
        return 1

    site = normalizar_site(a.site)
    app = a.app if a.app.lower().endswith(".exe") else a.app + ".exe"
    fim = time.monotonic() + a.minutos * 60

    log(f"Python {sys.version.split()[0]} em {sys.executable}")
    log(f"Bloqueando {site} e fechando {app} por {a.minutos:g} min. Ctrl+C para parar antes.")
    fechados = 0
    try:
        aplicar_hosts(site)
        log("  [ok] hosts atualizado. Abra o site numa aba anônima para testar.")
        while time.monotonic() < fim:
            # Reaplica se alguém mexer no hosts.
            if INICIO not in ler_hosts():
                aplicar_hosts(site)
                log("  [!] hosts foi alterado; bloqueio reaplicado.")
            n = fechar_app(app)
            if n:
                fechados += n
                log(f"  [ok] {app} fechado ({fechados} no total).")
            time.sleep(1)
    except KeyboardInterrupt:
        log("\nInterrompido.")
    except Exception as e:  # registra o erro para o instalador ler
        log(f"ERRO: {type(e).__name__}: {e}")
    finally:
        aplicar_hosts(None)
        log("Bloqueio removido do hosts. Teste encerrado.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
