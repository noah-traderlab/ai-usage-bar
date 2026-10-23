# -*- coding: utf-8 -*-
import sys
from . import app

if __name__ == "__main__":
    raise SystemExit(app.cli(sys.argv[1:]))
