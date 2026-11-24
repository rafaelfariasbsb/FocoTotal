import os
from pathlib import Path
import tempfile
import time
import unittest
from datetime import datetime

from focototal import nucleo

NOW = datetime(2026, 11, 23, 10, 0).timestamp()  # segunda-feira 10h (hora local)
LOCAL = datetime(2026, 11, 23, 10, 0)


def config():
    return {
        "lists": [
            {"id": "a", "name": "A", "sites": ["youtube.com"], "apps": ["steam.exe"]},
            {"id": "b", "name": "B", "sites": ["x.com"], "apps": []},
        ],
        "schedules": [],
        "settings": nucleo.default_settings(),
    }


class Sites(unittest.TestCase):
    def test_normalize(self):
        casos = {
            "youtube.com": "youtube.com",
            "https://www.YouTube.com/watch?v=1": "youtube.com",
            "*.reddit.com": "reddit.com",
            "http://user@x.com:8080/home": "x.com",
            "  instagram.com/  ": "instagram.com",
            "sub.domain.com.br": "sub.domain.com.br",
        }
        for entrada, esperado in casos.items():
            self.assertEqual(nucleo.normalize_site(entrada), esperado, entrada)
        for invalido in ("", "localhost", "não é site", "exa_mple.com"):
            self.assertIsNone(nucleo.normalize_site(invalido), invalido)

    def test_hosts_idempotente_e_preserva(self):
        original = "# hosts\r\n127.0.0.1 intranet.local\r\n"
        nomes = nucleo.expand_for_hosts(["x.com", "https://www.x.com"])
        self.assertIn("www.x.com", nomes)
        self.assertEqual(len(nomes), len(set(nomes)))
        uma = nucleo.hosts_apply_block(original, nomes)
        self.assertEqual(uma, nucleo.hosts_apply_block(uma, nomes))
        self.assertIn("0.0.0.0 x.com", uma)
        self.assertIn("127.0.0.1 intranet.local", uma)
        self.assertEqual(nucleo.hosts_remove_block(uma), original)
        self.assertNotIn("FocoTotal", nucleo.hosts_apply_block(uma, []))


class Apps(unittest.TestCase):
    def test_matches(self):
        m = nucleo.app_matches
        self.assertTrue(m("steam.exe", "steam", None))
        self.assertTrue(m("Steam", "steam.exe", None))
        self.assertFalse(m("steam.exe", "steamwebhelper.exe", None))
        self.assertTrue(m("C:\\Jogos\\*", "x.exe", "C:\\Jogos\\Sub\\jogo.exe"))
        self.assertFalse(m("C:\\Jogos\\", "x.exe", "C:\\JogosX\\jogo.exe"))
        self.assertTrue(m("C:\\Apps\\discord.exe", "discord.exe", "c:\\apps\\Discord.exe"))
        self.assertFalse(m("C:\\Apps\\discord.exe", "discord.exe", None))

    def test_protegidos(self):
        for nome in ("explorer.exe", "svchost", "pythonw.exe"):
            self.assertTrue(nucleo.is_protected(nome))
            self.assertFalse(nucleo.app_matches(nome, nome, None))


class Agenda(unittest.TestCase):
    def test_horarios(self):
        def ativo(ini, fim, h, dia=23):
            e = {"listId": "a", "days": [0], "start": ini, "end": fim}
            return nucleo.schedule_active(e, datetime(2026, 11, dia, h, 0))

        self.assertTrue(ativo("09:00", "18:00", 10))
        self.assertFalse(ativo("09:00", "18:00", 18))
        self.assertTrue(ativo("22:00", "06:00", 23))
        self.assertFalse(ativo("22:00", "06:00", 5))      # segunda 5h: começou no domingo (não marcado)
        self.assertTrue(ativo("22:00", "06:00", 5, dia=24))  # terça 5h: continuação de segunda


