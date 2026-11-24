"""
Serviço do FocoTotal. Roda como SYSTEM por uma tarefa agendada (pythonw.exe -m focototal.servico).

A cada segundo calcula o que deve estar bloqueado e aplica:
- sites: bloco no arquivo hosts;
- apps: encerra os processos;
- navegadores: política de DNS sobre HTTPS desligado;
- anti-burla durante trava: fecha ferramentas do sistema, recria a tarefa agendada se removida,
  e marca HKLM\\SOFTWARE\\FocoTotal\\Locked=1 (o desinstalador recusa).

Atende o app por TCP em 127.0.0.1 (uma linha JSON por pedido e por resposta).
"""

from __future__ import annotations

import json
import logging
import logging.handlers
import os
import socketserver
import sys
import threading
import time
from datetime import datetime

from . import caminhos, nucleo, winapi

log = logging.getLogger("focototal")


# ---------------------------------------------------------------- armazenamento


class Armazenamento:
    def __init__(self, pasta: str):
        self.pasta = pasta
        os.makedirs(pasta, exist_ok=True)

    def _arq(self, nome: str) -> str:
        return os.path.join(self.pasta, nome)

    def carregar(self, nome: str, padrao):
        for caminho in (self._arq(nome), self._arq(nome) + ".bak"):
            try:
                with open(caminho, encoding="utf-8") as f:
                    return json.load(f)
            except FileNotFoundError:
                continue
            except (OSError, ValueError) as e:
                log.warning("Falha ao ler %s: %s", caminho, e)
        return padrao()

    def salvar(self, nome: str, valor) -> None:
        caminho = self._arq(nome)
        tmp = caminho + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(valor, f, ensure_ascii=False, indent=2)
        if os.path.exists(caminho):
            try:
                os.replace(caminho, caminho + ".bak")
            except OSError:
                pass
        os.replace(tmp, caminho)


def proteger_pasta(pasta: str) -> None:
    """SYSTEM com controle total, administradores só leitura (via icacls, ferramenta do Windows)."""
    if not winapi.IS_WINDOWS:
        return
    r = winapi.run(["icacls", pasta, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F",
                    "/grant:r", "*S-1-5-32-544:(OI)(CI)RX", "/T", "/Q"])
    if r.returncode != 0:
        log.warning("icacls falhou: %s", r.stderr[:300])


# ---------------------------------------------------------------- aplicação das regras


class Hosts:
    def __init__(self, caminho: str):
        self.caminho = caminho

    def aplicar(self, sites) -> None:
        try:
            nomes = nucleo.expand_for_hosts(sites)
            try:
                with open(self.caminho, encoding="utf-8", errors="replace") as f:
                    atual = f.read()
            except FileNotFoundError:
                atual = ""
            desejado = nucleo.hosts_apply_block(atual, nomes)
            if desejado.replace("\r\n", "\n") == atual.replace("\r\n", "\n"):
                return
            if winapi.IS_WINDOWS and os.path.exists(self.caminho):
                os.chmod(self.caminho, 0o666)  # tira o "somente leitura"
            with open(self.caminho, "w", encoding="utf-8", newline="") as f:
                f.write(desejado)
            winapi.flush_dns()
            log.info("hosts atualizado: %d nomes bloqueados", len(nomes))
        except OSError as e:
            log.warning("Falha ao atualizar o hosts: %s", e)


class Processos:
    def aplicar(self, regras: list[str], fechar_ferramentas: bool) -> list[str]:
        fechados: list[str] = []
        if not regras and not fechar_ferramentas:
            return fechados
        meu_pid = os.getpid()
        com_caminho = any("\\" in r or "/" in r for r in regras)
        for pid, pai, nome in winapi.list_processes():
            if pid <= 4 or pid == meu_pid or pai == meu_pid or nucleo.is_protected(nome):
                continue
            caminho = winapi.process_path(pid) if com_caminho else None
            alvo = any(nucleo.app_matches(r, nome, caminho) for r in regras) or (
                fechar_ferramentas and nome.lower() in nucleo.SYSTEM_TOOLS)
            if alvo and winapi.kill(pid):
                fechados.append(nome)
        if fechados:
            log.info("Encerrados: %s", ", ".join(fechados))
        return fechados


