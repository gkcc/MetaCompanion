"""Source-tree launcher; packaging exposes the same CLI as metacompanion-solver."""

from metacompanion_solver.cli import main


if __name__ == "__main__":
    raise SystemExit(main())