class Politica(unittest.TestCase):
    def test_compute_e_tick(self):
        estado = {"blocks": [
            {"id": "1", "listIds": ["a"], "start": NOW - 60, "end": NOW + 60, "lock": "timer"},
            {"id": "2", "listIds": ["b"], "start": NOW - 60, "end": NOW - 1, "lock": "none"},
        ], "pomodoro": None}
        pol = nucleo.compute_policy(config(), estado, NOW, LOCAL)
        self.assertEqual(pol["active"], ["a"])
        self.assertEqual(pol["locked"], ["a"])
        self.assertIn("youtube.com", pol["sites"])
        self.assertIn("steam.exe", pol["apps"])
        self.assertTrue(nucleo.tick(estado, NOW))
        self.assertEqual(len(estado["blocks"]), 1)

    def test_pomodoro(self):
        estado = {"blocks": [], "pomodoro": {"listId": "a", "focus": 25, "pause": 5, "cycles": 2, "cycle": 1,
                                              "phase": "focus", "phaseEnd": NOW - 1, "lock": "none"}}
        nucleo.tick(estado, NOW)
        self.assertEqual(estado["pomodoro"]["phase"], "break")
        self.assertEqual(nucleo.compute_policy(config(), estado, NOW, LOCAL)["active"], [])
        nucleo.tick(estado, NOW + 5 * 60)
        self.assertEqual((estado["pomodoro"]["phase"], estado["pomodoro"]["cycle"]), ("focus", 2))
        nucleo.tick(estado, NOW + 31 * 60)
        self.assertIsNone(estado["pomodoro"])

    def test_trava_so_endurece(self):
        atual = config()
        estado = {"blocks": [{"id": "1", "listIds": ["a"], "start": NOW, "end": NOW + 3600, "lock": "timer"}],
                  "pomodoro": None}
        pol = nucleo.compute_policy(atual, estado, NOW, LOCAL)
        v = lambda nova: nucleo.validate_config_change(atual, nova, pol, LOCAL)

        c = config(); c["lists"][0]["sites"].append("twitch.tv"); self.assertIsNone(v(c))
        c = config(); c["lists"][0]["sites"][0] = "https://www.youtube.com/"; self.assertIsNone(v(c))
        c = config(); c["lists"][0]["sites"].clear(); self.assertIsNotNone(v(c))
        c = config(); c["lists"][0]["apps"].clear(); self.assertIsNotNone(v(c))
        c = config(); del c["lists"][0]; self.assertIsNotNone(v(c))
        c = config(); c["lists"][1]["sites"].clear(); self.assertIsNone(v(c))
        c = config(); c["settings"]["protectSystemTools"] = False; self.assertIsNotNone(v(c))

    def test_agenda_travada(self):
        atual = config()
        atual["schedules"].append({"id": "s", "listId": "a", "days": [0], "start": "09:00", "end": "18:00",
                                   "locked": True, "enabled": True})
        pol = nucleo.compute_policy(atual, {"blocks": [], "pomodoro": None}, NOW, LOCAL)
        self.assertIn("a", pol["locked"])
        sem = config()
        self.assertIsNotNone(nucleo.validate_config_change(atual, sem, pol, LOCAL))
        noite = datetime(2026, 11, 23, 20, 0)
        pol2 = nucleo.compute_policy(atual, {"blocks": [], "pomodoro": None}, noite.timestamp(), noite)
        self.assertIsNone(nucleo.validate_config_change(atual, sem, pol2, noite))

    def test_validate_start(self):
        c = config()
        self.assertIsNotNone(nucleo.validate_start(c, {"minutes": 10}))
        self.assertIsNotNone(nucleo.validate_start(c, {"listIds": ["z"], "minutes": 10}))
        self.assertIsNotNone(nucleo.validate_start(c, {"listIds": ["a"], "minutes": 0}))
        self.assertIsNotNone(nucleo.validate_start(c, {"listIds": ["a"], "minutes": 10, "lock": "password"}))
        self.assertIsNone(nucleo.validate_start(c, {"listIds": ["a"], "minutes": 10, "lock": "text", "textLength": 50}))

    def test_sanitize(self):
        c = config(); c["lists"][0]["sites"] += ["  youtube.com ", "", "YouTube.com"]
        self.assertIsNone(nucleo.sanitize_config(c))
        self.assertEqual(c["lists"][0]["sites"], ["youtube.com"])
        c = config(); c["lists"][0]["sites"].append("não é site")
        self.assertIsNotNone(nucleo.sanitize_config(c))


