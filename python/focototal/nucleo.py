"""
Núcleo do FocoTotal: regras puras, sem acesso a disco nem ao Windows.

Formato dos dados (JSON):

config = {
  "lists": [{"id", "name", "sites": [...], "apps": [...]}],
  "schedules": [{"id", "listId", "days": [0..6 (0 = segunda)], "start": "HH:MM", "end": "HH:MM",
                 "locked": bool, "enabled": bool}],
  "settings": {"protectSystemTools": bool, "disableBrowserDoH": bool, "protectUninstall": bool},
}

state = {
  "blocks": [{"id", "listIds", "start", "end" (epoch UTC), "lock": none|timer|text|password,
              "textLength", "passwordHash", "challenge", "source": manual|schedule}],
  "pomodoro": {"listId", "focus", "pause", "cycles", "cycle", "phase": focus|break, "phaseEnd", "lock"} | None,
  "lastSeen": epoch,
}
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import re
import secrets
import uuid
from datetime import datetime, timedelta

LOCKS = ("none", "timer", "text", "password")
LOCK_NAMES = {"none": "Sem trava", "timer": "Trava de tempo", "text": "Texto aleatório", "password": "Senha"}
DAY_NAMES = ["Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom"]


def new_id() -> str:
    return uuid.uuid4().hex


# ---------------------------------------------------------------- configuração


def default_settings() -> dict:
    return {"protectSystemTools": True, "disableBrowserDoH": True, "protectUninstall": True}


def default_config() -> dict:
    return {
        "schemaVersion": 1,
        "lists": [
            {"id": "redes-sociais", "name": "Redes sociais",
             "sites": ["facebook.com", "instagram.com", "x.com", "twitter.com", "tiktok.com", "reddit.com"], "apps": []},
            {"id": "videos-jogos", "name": "Vídeos e jogos",
             "sites": ["youtube.com", "twitch.tv", "netflix.com"],
             "apps": ["steam.exe", "EpicGamesLauncher.exe", "Discord.exe"]},
        ],
        "schedules": [],
        "settings": default_settings(),
    }


def default_state() -> dict:
    return {"blocks": [], "pomodoro": None, "lastSeen": 0.0}


# ---------------------------------------------------------------- sites

HOSTS_BEGIN = "# >>> FocoTotal (nao edite este bloco) >>>"
HOSTS_END = "# <<< FocoTotal <<<"
_HOSTNAME = re.compile(r"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")
_SUBDOMAINS = ("www", "m", "mobile", "web", "app", "api")


def normalize_site(entry: str | None) -> str | None:
    """'https://www.YouTube.com/watch?v=1' -> 'youtube.com'. None se não for domínio válido."""
    if not entry or not entry.strip():
        return None
    s = entry.strip().lower()
    if "://" in s:
        s = s.split("://", 1)[1]
    s = re.split(r"[/?#]", s, maxsplit=1)[0]
    s = s.rsplit("@", 1)[-1]
    s = s.split(":", 1)[0]
    if s.startswith("*."):
        s = s[2:]
    if s.startswith("www."):
        s = s[4:]
    s = s.strip(".")
    return s if _HOSTNAME.match(s) else None


def expand_for_hosts(entries) -> list[str]:
    names: set[str] = set()
    for e in entries:
        d = normalize_site(e)
        if not d:
            continue
        names.add(d)
        names.update(f"{sub}.{d}" for sub in _SUBDOMAINS)
    return sorted(names)


def hosts_remove_block(content: str) -> str:
    out, inside = [], False
    for line in content.replace("\r\n", "\n").split("\n"):
        t = line.strip()
        if t == HOSTS_BEGIN:
            inside = True
            continue
        if t == HOSTS_END:
            inside = False
            continue
        if not inside:
            out.append(line)
    while out and not out[-1].strip():
        out.pop()
    return "\r\n".join(out) + "\r\n" if out else ""


def hosts_apply_block(content: str, names: list[str]) -> str:
    base = hosts_remove_block(content)
    if not names:
        return base
    lines = [HOSTS_BEGIN]
    for n in names:
        lines.append(f"0.0.0.0 {n}")
        lines.append(f":: {n}")
    lines.append(HOSTS_END)
    return base + ("\r\n" if base else "") + "\r\n".join(lines) + "\r\n"


# ---------------------------------------------------------------- apps

SYSTEM_TOOLS = (
    "taskmgr.exe", "resmon.exe", "perfmon.exe", "procexp.exe", "procexp64.exe", "processhacker.exe",
    "systeminformer.exe", "mmc.exe", "regedit.exe", "schtasks.exe",
)

_PROTECTED = {
    "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe",
    "explorer.exe", "dwm.exe", "fontdrvhost.exe", "msmpeng.exe", "conhost.exe", "sihost.exe", "ctfmon.exe",
    "logonui.exe", "userinit.exe", "python.exe", "pythonw.exe", "py.exe", "pyw.exe",
}


def ensure_exe(name: str) -> str:
    name = name.strip()
    return name if name.lower().endswith(".exe") or name.lower() == "system" else name + ".exe"


def is_protected(name: str) -> bool:
    return ensure_exe(name).lower() in _PROTECTED


def app_matches(rule: str, process_name: str, full_path: str | None) -> bool:
    """Regra: 'steam.exe', 'steam', 'C:\\Jogos\\steam.exe' ou pasta 'C:\\Jogos\\*'."""
    if not rule or not rule.strip():
        return False
    rule = rule.strip().strip('"')
    name = ensure_exe(process_name)
    if is_protected(name):
        return False
    if "\\" not in rule and "/" not in rule:
        return ensure_exe(rule).lower() == name.lower()
    if not full_path:
        return False
    r = rule.replace("/", "\\").lower()
    p = full_path.replace("/", "\\").lower()
    if r.endswith("\\*") or r.endswith("\\"):
        folder = r.rstrip("*").rstrip("\\") + "\\"
        return p.startswith(folder)
    return r == p


# ---------------------------------------------------------------- agenda e política


def parse_time(s: str):
    m = re.fullmatch(r"\s*(\d{1,2}):(\d{2})\s*", s or "")
    if not m:
        return None
    h, mi = int(m.group(1)), int(m.group(2))
    return timedelta(hours=h, minutes=mi) if h < 24 and mi < 60 else None


def schedule_active(entry: dict, local_now: datetime) -> bool:
    if not entry.get("enabled", True) or not entry.get("days"):
        return False
    start, end = parse_time(entry.get("start", "")), parse_time(entry.get("end", ""))
    if start is None or end is None:
        return False
    t = timedelta(hours=local_now.hour, minutes=local_now.minute, seconds=local_now.second)
    today = local_now.weekday()
    yesterday = (today + 6) % 7
    days = set(entry["days"])
    if start < end:
        return today in days and start <= t < end
    return (today in days and t >= start) or (yesterday in days and t < end)


def tick(state: dict, now: float) -> bool:
    """Remove bloqueios vencidos e avança o Pomodoro. True se mudou algo."""
    before = len(state["blocks"])
    state["blocks"] = [b for b in state["blocks"] if b["end"] > now]
    changed = len(state["blocks"]) != before

    p = state.get("pomodoro")
    while p and p["phaseEnd"] <= now:
        changed = True
        if p["phase"] == "focus":
            if p["cycle"] >= p["cycles"]:
                state["pomodoro"] = p = None
                break
            p["phase"] = "break"
            p["phaseEnd"] += p["pause"] * 60
        else:
            p["cycle"] += 1
            p["phase"] = "focus"
            p["phaseEnd"] += p["focus"] * 60
    return changed


def compute_policy(config: dict, state: dict, now: float, local_now: datetime) -> dict:
    active, locked = set(), set()
    for b in state["blocks"]:
        if b["start"] <= now < b["end"]:
            active.update(b["listIds"])
            if b["lock"] != "none":
                locked.update(b["listIds"])
    for s in config["schedules"]:
        if schedule_active(s, local_now):
            active.add(s["listId"])
            if s.get("locked"):
                locked.add(s["listId"])
    p = state.get("pomodoro")
    if p:
        if p["phase"] == "focus" and p["phaseEnd"] > now:
            active.add(p["listId"])
        if p["lock"] != "none":
            locked.add(p["listId"])
    sites, apps = [], []
    for lst in config["lists"]:
        if lst["id"] in active:
            sites.extend(lst["sites"])
            apps.extend(lst["apps"])
    return {"active": sorted(active), "locked": sorted(locked), "sites": sites, "apps": apps}


def _site_key(s: str) -> str:
    return normalize_site(s) or s.strip().lower()


def validate_config_change(current: dict, new: dict, policy: dict, local_now: datetime) -> str | None:
    """Durante trava só é permitido endurecer as regras. Retorna o erro ou None."""
    for list_id in policy["locked"]:
        old = next((l for l in current["lists"] if l["id"] == list_id), None)
        if old is None:
            continue
        nl = next((l for l in new["lists"] if l["id"] == list_id), None)
        if nl is None:
            return f'A lista "{old["name"]}" está em um bloqueio travado e não pode ser excluída.'
        new_sites = {_site_key(s) for s in nl["sites"]}
        for s in old["sites"]:
            if _site_key(s) not in new_sites:
                return f'O site "{s}" está em um bloqueio travado e não pode ser removido agora.'
        new_apps = {a.strip().lower() for a in nl["apps"]}
        for a in old["apps"]:
            if a.strip().lower() not in new_apps:
                return f'O app "{a}" está em um bloqueio travado e não pode ser removido agora.'

    for s in current["schedules"]:
        if s.get("locked") and schedule_active(s, local_now):
            n = next((x for x in new["schedules"] if x["id"] == s["id"]), None)
            if (n is None or not n.get("enabled", True) or not n.get("locked") or n["listId"] != s["listId"]
                    or n["start"] != s["start"] or n["end"] != s["end"] or not set(s["days"]) <= set(n["days"])):
                return "Uma agenda travada está no horário ativo e não pode ser alterada até terminar."

    if policy["locked"]:
        o, n = current["settings"], new["settings"]
        for k in ("protectSystemTools", "disableBrowserDoH", "protectUninstall"):
            if o.get(k) and not n.get(k):
                return "As proteções não podem ser desligadas durante um bloqueio travado."
    return None


def sanitize_config(c: dict) -> str | None:
    """Limpa e valida a configuração recebida do app. Retorna erro ou None."""
    if not isinstance(c, dict):
        return "Configuração inválida."
    c.setdefault("lists", [])
    c.setdefault("schedules", [])
    c["settings"] = {**default_settings(), **(c.get("settings") or {})}
    if len(c["lists"]) > 200 or len(c["schedules"]) > 200:
        return "Limite de listas ou agendas excedido."
    for l in c["lists"]:
        l["id"] = l.get("id") or new_id()
        l["name"] = (l.get("name") or "").strip() or "Sem nome"
        for key in ("sites", "apps"):
            seen, out = set(), []
            for v in l.get(key) or []:
                v = str(v).strip()
                if v and v.lower() not in seen:
                    seen.add(v.lower())
                    out.append(v)
            l[key] = out
        for s in l["sites"]:
            if normalize_site(s) is None:
                return f'"{s}" não é um site válido (lista {l["name"]}).'
    if len({l["id"] for l in c["lists"]}) != len(c["lists"]):
        return "Há listas com o mesmo identificador."
    ids = {l["id"] for l in c["lists"]}
    for s in c["schedules"]:
        s["id"] = s.get("id") or new_id()
        s.setdefault("enabled", True)
        s.setdefault("locked", False)
        if s.get("listId") not in ids:
            return "Uma agenda aponta para uma lista que não existe."
        if parse_time(s.get("start", "")) is None or parse_time(s.get("end", "")) is None:
            return "Horário de agenda inválido. Use HH:MM."
        s["days"] = sorted({int(d) for d in s.get("days", []) if 0 <= int(d) <= 6})
    return None


def validate_start(config: dict, req: dict) -> str | None:
    ids = req.get("listIds") or []
    if not ids:
        return "Escolha pelo menos uma lista."
    known = {l["id"] for l in config["lists"]}
    if any(i not in known for i in ids):
        return "Lista não encontrada."
    minutes = req.get("minutes", 0)
    if not isinstance(minutes, (int, float)) or not 1 <= minutes <= 60 * 24 * 60:
        return "A duração deve ficar entre 1 minuto e 60 dias."
    lock = req.get("lock", "none")
    if lock not in LOCKS:
        return "Tipo de trava inválido."
    if lock == "text" and not 10 <= int(req.get("textLength", 0)) <= 5000:
        return "O texto aleatório deve ter entre 10 e 5000 caracteres."
    if lock == "password" and not req.get("password"):
        return "Informe a senha da trava."
    return None


def new_block(req: dict, now: float, source: str = "manual") -> dict:
    lock = req.get("lock", "none")
    return {
        "id": new_id(),
        "listIds": list(dict.fromkeys(req["listIds"])),
        "start": now,
        "end": now + float(req["minutes"]) * 60,
        "lock": lock,
        "textLength": int(req.get("textLength", 200)),
        "passwordHash": hash_password(req["password"]) if lock == "password" else None,
        "challenge": None,
        "source": source,
    }


# ---------------------------------------------------------------- travas

_ITER = 210_000
_ALPHABET = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"


def hash_password(password: str) -> str:
    salt = secrets.token_bytes(16)
    h = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, _ITER)
    return f"pbkdf2${_ITER}${base64.b64encode(salt).decode()}${base64.b64encode(h).decode()}"


def verify_password(password: str, stored: str | None) -> bool:
    try:
        kind, it, salt, h = (stored or "").split("$")
        if kind != "pbkdf2":
            return False
        actual = hashlib.pbkdf2_hmac("sha256", password.encode(), base64.b64decode(salt), int(it))
        return hmac.compare_digest(actual, base64.b64decode(h))
    except ValueError:
        return False


def random_text(length: int) -> str:
    return "".join(secrets.choice(_ALPHABET) for _ in range(length))


def try_stop(block: dict, answer: str | None) -> dict:
    """Decide se o bloqueio pode parar. Retorna {outcome: stopped|denied|challenge|wrong, message, challenge?}."""
    lock = block["lock"]
    if lock == "none":
        return {"outcome": "stopped", "message": "Bloqueio encerrado."}
    if lock == "timer":
        end = datetime.fromtimestamp(block["end"]).strftime("%d/%m %H:%M")
        return {"outcome": "denied", "message": f"Trava de tempo: este bloqueio só termina em {end}."}
    if lock == "text":
        if not block.get("challenge") or not answer:
            if not block.get("challenge"):
                block["challenge"] = random_text(max(10, int(block.get("textLength", 200))))
            return {"outcome": "challenge", "message": "Digite o texto abaixo exatamente como aparece para encerrar.",
                    "challenge": block["challenge"]}
        if answer == block["challenge"]:
            return {"outcome": "stopped", "message": "Texto correto. Bloqueio encerrado."}
        return {"outcome": "wrong", "message": "O texto não confere. Tente de novo.", "challenge": block["challenge"]}
    if lock == "password":
        if not answer:
            return {"outcome": "challenge", "message": "Digite a senha da trava."}
        if verify_password(answer, block.get("passwordHash")):
            return {"outcome": "stopped", "message": "Senha correta. Bloqueio encerrado."}
        return {"outcome": "wrong", "message": "Senha incorreta."}
    return {"outcome": "denied", "message": "Tipo de trava desconhecido."}


class ClockGuard:
    """Detecta o relógio adiantado comparando com um contador monotônico."""

    TOLERANCE = 120.0

    def __init__(self):
        self._last = None

    def observe(self, now: float, monotonic: float) -> float:
        if self._last is None:
            self._last = (now, monotonic)
            return 0.0
        last_now, last_mono = self._last
        self._last = (now, monotonic)
        jump = (now - last_now) - (monotonic - last_mono)
        return jump if jump > self.TOLERANCE else 0.0

    @staticmethod
    def compensate(state: dict, jump: float) -> bool:
        if jump <= 0:
            return False
        changed = False
        for b in state["blocks"]:
            if b["lock"] != "none":
                b["end"] += jump
                changed = True
        p = state.get("pomodoro")
        if p and p["lock"] != "none":
            p["phaseEnd"] += jump
            changed = True
        return changed


# ---------------------------------------------------------------- estatísticas


def stats_today(stats: dict, local_now: datetime) -> dict:
    key = local_now.strftime("%Y-%m-%d")
    days = stats.setdefault("days", [])
    for d in days:
        if d["date"] == key:
            return d
    d = {"date": key, "focusMinutes": 0, "blockedApps": {}}
    days.append(d)
    days.sort(key=lambda x: x["date"])
    del days[:-365]
    return d


def stats_record_app(stats: dict, local_now: datetime, name: str) -> None:
    d = stats_today(stats, local_now)
    key = ensure_exe(name).lower()
    d["blockedApps"][key] = d["blockedApps"].get(key, 0) + 1


def public_block(b: dict) -> dict:
    """Bloqueio como o app vê: sem hash de senha nem desafio."""
    return {k: b[k] for k in ("id", "listIds", "start", "end", "lock", "source")}
