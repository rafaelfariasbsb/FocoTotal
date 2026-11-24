"""Atalho para o instalador: python instalar.py instalar|desinstalar|status [--log arquivo]"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from focototal.instalador import main  # noqa: E402

sys.exit(main())
