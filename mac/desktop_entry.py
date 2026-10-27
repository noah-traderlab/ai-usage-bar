"""Standalone macOS entry point; never joins a team automatically."""
import sys

if __name__ == "__main__":
    if sys.argv[1:] == ["--smoke-test"]:
        # Exercise bundled Tcl/Tk without reading user logs or team state.
        import tkinter as tk
        root = tk.Tk()
        root.withdraw()
        root.update()
        root.destroy()
    else:
        from aiusagebar.gui import main
        raise SystemExit(main())