class Travas(unittest.TestCase):
    def test_senha(self):
        h = nucleo.hash_password("segredo")
        self.assertTrue(nucleo.verify_password("segredo", h))
        self.assertFalse(nucleo.verify_password("Segredo", h))
        self.assertFalse(nucleo.verify_password("x", None))

    def test_tempo(self):
        self.assertEqual(nucleo.try_stop({"lock": "timer", "end": NOW}, "x")["outcome"], "denied")

    def test_texto(self):
        b = {"lock": "text", "textLength": 40, "challenge": None}
        r = nucleo.try_stop(b, None)
        self.assertEqual(r["outcome"], "challenge")
        self.assertEqual(len(r["challenge"]), 40)
        self.assertEqual(nucleo.try_stop(b, r["challenge"] + "x")["outcome"], "wrong")
        self.assertEqual(nucleo.try_stop(b, r["challenge"])["outcome"], "stopped")

    def test_senha_trava(self):
        b = {"lock": "password", "passwordHash": nucleo.hash_password("abc")}
        self.assertEqual(nucleo.try_stop(b, None)["outcome"], "challenge")
        self.assertEqual(nucleo.try_stop(b, "abd")["outcome"], "wrong")
        self.assertEqual(nucleo.try_stop(b, "abc")["outcome"], "stopped")

    def test_relogio(self):
        g = nucleo.ClockGuard()
        self.assertEqual(g.observe(1000, 1), 0)
        self.assertEqual(g.observe(1001, 2), 0)
        salto = g.observe(1001 + 3 * 86400, 3)
        self.assertGreater(salto, 2.9 * 86400)
        estado = {"blocks": [{"lock": "timer", "end": 5000}, {"lock": "none", "end": 5000}], "pomodoro": None}
        self.assertTrue(nucleo.ClockGuard.compensate(estado, salto))
        self.assertEqual(estado["blocks"][0]["end"], 5000 + salto)
        self.assertEqual(estado["blocks"][1]["end"], 5000)


class ServicoLocal(unittest.TestCase):
    """Sobe o serviço de verdade (sem as partes do Windows) e conversa pelo cliente."""

    def test_fluxo(self):
        with tempfile.TemporaryDirectory() as d:
            os.environ["FOCOTOTAL_TESTE"] = d
            import importlib
            from focototal import caminhos, servico, cliente
            importlib.reload(caminhos); importlib.reload(servico); importlib.reload(cliente)
            Path(caminhos.HOSTS).write_text("127.0.0.1 localhost\n")
            coord = servico.Coordenador(servico.Armazenamento(caminhos.PASTA_DADOS))
            srv = servico._Servidor(("127.0.0.1", 0), servico._Atendente)
            caminhos.PORTA = srv.server_address[1]
            srv.coordenador = coord
            import threading
            threading.Thread(target=srv.serve_forever, daemon=True).start()
            try:
                st = cliente.enviar({"cmd": "status"})
                self.assertTrue(st["ok"])
                self.assertEqual(st["status"]["active"], [])

                r = cliente.enviar({"cmd": "start-block", "start": {"listIds": ["redes-sociais"], "minutes": 5,
                                                                    "lock": "text", "textLength": 20}})
                self.assertTrue(r["ok"], r)
                coord.passo()
                hosts = Path(caminhos.HOSTS).read_text()
                self.assertIn("0.0.0.0 instagram.com", hosts)
                self.assertIn("127.0.0.1 localhost", hosts)

                cfg = r["status"]["config"]
                cfg["lists"][0]["sites"].remove("instagram.com")
                self.assertFalse(cliente.enviar({"cmd": "save-config", "config": cfg})["ok"])

                bid = r["status"]["blocks"][0]["id"]
                self.assertNotIn("passwordHash", r["status"]["blocks"][0])
                c = cliente.enviar({"cmd": "stop-block", "id": bid})
                self.assertEqual(c["outcome"], "challenge")
                f = cliente.enviar({"cmd": "stop-block", "id": bid, "answer": c["challenge"]})
                self.assertEqual(f["outcome"], "stopped")
                coord.passo()
                self.assertNotIn("FocoTotal", Path(caminhos.HOSTS).read_text())
                self.assertTrue(os.path.exists(os.path.join(caminhos.PASTA_DADOS, "state.json")))
            finally:
                srv.shutdown()
                srv.server_close()


if __name__ == "__main__":
    unittest.main()
