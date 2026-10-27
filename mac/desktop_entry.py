"""Standalone macOS entry point; never joins a team automatically."""
import sys

if __name__ == "__main__":
    if sys.argv[1:] == ["--smoke-test"]:
        # Exercise bundled Tcl/Tk without reading user logs or team state.
        import tkinter as tk
        from types import SimpleNamespace
        from aiusagebar.gui import Ui
        root = tk.Tk()
        root.withdraw()
        fake_service = SimpleNamespace(
            local=SimpleNamespace(joined=False, declared_name="CI smoke test"),
            my_emails=lambda: ["smoke@example.invalid"],
        )
        view = Ui(root, fake_service)
        assert not view.agree.get(), "Sharing consent must start unchecked"
        assert view.reporter_stop is None, "Unjoined app must not report"
        root.update()
        root.destroy()
    else:
        from aiusagebar.gui import main
        raise SystemExit(main())
