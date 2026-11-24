"""App de configuração do FocoTotal (Tkinter, roda com pythonw.exe sem privilégios)."""

from __future__ import annotations

import copy
import json
import os
import sys
import tkinter as tk
from datetime import datetime
from tkinter import filedialog, messagebox, ttk

from . import caminhos, cliente, nucleo, winapi

TITULO = "FocoTotal"


def restante(fim: float) -> str:
    r = max(0, int(fim - datetime.now().timestamp()))
    d, r = divmod(r, 86400)
    h, r = divmod(r, 3600)
    m, s = divmod(r, 60)
    return f"{d}d {h}h {m:02d}min" if d else f"{h}h {m:02d}min {s:02d}s"


class DialogoTrava(tk.Toplevel):
    """Pede o texto aleatório (sem colar) ou a senha."""

    def __init__(self, pai, mensagem: str, desafio: str | None, senha: bool):
        super().__init__(pai)
        self.title("Encerrar bloqueio")
        self.transient(pai)
        self.resposta: str | None = None
        frm = ttk.Frame(self, padding=12)
        frm.pack(fill="both", expand=True)
        ttk.Label(frm, text=mensagem, wraplength=560).pack(anchor="w")
        if desafio:
            # Rótulo não permite selecionar nem copiar.
            ttk.Label(frm, text=desafio, wraplength=560, font=("Consolas", 11), relief="solid",
                      padding=6).pack(fill="x", pady=8)
            self.campo = tk.Text(frm, height=6, width=70, wrap="char", font=("Consolas", 11))
            for ev in ("<<Paste>>", "<Control-v>", "<Control-V>", "<Shift-Insert>", "<Button-3>", "<Button-2>"):
                self.campo.bind(ev, lambda e: "break")
            self.campo.pack(fill="both", expand=True)
        else:
            self.campo = ttk.Entry(frm, show="•" if senha else "", width=40)
            self.campo.pack(fill="x", pady=8)
            self.campo.bind("<Return>", lambda e: self._ok())
        botoes = ttk.Frame(frm)
        botoes.pack(anchor="e", pady=(8, 0))
        ttk.Button(botoes, text="Encerrar", command=self._ok).pack(side="left", padx=4)
        ttk.Button(botoes, text="Cancelar", command=self.destroy).pack(side="left")
        self.campo.focus_set()
        self.grab_set()
        self.wait_window()

    def _ok(self):
        self.resposta = (self.campo.get("1.0", "end-1c") if isinstance(self.campo, tk.Text) else self.campo.get())
        self.destroy()


