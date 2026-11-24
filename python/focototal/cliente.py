"""Cliente do serviço: uma linha JSON por pedido em 127.0.0.1."""

from __future__ import annotations

import json
import socket

from . import caminhos


class ServicoIndisponivel(Exception):
    pass


def enviar(pedido: dict, timeout: float = 5.0) -> dict:
    try:
        with socket.create_connection(("127.0.0.1", caminhos.PORTA), timeout=timeout) as s:
            s.sendall((json.dumps(pedido, ensure_ascii=False) + "\n").encode("utf-8"))
            f = s.makefile("rb")
            linha = f.readline()
    except OSError as e:
        raise ServicoIndisponivel(str(e)) from e
    if not linha:
        raise ServicoIndisponivel("resposta vazia")
    return json.loads(linha.decode("utf-8"))