class PoliticasNavegador:
    """Desliga o DoH só onde não há política definida (GPO/Intune não é sobrescrito)."""

    POLITICAS = [
        (r"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off"),
        (r"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off"),
        (r"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off"),
        (r"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0),
        (r"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1),
    ]

    def __init__(self):
        self._ultimo = None
        self._quando = 0.0

    def aplicar(self, ligado: bool) -> None:
        if not winapi.IS_WINDOWS or (self._ultimo == ligado and time.monotonic() - self._quando < 60):
            return
        self._ultimo, self._quando = ligado, time.monotonic()
        try:
            nossas = set(winapi.reg_get(caminhos.CHAVE_REGISTRO, "PoliticasCriadas") or [])
            for chave, nome, valor in self.POLITICAS:
                ident = f"{chave}\\{nome}"
                if ligado:
                    atual = winapi.reg_get(chave, nome)
                    if atual is None or (ident in nossas and atual != valor):
                        winapi.reg_set(chave, nome, valor)
                        nossas.add(ident)
                elif ident in nossas:
                    winapi.reg_delete(chave, nome)
                    nossas.discard(ident)
            winapi.reg_set(caminhos.CHAVE_REGISTRO, "PoliticasCriadas", sorted(nossas) or [""])
        except OSError as e:
            log.warning("Falha nas políticas de navegador: %s", e)


class AntiBurla:
    def __init__(self):
        self._travado = None
        self._ultima_checagem = 0.0

    def aplicar(self, travado: bool) -> None:
        if not winapi.IS_WINDOWS:
            return
        if travado != self._travado:
            try:
                winapi.reg_set(caminhos.CHAVE_REGISTRO, "Locked", 1 if travado else 0)
            except OSError as e:
                log.warning("Falha ao gravar Locked: %s", e)
            self._travado = travado
            log.info("Proteção contra burla %s", "ativada" if travado else "desativada")
        # Durante a trava, recria ou reativa a tarefa agendada se alguém mexer nela.
        if travado and time.monotonic() - self._ultima_checagem > 30:
            self._ultima_checagem = time.monotonic()
            xml = os.path.join(caminhos.PASTA_PROGRAMA, "tarefa.xml")
            consulta = winapi.run(["schtasks", "/Query", "/TN", caminhos.NOME_TAREFA])
            if consulta.returncode != 0 and os.path.exists(xml):
                winapi.run(["schtasks", "/Create", "/TN", caminhos.NOME_TAREFA, "/XML", xml, "/F"])
                log.warning("Tarefa agendada recriada")
            else:
                winapi.run(["schtasks", "/Change", "/TN", caminhos.NOME_TAREFA, "/ENABLE"])


# ---------------------------------------------------------------- coordenador


class Coordenador:
    def __init__(self, armazenamento: Armazenamento):
        self.arm = armazenamento
        self.trava = threading.Lock()
        self.config = armazenamento.carregar("config.json", nucleo.default_config)
        if nucleo.sanitize_config(self.config):
            self.config = nucleo.default_config()
        self.estado = armazenamento.carregar("state.json", nucleo.default_state)
        self.estado.setdefault("blocks", [])
        self.estado.setdefault("pomodoro", None)
        self.stats = armazenamento.carregar("stats.json", lambda: {"days": []})
        self.relogio = nucleo.ClockGuard()
        self.politica = nucleo.compute_policy(self.config, self.estado, time.time(), datetime.now())
        self.hosts = Hosts(caminhos.HOSTS)
        self.processos = Processos()
        self.navegador = PoliticasNavegador()
        self.antiburla = AntiBurla()
        self._segundos_foco = 0
        self._salvo_estado = 0.0
        self._salvo_stats = 0.0
        self._stats_sujo = False
        self.arm.salvar("config.json", self.config)

    def passo(self) -> None:
        with self.trava:
            agora = time.time()
            mudou = nucleo.ClockGuard.compensate(self.estado, self.relogio.observe(agora, time.monotonic()))
            mudou |= nucleo.tick(self.estado, agora)
            self.estado["lastSeen"] = agora
            self.politica = pol = nucleo.compute_policy(self.config, self.estado, agora, datetime.now())
            cfg = self.config["settings"]
            if mudou or time.monotonic() - self._salvo_estado > 30:
                self.arm.salvar("state.json", self.estado)
                self._salvo_estado = time.monotonic()

        travado = bool(pol["locked"])
        self.hosts.aplicar(pol["sites"])
        fechados = self.processos.aplicar(pol["apps"], cfg["protectSystemTools"] and travado)
        self.navegador.aplicar(cfg["disableBrowserDoH"])
        self.antiburla.aplicar(cfg["protectUninstall"] and travado)

        with self.trava:
            agora_local = datetime.now()
            for nome in fechados:
                nucleo.stats_record_app(self.stats, agora_local, nome)
                self._stats_sujo = True
            if pol["active"]:
                self._segundos_foco += 1
                if self._segundos_foco >= 60:
                    self._segundos_foco = 0
                    nucleo.stats_today(self.stats, agora_local)["focusMinutes"] += 1
                    self._stats_sujo = True
            if self._stats_sujo and time.monotonic() - self._salvo_stats > 60:
                self.arm.salvar("stats.json", self.stats)
                self._salvo_stats = time.monotonic()
                self._stats_sujo = False

    def _recalcular(self) -> None:
        self.politica = nucleo.compute_policy(self.config, self.estado, time.time(), datetime.now())

    def status(self) -> dict:
        hoje = nucleo.stats_today(self.stats, datetime.now())
        return {
            "config": self.config,
            "blocks": [nucleo.public_block(b) for b in self.estado["blocks"]],
            "pomodoro": self.estado.get("pomodoro"),
            "active": self.politica["active"],
            "locked": self.politica["locked"],
            "version": caminhos.VERSAO,
            "blockedToday": sum(hoje["blockedApps"].values()),
        }

    def atender(self, pedido: dict) -> dict:
        with self.trava:
            resposta = self._atender(pedido)
            # Cópia profunda, porque a resposta é serializada fora da trava.
            return json.loads(json.dumps(resposta))

    def _atender(self, p: dict) -> dict:
        cmd = p.get("cmd")
        if cmd == "status":
            return {"ok": True, "status": self.status()}
        if cmd == "stats":
            return {"ok": True, "stats": self.stats}

        if cmd == "save-config":
            nova = p.get("config")
            erro = nucleo.sanitize_config(nova) or nucleo.validate_config_change(
                self.config, nova, self.politica, datetime.now())
            if erro:
                return {"ok": False, "message": erro}
            self.config = nova
            self.arm.salvar("config.json", self.config)
            self._recalcular()
            return {"ok": True, "message": "Configuração salva.", "status": self.status()}

        if cmd == "start-block":
            req = p.get("start") or {}
            erro = nucleo.validate_start(self.config, req)
            if erro:
                return {"ok": False, "message": erro}
            self.estado["blocks"].append(nucleo.new_block(req, time.time()))
            self.arm.salvar("state.json", self.estado)
            self._recalcular()
            return {"ok": True, "message": "Bloqueio iniciado.", "status": self.status()}

        if cmd == "stop-block":
            bloco = next((b for b in self.estado["blocks"] if b["id"] == p.get("id")), None)
            if bloco is None:
                return {"ok": False, "message": "Bloqueio não encontrado."}
            r = nucleo.try_stop(bloco, p.get("answer"))
            if r["outcome"] == "stopped":
                self.estado["blocks"].remove(bloco)
            self.arm.salvar("state.json", self.estado)
            self._recalcular()
            return {"ok": r["outcome"] == "stopped", **r, "status": self.status()}

        if cmd == "start-pomodoro":
            q = p.get("pomodoro") or {}
            if q.get("listId") not in {l["id"] for l in self.config["lists"]}:
                return {"ok": False, "message": "Escolha uma lista."}
            if self.estado.get("pomodoro"):
                return {"ok": False, "message": "Já existe um Pomodoro em andamento."}
            foco, pausa, ciclos = int(q.get("focus", 25)), int(q.get("pause", 5)), int(q.get("cycles", 4))
            if not (1 <= foco <= 240 and 1 <= pausa <= 120 and 1 <= ciclos <= 20):
                return {"ok": False, "message": "Valores de Pomodoro fora do limite."}
            trava = "timer" if q.get("lock") == "timer" else "none"
            self.estado["pomodoro"] = {"listId": q["listId"], "focus": foco, "pause": pausa, "cycles": ciclos,
                                       "cycle": 1, "phase": "focus", "phaseEnd": time.time() + foco * 60,
                                       "lock": trava}
            self.arm.salvar("state.json", self.estado)
            self._recalcular()
            return {"ok": True, "message": "Pomodoro iniciado.", "status": self.status()}

        if cmd == "stop-pomodoro":
            pom = self.estado.get("pomodoro")
            if not pom:
                return {"ok": False, "message": "Nenhum Pomodoro em andamento."}
            if pom["lock"] != "none":
                return {"ok": False, "message": "Este Pomodoro tem trava de tempo e vai até o fim."}
            self.estado["pomodoro"] = None
            self.arm.salvar("state.json", self.estado)
            self._recalcular()
            return {"ok": True, "message": "Pomodoro encerrado.", "status": self.status()}

        return {"ok": False, "message": "Comando desconhecido."}


# ---------------------------------------------------------------- servidor local


class _Atendente(socketserver.StreamRequestHandler):
    def handle(self):
        self.request.settimeout(15)
        try:
            linha = self.rfile.readline(8 * 1024 * 1024)
            pedido = json.loads(linha.decode("utf-8"))
            resposta = self.server.coordenador.atender(pedido)
        except Exception as e:  # pedido inválido não derruba o serviço
            resposta = {"ok": False, "message": f"Pedido inválido: {e}"}
        self.wfile.write((json.dumps(resposta, ensure_ascii=False) + "\n").encode("utf-8"))


class _Servidor(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = False


def configurar_log() -> None:
    os.makedirs(caminhos.PASTA_DADOS, exist_ok=True)
    h = logging.handlers.RotatingFileHandler(os.path.join(caminhos.PASTA_DADOS, "servico.log"),
                                             maxBytes=1_000_000, backupCount=2, encoding="utf-8")
    h.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    log.addHandler(h)
    log.setLevel(logging.INFO)


def main() -> int:
    configurar_log()
    try:
        servidor = _Servidor(("127.0.0.1", caminhos.PORTA), _Atendente)
    except OSError:
        # Já existe um serviço rodando (a tarefa agendada tenta de minuto em minuto).
        return 0
    proteger_pasta(caminhos.PASTA_DADOS)
    coord = Coordenador(Armazenamento(caminhos.PASTA_DADOS))
    servidor.coordenador = coord
    threading.Thread(target=servidor.serve_forever, daemon=True).start()
    log.info("FocoTotal %s iniciado (Python %s)", caminhos.VERSAO, sys.version.split()[0])
    proximo = time.monotonic()
    while True:
        try:
            coord.passo()
        except Exception:
            log.exception("Erro no laço principal")
        proximo += 1
        time.sleep(max(0.0, proximo - time.monotonic()))
        if proximo < time.monotonic() - 5:
            proximo = time.monotonic()


if __name__ == "__main__":
    sys.exit(main())
