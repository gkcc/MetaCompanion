from __future__ import annotations

import sys
from pathlib import Path


SOLVER_ROOT = str(Path(__file__).resolve().parents[1])
if SOLVER_ROOT not in sys.path:
    sys.path.insert(0, SOLVER_ROOT)
