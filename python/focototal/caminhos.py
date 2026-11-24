"""Onde o FocoTotal fica instalado e guarda dados."""

import os
import sys

VERSAO = "0.2.0"
NOME_TAREFA = "FocoTotal"
PORTA = 47613  # só em 127.0.0.1

if sys.platform == "win32":
    PASTA_PROGRAMA = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "FocoTotal")
    PASTA_DADOS = os.path.join(os.environ.get("ProgramData", r"C:\ProgramData"), "FocoTotal")
    HOSTS = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "drivers", "etc", "hosts")
else:  # para testes fora do Windows
    _base = os.environ.get("FOCOTOTAL_TESTE", "/tmp/focototal-teste")
    PASTA_PROGRAMA = os.path.join(_base, "programa")
    PASTA_DADOS = os.path.join(_base, "dados")
    HOSTS = os.path.join(_base, "hosts")

CHAVE_REGISTRO = r"SOFTWARE\FocoTotal"