class DialogoAgenda(tk.Toplevel):
    def __init__(self, pai, listas: list[dict], entrada: dict | None):
        super().__init__(pai)
        self.title("Agenda")
        self.transient(pai)
        self.resultado: dict | None = None
        self.listas = listas
        e = copy.deepcopy(entrada) if entrada else {"id": nucleo.new_id(), "listId": listas[0]["id"],
                                                     "days": [0, 1, 2, 3, 4], "start": "09:00", "end": "18:00",
                                                     "locked": False, "enabled": True}
        self.entrada = e
        frm = ttk.Frame(self, padding=12)
        frm.pack(fill="both", expand=True)

        ttk.Label(frm, text="Lista:").grid(row=0, column=0, sticky="w")
        self.lista = ttk.Combobox(frm, state="readonly", values=[l["name"] for l in listas], width=30)
        idx = next((i for i, l in enumerate(listas) if l["id"] == e["listId"]), 0)
        self.lista.current(idx)
        self.lista.grid(row=0, column=1, columnspan=7, sticky="w", pady=4)

        self.dias = []
        for i, nome in enumerate(nucleo.DAY_NAMES):
            v = tk.BooleanVar(value=i in e["days"])
            ttk.Checkbutton(frm, text=nome, variable=v).grid(row=1, column=i, padx=2, pady=4)
            self.dias.append(v)

        ttk.Label(frm, text="Das").grid(row=2, column=0, sticky="w")
        self.inicio = ttk.Entry(frm, width=6)
        self.inicio.insert(0, e["start"])
        self.inicio.grid(row=2, column=1, sticky="w")
        ttk.Label(frm, text="às").grid(row=2, column=2)
        self.fim = ttk.Entry(frm, width=6)
        self.fim.insert(0, e["end"])
        self.fim.grid(row=2, column=3, sticky="w")
        ttk.Label(frm, text="(HH:MM; se o fim for antes do início, vai até o dia seguinte)").grid(
            row=3, column=0, columnspan=8, sticky="w")

        self.ativa = tk.BooleanVar(value=e.get("enabled", True))
        self.travada = tk.BooleanVar(value=e.get("locked", False))
        ttk.Checkbutton(frm, text="Ativa", variable=self.ativa).grid(row=4, column=0, columnspan=2, sticky="w")
        ttk.Checkbutton(frm, text="Travada (não pode ser alterada durante o horário)",
                        variable=self.travada).grid(row=4, column=2, columnspan=6, sticky="w")
        b = ttk.Frame(frm)
        b.grid(row=5, column=0, columnspan=8, sticky="e", pady=(10, 0))
        ttk.Button(b, text="Salvar", command=self._salvar).pack(side="left", padx=4)
        ttk.Button(b, text="Cancelar", command=self.destroy).pack(side="left")
        self.grab_set()
        self.wait_window()

    def _salvar(self):
        dias = [i for i, v in enumerate(self.dias) if v.get()]
        ini, fim = self.inicio.get().strip(), self.fim.get().strip()
        if not dias:
            messagebox.showwarning(TITULO, "Escolha pelo menos um dia.", parent=self)
            return
        if nucleo.parse_time(ini) is None or nucleo.parse_time(fim) is None:
            messagebox.showwarning(TITULO, "Use horários no formato HH:MM.", parent=self)
            return
        self.entrada.update(listId=self.listas[self.lista.current()]["id"], days=dias, start=ini, end=fim,
                            enabled=self.ativa.get(), locked=self.travada.get())
        self.resultado = self.entrada
        self.destroy()


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(TITULO)
        self.geometry("900x600")
        self.minsize(780, 520)
        self.status: dict | None = None
        self.editando: dict | None = None  # cópia das listas em edição
        self.listas_sujas = False
        self._carregando = False
        self._ultimo_bloqueado = -1

        estilo = ttk.Style(self)
        if "vista" in estilo.theme_names():
            estilo.theme_use("vista")

        self.lbl_status = ttk.Label(self, text="Conectando ao serviço…", font=("Segoe UI", 12, "bold"), padding=8)
        self.lbl_status.pack(fill="x")
        abas = ttk.Notebook(self)
        abas.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        self._aba_bloqueio(abas)
        self._aba_listas(abas)
        self._aba_agenda(abas)
        self._aba_pomodoro(abas)
        self._aba_estatisticas(abas)
        self._aba_config(abas)
        abas.bind("<<NotebookTabChanged>>", lambda e: abas.index("current") == 4 and self.carregar_stats())

        self.after(100, self._aviso_primeiro_uso)
        self.atualizar()

    # ------------------------------------------------------------ comunicação

    def enviar(self, pedido: dict, mostrar_erro=True) -> dict | None:
        try:
            r = cliente.enviar(pedido)
        except cliente.ServicoIndisponivel:
            self.lbl_status.config(text="O serviço do FocoTotal não está rodando. Reinstale ou reinicie o PC.",
                                   foreground="firebrick")
            if mostrar_erro and pedido.get("cmd") != "status":
                messagebox.showerror(TITULO, "Não foi possível falar com o serviço do FocoTotal.", parent=self)
            return None
        if r.get("status"):
            self.aplicar_status(r["status"])
        if not r.get("ok") and mostrar_erro and "outcome" not in r and r.get("message"):
            messagebox.showwarning(TITULO, r["message"], parent=self)
        return r

    def atualizar(self):
        self.enviar({"cmd": "status"}, mostrar_erro=False)
        self.after(2000, self.atualizar)

    def nomes(self) -> dict:
        return {l["id"]: l["name"] for l in (self.status or {}).get("config", {}).get("lists", [])}

    def aplicar_status(self, s: dict):
        self.status = s
        nomes = self.nomes()
        lista_nomes = lambda ids: ", ".join(nomes.get(i, "(lista excluída)") for i in ids)
        if s["active"]:
            self.lbl_status.config(text=f"Bloqueando: {lista_nomes(s['active'])}" + (
                "  (travado)" if s["locked"] else ""), foreground="seagreen")
        else:
            self.lbl_status.config(text="Nenhum bloqueio ativo.", foreground="")
        self._ultimo_bloqueado = s.get("blockedToday", 0)

        # Bloqueios em andamento
        sel = self.tv_blocos.selection()
        self.tv_blocos.delete(*self.tv_blocos.get_children())
        for b in sorted(s["blocks"], key=lambda b: b["end"]):
            self.tv_blocos.insert("", "end", iid=b["id"], values=(
                lista_nomes(b["listIds"]), restante(b["end"]), datetime.fromtimestamp(b["end"]).strftime("%d/%m %H:%M"),
                nucleo.LOCK_NAMES[b["lock"]]))
        for i in sel:
            if self.tv_blocos.exists(i):
                self.tv_blocos.selection_add(i)

        # Escolha de listas para iniciar bloqueio e Pomodoro
        listas = s["config"]["lists"]
        atuais = [l["id"] for l in listas]
        if getattr(self, "_ids_listas", None) != atuais or self.lb_iniciar.size() != len(atuais):
            marcadas = {self._ids_listas[i] for i in self.lb_iniciar.curselection()} if getattr(
                self, "_ids_listas", None) else set()
            self._ids_listas = atuais
            self.lb_iniciar.delete(0, "end")
            for i, l in enumerate(listas):
                self.lb_iniciar.insert("end", l["name"])
                if l["id"] in marcadas:
                    self.lb_iniciar.selection_set(i)
            self.cb_pomo["values"] = [l["name"] for l in listas]
            if listas and self.cb_pomo.current() < 0:
                self.cb_pomo.current(0)

        if not self.listas_sujas:
            self.carregar_editor(copy.deepcopy(s["config"]["lists"]))

        # Agenda
        agora = datetime.now()
        self.tv_agenda.delete(*self.tv_agenda.get_children())
        for e in s["config"]["schedules"]:
            situacao = "Desativada" if not e.get("enabled", True) else (
                "Bloqueando agora" if nucleo.schedule_active(e, agora) else "Aguardando")
            dias = "Todos os dias" if len(e["days"]) == 7 else ", ".join(nucleo.DAY_NAMES[d] for d in e["days"])
            self.tv_agenda.insert("", "end", iid=e["id"], values=(
                nomes.get(e["listId"], "?"), dias, e["start"], e["end"], "Sim" if e.get("locked") else "Não", situacao))

        # Pomodoro
        p = s.get("pomodoro")
        if p:
            fase = "Foco" if p["phase"] == "focus" else "Pausa"
            self.lbl_pomo.config(text=f"{fase}: ciclo {p['cycle']} de {p['cycles']}, faltam {restante(p['phaseEnd'])} "
                                      f"({nomes.get(p['listId'], '?')})",
                                 foreground="seagreen" if p["phase"] == "focus" else "steelblue")
        else:
            self.lbl_pomo.config(text="Nenhum Pomodoro em andamento.", foreground="")

        if not self.config_suja:
            st = s["config"]["settings"]
            self.v_ferramentas.set(st["protectSystemTools"])
            self.v_doh.set(st["disableBrowserDoH"])
            self.v_desinstalar.set(st["protectUninstall"])
        self.lbl_versao.config(text=f"Serviço versão {s['version']}.")

    # ------------------------------------------------------------ aba Bloqueio

    def _aba_bloqueio(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Bloqueio")
        ativos = ttk.LabelFrame(f, text="Bloqueios em andamento", padding=8)
        ativos.pack(fill="both", expand=True)
        self.tv_blocos = ttk.Treeview(ativos, columns=("listas", "restante", "termina", "trava"), show="headings",
                                      height=5, selectmode="browse")
        for col, txt, w in (("listas", "Listas", 300), ("restante", "Restante", 140), ("termina", "Termina", 110),
                            ("trava", "Trava", 130)):
            self.tv_blocos.heading(col, text=txt)
            self.tv_blocos.column(col, width=w)
        self.tv_blocos.pack(fill="both", expand=True)
        ttk.Button(ativos, text="Parar bloqueio selecionado", command=self.parar_bloqueio).pack(anchor="w", pady=(6, 0))

        novo = ttk.LabelFrame(f, text="Novo bloqueio", padding=8)
        novo.pack(fill="x", pady=(8, 0))
        ttk.Label(novo, text="Listas (clique para marcar):").grid(row=0, column=0, sticky="w")
        self.lb_iniciar = tk.Listbox(novo, selectmode="multiple", height=5, exportselection=False, width=34)
        self.lb_iniciar.grid(row=1, column=0, rowspan=5, sticky="nsw", padx=(0, 16))

        ttk.Label(novo, text="Duração:").grid(row=1, column=1, sticky="w")
        self.sp_horas = ttk.Spinbox(novo, from_=0, to=1440, width=5)
        self.sp_horas.set(1)
        self.sp_horas.grid(row=1, column=2, sticky="w")
        ttk.Label(novo, text="h").grid(row=1, column=3, sticky="w")
        self.sp_min = ttk.Spinbox(novo, from_=0, to=59, width=5)
        self.sp_min.set(0)
        self.sp_min.grid(row=1, column=4, sticky="w")
        ttk.Label(novo, text="min").grid(row=1, column=5, sticky="w")

        ttk.Label(novo, text="Trava:").grid(row=2, column=1, sticky="w")
        self.cb_trava = ttk.Combobox(novo, state="readonly", values=[nucleo.LOCK_NAMES[k] for k in nucleo.LOCKS],
                                     width=18)
        self.cb_trava.current(0)
        self.cb_trava.grid(row=2, column=2, columnspan=4, sticky="w")
        ttk.Label(novo, text="Tamanho do texto:").grid(row=3, column=1, sticky="w")
        self.sp_texto = ttk.Spinbox(novo, from_=10, to=5000, width=6)
        self.sp_texto.set(300)
        self.sp_texto.grid(row=3, column=2, columnspan=2, sticky="w")
        ttk.Label(novo, text="Senha:").grid(row=4, column=1, sticky="w")
        self.ent_senha = ttk.Entry(novo, show="•", width=20)
        self.ent_senha.grid(row=4, column=2, columnspan=4, sticky="w")
        ttk.Button(novo, text="Iniciar bloqueio", command=self.iniciar_bloqueio).grid(
            row=5, column=1, columnspan=5, sticky="w", pady=(6, 0))

    def iniciar_bloqueio(self):
        ids = [self._ids_listas[i] for i in self.lb_iniciar.curselection()]
        try:
            minutos = int(self.sp_horas.get() or 0) * 60 + int(self.sp_min.get() or 0)
        except ValueError:
            minutos = 0
        trava = nucleo.LOCKS[self.cb_trava.current()]
        if not ids:
            messagebox.showwarning(TITULO, "Selecione pelo menos uma lista.", parent=self)
            return
        if minutos < 1:
            messagebox.showwarning(TITULO, "Defina a duração.", parent=self)
            return
        if trava != "none":
            fim = datetime.fromtimestamp(datetime.now().timestamp() + minutos * 60).strftime("%d/%m %H:%M")
            msg = (f"Este bloqueio NÃO poderá ser parado até {fim}. Continuar?" if trava == "timer"
                   else f"Este bloqueio vai até {fim} e só para com a trava escolhida. Continuar?")
            if not messagebox.askyesno("Confirmar bloqueio travado", msg, icon="warning", parent=self):
                return
        r = self.enviar({"cmd": "start-block", "start": {"listIds": ids, "minutes": minutos, "lock": trava,
                                                           "textLength": int(self.sp_texto.get() or 300),
                                                           "password": self.ent_senha.get()}})
        if r and r.get("ok"):
            self.ent_senha.delete(0, "end")

    def parar_bloqueio(self):
        sel = self.tv_blocos.selection()
        if not sel:
            messagebox.showwarning(TITULO, "Selecione um bloqueio.", parent=self)
            return
        bid = sel[0]
        bloco = next((b for b in self.status["blocks"] if b["id"] == bid), None)
        resposta = None
        for _ in range(10):
            r = self.enviar({"cmd": "stop-block", "id": bid, "answer": resposta})
            if not r:
                return
            res = r.get("outcome")
            if res == "stopped":
                messagebox.showinfo(TITULO, r["message"], parent=self)
                return
            if res in ("challenge", "wrong"):
                d = DialogoTrava(self, r["message"], r.get("challenge"), bloco and bloco["lock"] == "password")
                if d.resposta is None:
                    return
                resposta = d.resposta
                continue
            messagebox.showwarning(TITULO, r.get("message", "Não é possível parar."), parent=self)
            return

    # ------------------------------------------------------------ aba Listas

    def _aba_listas(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Listas")
        esq = ttk.Frame(f)
        esq.pack(side="left", fill="y")
        self.lb_listas = tk.Listbox(esq, width=26, exportselection=False)
        self.lb_listas.pack(fill="y", expand=True)
        self.lb_listas.bind("<<ListboxSelect>>", lambda e: self.mostrar_lista())
        b = ttk.Frame(esq)
        b.pack(fill="x", pady=4)
        ttk.Button(b, text="Nova", command=self.nova_lista).pack(side="left")
        ttk.Button(b, text="Excluir", command=self.excluir_lista).pack(side="left", padx=4)

        dir_ = ttk.Frame(f, padding=(10, 0, 0, 0))
        dir_.pack(side="left", fill="both", expand=True)
        linha = ttk.Frame(dir_)
        linha.pack(fill="x")
        ttk.Label(linha, text="Nome:").pack(side="left")
        self.ent_nome = ttk.Entry(linha, width=40)
        self.ent_nome.pack(side="left", padx=6)
        self.ent_nome.bind("<KeyRelease>", lambda e: self.editor_mudou())

        caixas = ttk.Frame(dir_)
        caixas.pack(fill="both", expand=True, pady=6)
        caixas.columnconfigure(0, weight=1)
        caixas.columnconfigure(1, weight=1)
        caixas.rowconfigure(1, weight=1)
        ttk.Label(caixas, text="Sites (um por linha, ex.: youtube.com)").grid(row=0, column=0, sticky="w")
        ttk.Label(caixas, text="Apps (ex.: steam.exe ou C:\\Jogos\\*)").grid(row=0, column=1, sticky="w")
        self.txt_sites = tk.Text(caixas, width=30, height=12, undo=True)
        self.txt_apps = tk.Text(caixas, width=30, height=12, undo=True)
        self.txt_sites.grid(row=1, column=0, sticky="nsew", padx=(0, 6))
        self.txt_apps.grid(row=1, column=1, sticky="nsew")
        for t in (self.txt_sites, self.txt_apps):
            t.bind("<<Modified>>", self._texto_modificado)
        ttk.Button(caixas, text="Adicionar app aberto…", command=self.escolher_app_aberto).grid(
            row=2, column=1, sticky="w", pady=4)
        ttk.Button(caixas, text="Procurar .exe…", command=self.procurar_exe).grid(row=2, column=1, sticky="e", pady=4)

        baixo = ttk.Frame(dir_)
        baixo.pack(fill="x")
        ttk.Button(baixo, text="Salvar listas", command=self.salvar_listas).pack(side="left")
        ttk.Button(baixo, text="Descartar alterações", command=self.descartar_listas).pack(side="left", padx=6)
        self.lbl_travada = ttk.Label(baixo, text="", foreground="darkorange")
        self.lbl_travada.pack(side="left", padx=6)

    def _texto_modificado(self, e):
        if e.widget.edit_modified():
            e.widget.edit_modified(False)
            self.editor_mudou()

    def lista_atual(self) -> dict | None:
        sel = self.lb_listas.curselection()
        return self.editando[sel[0]] if sel and self.editando and sel[0] < len(self.editando) else None

    def carregar_editor(self, listas: list[dict]):
        atual = self.lista_atual()
        id_sel = atual["id"] if atual else None
        self.editando = listas
        self._carregando = True
        self.lb_listas.delete(0, "end")
        for l in listas:
            self.lb_listas.insert("end", l["name"])
        idx = next((i for i, l in enumerate(listas) if l["id"] == id_sel), 0 if listas else None)
        if idx is not None:
            self.lb_listas.selection_set(idx)
        self._carregando = False
        self.mostrar_lista()

    def mostrar_lista(self):
        l = self.lista_atual()
        self._carregando = True
        self.ent_nome.delete(0, "end")
        self.txt_sites.delete("1.0", "end")
        self.txt_apps.delete("1.0", "end")
        if l:
            self.ent_nome.insert(0, l["name"])
            self.txt_sites.insert("1.0", "\n".join(l["sites"]))
            self.txt_apps.insert("1.0", "\n".join(l["apps"]))
        for t in (self.txt_sites, self.txt_apps):
            t.edit_modified(False)
        self._carregando = False
        travada = l and self.status and l["id"] in self.status["locked"]
        self.lbl_travada.config(text="Lista em bloqueio travado: dá para adicionar itens, não remover." if travada else "")

    def editor_mudou(self):
        l = self.lista_atual()
        if self._carregando or not l:
            return
        self.listas_sujas = True
        l["name"] = self.ent_nome.get()
        l["sites"] = [x.strip() for x in self.txt_sites.get("1.0", "end").splitlines() if x.strip()]
        l["apps"] = [x.strip() for x in self.txt_apps.get("1.0", "end").splitlines() if x.strip()]
        i = self.lb_listas.curselection()[0]
        if self.lb_listas.get(i) != l["name"]:
            self.lb_listas.delete(i)
            self.lb_listas.insert(i, l["name"])
            self.lb_listas.selection_set(i)

    def nova_lista(self):
        if self.editando is None:
            return
        self.editando.append({"id": nucleo.new_id(), "name": "Nova lista", "sites": [], "apps": []})
        self.listas_sujas = True
        self.lb_listas.insert("end", "Nova lista")
        self.lb_listas.selection_clear(0, "end")
        self.lb_listas.selection_set("end")
        self.mostrar_lista()
        self.ent_nome.focus_set()
        self.ent_nome.select_range(0, "end")

    def excluir_lista(self):
        l = self.lista_atual()
        if not l or not messagebox.askyesno(TITULO, f'Excluir a lista "{l["name"]}"? As agendas dela também saem.',
                                            parent=self):
            return
        self.editando.remove(l)
        self.listas_sujas = True
        self.carregar_editor(self.editando)

    def _acrescentar_apps(self, apps):
        if not self.lista_atual():
            messagebox.showwarning(TITULO, "Selecione ou crie uma lista.", parent=self)
            return
        linhas = [x.strip() for x in self.txt_apps.get("1.0", "end").splitlines() if x.strip()]
        for a in apps:
            if a.lower() not in {x.lower() for x in linhas}:
                linhas.append(a)
        self.txt_apps.delete("1.0", "end")
        self.txt_apps.insert("1.0", "\n".join(linhas))
        self.editor_mudou()

    def escolher_app_aberto(self):
        nomes = sorted({n for _, _, n in winapi.list_processes() if not nucleo.is_protected(n)}, key=str.lower)
        if not nomes:
            messagebox.showinfo(TITULO, "Não foi possível listar os apps abertos.", parent=self)
            return
        jan = tk.Toplevel(self)
        jan.title("Apps em execução")
        jan.transient(self)
        lb = tk.Listbox(jan, selectmode="multiple", width=40, height=20)
        for n in nomes:
            lb.insert("end", n)
        lb.pack(fill="both", expand=True, padx=8, pady=8)

        def ok():
            self._acrescentar_apps([nomes[i] for i in lb.curselection()])
            jan.destroy()

        ttk.Button(jan, text="Adicionar", command=ok).pack(pady=(0, 8))
        jan.grab_set()

    def procurar_exe(self):
        arqs = filedialog.askopenfilenames(parent=self, title="Programas a bloquear",
                                           filetypes=[("Programas", "*.exe")])
        if arqs:
            self._acrescentar_apps([os.path.basename(a) for a in arqs])

    def salvar_listas(self):
        if not self.status:
            return
        cfg = copy.deepcopy(self.status["config"])
        cfg["lists"] = copy.deepcopy(self.editando)
        ids = {l["id"] for l in cfg["lists"]}
        cfg["schedules"] = [s for s in cfg["schedules"] if s["listId"] in ids]
        r = self.enviar({"cmd": "save-config", "config": cfg})
        if r and r.get("ok"):
            self.listas_sujas = False
            self.carregar_editor(copy.deepcopy(r["status"]["config"]["lists"]))

    def descartar_listas(self):
        self.listas_sujas = False
        if self.status:
            self.carregar_editor(copy.deepcopy(self.status["config"]["lists"]))

    # ------------------------------------------------------------ aba Agenda

    def _aba_agenda(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Agenda")
        ttk.Label(f, text="A agenda bloqueia a lista toda semana nos dias e horários escolhidos.").pack(anchor="w")
        self.tv_agenda = ttk.Treeview(f, columns=("lista", "dias", "ini", "fim", "trav", "sit"), show="headings",
                                      selectmode="browse")
        for col, txt, w in (("lista", "Lista", 180), ("dias", "Dias", 200), ("ini", "Início", 70), ("fim", "Fim", 70),
                            ("trav", "Travada", 70), ("sit", "Situação", 130)):
            self.tv_agenda.heading(col, text=txt)
            self.tv_agenda.column(col, width=w)
        self.tv_agenda.pack(fill="both", expand=True, pady=6)
        self.tv_agenda.bind("<Double-1>", lambda e: self.editar_agenda(True))
        b = ttk.Frame(f)
        b.pack(anchor="w")
        ttk.Button(b, text="Adicionar", command=lambda: self.editar_agenda(False)).pack(side="left")
        ttk.Button(b, text="Editar", command=lambda: self.editar_agenda(True)).pack(side="left", padx=4)
        ttk.Button(b, text="Excluir", command=self.excluir_agenda).pack(side="left")

    def editar_agenda(self, existente: bool):
        if not self.status:
            return
        cfg = copy.deepcopy(self.status["config"])
        if not cfg["lists"]:
            messagebox.showwarning(TITULO, "Crie uma lista primeiro.", parent=self)
            return
        entrada = None
        if existente:
            sel = self.tv_agenda.selection()
            if not sel:
                return
            entrada = next((s for s in cfg["schedules"] if s["id"] == sel[0]), None)
        d = DialogoAgenda(self, cfg["lists"], entrada)
        if not d.resultado:
            return
        cfg["schedules"] = [s for s in cfg["schedules"] if s["id"] != d.resultado["id"]] + [d.resultado]
        self.enviar({"cmd": "save-config", "config": cfg})

    def excluir_agenda(self):
        sel = self.tv_agenda.selection()
        if not sel or not self.status or not messagebox.askyesno(TITULO, "Excluir esta agenda?", parent=self):
            return
        cfg = copy.deepcopy(self.status["config"])
        cfg["schedules"] = [s for s in cfg["schedules"] if s["id"] != sel[0]]
        self.enviar({"cmd": "save-config", "config": cfg})

    # ------------------------------------------------------------ aba Pomodoro

    def _aba_pomodoro(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Pomodoro")
        self.lbl_pomo = ttk.Label(f, text="Nenhum Pomodoro em andamento.", font=("Segoe UI", 12, "bold"))
        self.lbl_pomo.pack(anchor="w", pady=(0, 10))
        l1 = ttk.Frame(f)
        l1.pack(anchor="w")
        ttk.Label(l1, text="Lista bloqueada durante o foco:").pack(side="left")
        self.cb_pomo = ttk.Combobox(l1, state="readonly", width=30)
        self.cb_pomo.pack(side="left", padx=6)
        l2 = ttk.Frame(f)
        l2.pack(anchor="w", pady=6)
        self.sp_foco = self._spin(l2, "Foco (min):", 1, 240, 25)
        self.sp_pausa = self._spin(l2, "Pausa (min):", 1, 120, 5)
        self.sp_ciclos = self._spin(l2, "Ciclos:", 1, 20, 4)
        self.v_pomo_trava = tk.BooleanVar()
        ttk.Checkbutton(f, text="Trava de tempo (não dá para parar no meio)", variable=self.v_pomo_trava).pack(anchor="w")
        l3 = ttk.Frame(f)
        l3.pack(anchor="w", pady=8)
        ttk.Button(l3, text="Iniciar Pomodoro", command=self.iniciar_pomodoro).pack(side="left")
        ttk.Button(l3, text="Parar", command=lambda: self.enviar({"cmd": "stop-pomodoro"})).pack(side="left", padx=4)
        ttk.Label(f, text="Durante o foco a lista fica bloqueada. Na pausa, tudo é liberado até o próximo ciclo.").pack(
            anchor="w")

    def _spin(self, pai, rotulo, de, ate, valor):
        ttk.Label(pai, text=rotulo).pack(side="left", padx=(0, 4))
        s = ttk.Spinbox(pai, from_=de, to=ate, width=5)
        s.set(valor)
        s.pack(side="left", padx=(0, 12))
        return s

    def iniciar_pomodoro(self):
        i = self.cb_pomo.current()
        if i < 0 or not getattr(self, "_ids_listas", None):
            messagebox.showwarning(TITULO, "Escolha uma lista.", parent=self)
            return
        try:
            foco, pausa, ciclos = int(self.sp_foco.get()), int(self.sp_pausa.get()), int(self.sp_ciclos.get())
        except ValueError:
            messagebox.showwarning(TITULO, "Use números inteiros.", parent=self)
            return
        self.enviar({"cmd": "start-pomodoro", "pomodoro": {
            "listId": self._ids_listas[i], "focus": foco, "pause": pausa, "cycles": ciclos,
            "lock": "timer" if self.v_pomo_trava.get() else "none"}})

    # ------------------------------------------------------------ aba Estatísticas

    def _aba_estatisticas(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Estatísticas")
        ttk.Label(f, text="Os dados ficam só neste computador. Últimos 30 dias.").pack(anchor="w")
        self.tv_stats = ttk.Treeview(f, columns=("data", "foco", "bloq", "mais"), show="headings")
        for col, txt, w in (("data", "Data", 110), ("foco", "Minutos de foco", 130), ("bloq", "Apps fechados", 120),
                            ("mais", "Mais bloqueado", 260)):
            self.tv_stats.heading(col, text=txt)
            self.tv_stats.column(col, width=w)
        self.tv_stats.pack(fill="both", expand=True, pady=6)
        b = ttk.Frame(f)
        b.pack(anchor="w")
        ttk.Button(b, text="Atualizar", command=self.carregar_stats).pack(side="left")
        ttk.Button(b, text="Exportar JSON…", command=self.exportar_stats).pack(side="left", padx=4)
        self._stats = None

    def carregar_stats(self):
        r = self.enviar({"cmd": "stats"}, mostrar_erro=False)
        if not r or not r.get("ok"):
            return
        self._stats = r["stats"]
        self.tv_stats.delete(*self.tv_stats.get_children())
        for d in sorted(self._stats.get("days", []), key=lambda d: d["date"], reverse=True)[:30]:
            apps = d.get("blockedApps", {})
            top = max(apps.items(), key=lambda kv: kv[1]) if apps else None
            data = datetime.strptime(d["date"], "%Y-%m-%d").strftime("%d/%m/%Y")
            self.tv_stats.insert("", "end", values=(data, d.get("focusMinutes", 0), sum(apps.values()),
                                                     f"{top[0]} ({top[1]})" if top else ""))

    def exportar_stats(self):
        if not self._stats:
            self.carregar_stats()
        if not self._stats:
            return
        arq = filedialog.asksaveasfilename(parent=self, defaultextension=".json", filetypes=[("JSON", "*.json")],
                                           initialfile=f"focototal-estatisticas-{datetime.now():%Y%m%d}.json")
        if arq:
            with open(arq, "w", encoding="utf-8") as f:
                json.dump(self._stats, f, ensure_ascii=False, indent=2)

    # ------------------------------------------------------------ aba Configurações

    def _aba_config(self, abas):
        f = ttk.Frame(abas, padding=10)
        abas.add(f, text="Configurações")
        self.config_suja = False
        self.v_doh, self.v_ferramentas, self.v_desinstalar = tk.BooleanVar(), tk.BooleanVar(), tk.BooleanVar()
        for var, txt in (
            (self.v_doh, "Desligar DNS sobre HTTPS no Chrome, Edge, Brave e Firefox (necessário para bloquear sites)"),
            (self.v_ferramentas, "Durante bloqueio travado, fechar Gerenciador de Tarefas, Editor do Registro e parecidos"),
            (self.v_desinstalar, "Durante bloqueio travado, impedir desinstalar e recriar a tarefa se for removida"),
        ):
            ttk.Checkbutton(f, text=txt, variable=var, command=lambda: setattr(self, "config_suja", True)).pack(
                anchor="w", pady=2)
        ttk.Button(f, text="Salvar configurações", command=self.salvar_config).pack(anchor="w", pady=8)
        self.lbl_versao = ttk.Label(f, text="")
        self.lbl_versao.pack(anchor="w")
        ttk.Label(f, text="O bloqueio de sites usa o arquivo hosts do Windows e vale para todos os navegadores.").pack(
            anchor="w", pady=(8, 0))

    def salvar_config(self):
        if not self.status:
            return
        cfg = copy.deepcopy(self.status["config"])
        cfg["settings"].update(protectSystemTools=self.v_ferramentas.get(), disableBrowserDoH=self.v_doh.get(),
                               protectUninstall=self.v_desinstalar.get())
        r = self.enviar({"cmd": "save-config", "config": cfg})
        self.config_suja = False
        if r and r.get("ok"):
            messagebox.showinfo(TITULO, "Configurações salvas.", parent=self)

    # ------------------------------------------------------------ primeiro uso

    def _aviso_primeiro_uso(self):
        marca = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "FocoTotal", "aviso-visto")
        if os.path.exists(marca):
            return
        messagebox.showinfo(
            "Bem-vindo ao FocoTotal",
            "Para bloquear o que você escolher, o FocoTotal faz estas alterações no Windows:\n\n"
            "• Escreve um bloco no arquivo hosts enquanto há sites bloqueados (removido ao terminar).\n"
            "• Desliga o DNS sobre HTTPS dos navegadores por política, se ainda não houver política.\n"
            "• Durante bloqueio travado: fecha os apps da lista e ferramentas como o Gerenciador de Tarefas, "
            "e recusa a desinstalação até o bloqueio acabar.\n\n"
            "Nada é enviado pela internet.", parent=self)
        try:
            os.makedirs(os.path.dirname(marca), exist_ok=True)
            open(marca, "w").close()
        except OSError:
            pass


def main() -> int:
    App().mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
