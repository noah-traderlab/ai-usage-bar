# -*- coding: utf-8 -*-
"""
Mac 用チーム参加ツールの画面（Tkinter・Mac 標準の Python に同梱）。
招待コードを貼り付け → 確認コードを照合 → すべての共有に同意して参加。参加後は自動で報告。
"""
import threading

try:
    import tkinter as tk
    from tkinter import ttk, filedialog, messagebox
except Exception:  # noqa: BLE001
    tk = None

from . import app, team, services
from .crypto import TeamError


def main():
    if tk is None:
        print("この環境では画面を開けません。コマンドで実行してください： python3 -m aiusagebar join --help")
        return 1
    svc = app.build_service()
    root = tk.Tk()
    root.title("AI Usage Bar — チーム参加（Mac）")
    root.geometry("620x680")
    Ui(root, svc)
    root.mainloop()
    return 0


class Ui:
    def __init__(self, root, svc):
        self.root = root
        self.svc = svc
        self.reporter_stop = None
        self.peeked = None
        self.folder = None
        pad = {"padx": 16, "pady": 6}
        self.frame = ttk.Frame(root)
        self.frame.pack(fill="both", expand=True)
        if svc.local.joined:
            try:
                svc.load_team()
            except TeamError:
                pass
            self._joined_view()
        else:
            self._join_view(pad)

    def _clear(self):
        for w in self.frame.winfo_children():
            w.destroy()

    # ------------------------------------------------------------------ 参加画面

    def _join_view(self, pad):
        self._clear()
        f = self.frame
        ttk.Label(f, text="チームに参加", font=("", 18, "bold")).pack(anchor="w", **pad)
        ttk.Label(f, text="マスターから届いた招待コード（AIUB-INVITE-1: で始まる文字列）を貼り付けてください。招待文をまるごと貼り付けても読み取れます。",
                  wraplength=560, foreground="#555").pack(anchor="w", padx=16)

        self.invite = tk.Text(f, height=4, wrap="word")
        self.invite.pack(fill="x", padx=16, pady=6)
        self.invite.bind("<KeyRelease>", lambda e: self._maybe_decode())

        row = ttk.Frame(f); row.pack(fill="x", padx=16)
        self.folder_var = tk.StringVar()
        ttk.Label(row, text="共有フォルダ").pack(side="left")
        ttk.Entry(row, textvariable=self.folder_var).pack(side="left", fill="x", expand=True, padx=6)
        ttk.Button(row, text="参照…", command=self._pick_folder).pack(side="left")
        ttk.Button(f, text="読み込む", command=self._load).pack(anchor="w", padx=16, pady=6)

        self.info = ttk.Label(f, text="（まだ読み込んでいません）", wraplength=560, foreground="#333")
        self.info.pack(anchor="w", padx=16, pady=4)

        grid = ttk.Frame(f); grid.pack(fill="x", padx=16, pady=4)
        ttk.Label(grid, text="チーム確認コード").grid(row=0, column=0, sticky="w")
        self.fp_var = tk.StringVar()
        ttk.Entry(grid, textvariable=self.fp_var, font=("Menlo", 12)).grid(row=0, column=1, sticky="we", padx=6)
        ttk.Label(grid, text="あなたの名前").grid(row=1, column=0, sticky="w")
        self.name_var = tk.StringVar(value=self.svc.local.declared_name or _default_name())
        ttk.Entry(grid, textvariable=self.name_var).grid(row=1, column=1, sticky="we", padx=6)
        ttk.Label(grid, text="メールアドレス").grid(row=2, column=0, sticky="w")
        self.email_var = tk.StringVar(value=(self.svc.my_emails() or [""])[0])
        ttk.Entry(grid, textvariable=self.email_var).grid(row=2, column=1, sticky="we", padx=6)
        grid.columnconfigure(1, weight=1)

        ttk.Label(f, text="マスターに共有される内容：\n" + app._describe_full(), wraplength=560, foreground="#333",
                  justify="left").pack(anchor="w", padx=16, pady=(10, 4))
        self.agree = tk.BooleanVar(value=False)
        ttk.Checkbutton(f, text="上記のすべて（依頼文の本文を含む）をマスターに共有することに同意します",
                        variable=self.agree).pack(anchor="w", padx=16)
        ttk.Label(f, text="同意しない場合は参加できません。対象はこの Mac の Claude Code・Codex の記録と利用枠です（通常チャットは含みません）。"
                  "報告は暗号化され、マスター以外は読めません。共有をやめるときは「チームから抜ける」を選ぶと報告を削除します。",
                  wraplength=560, foreground="#777").pack(anchor="w", padx=16, pady=6)

        ttk.Button(f, text="参加する", command=self._join).pack(anchor="e", padx=16, pady=12)

    def _maybe_decode(self):
        dec = team.decode_invite(self.invite.get("1.0", "end"))
        if not dec:
            return
        self.fp_var.set(dec[2])
        if not self.folder_var.get():
            self.folder_var.set(dec[0])

    def _pick_folder(self):
        d = filedialog.askdirectory()
        if d:
            self.folder_var.set(d)

    def _load(self):
        try:
            self.peeked, self.folder = team.peek(self.folder_var.get())
            masters = "、".join(m["name"] for m in self.peeked.members if m["isMaster"])
            self.info.config(text="チーム名：%s\nマスター：%s\nチーム確認コードを確認し、下の内容に同意して参加してください。"
                             % (self.peeked.team_name, masters))
        except Exception as ex:  # noqa: BLE001
            self.peeked = None
            self.info.config(text="読み込めませんでした：%s" % ex)

    def _join(self):
        if not self.peeked:
            messagebox.showwarning("確認", "先に「読み込む」でチームの内容を確認してください。")
            return
        if not self.agree.get():
            messagebox.showwarning("確認", "共有への同意にチェックを入れてください。")
            return
        try:
            self.svc.join(self.folder_var.get(), self.fp_var.get(), self.name_var.get(),
                          self.email_var.get(), confirmed=self.peeked)
        except TeamError as ex:
            messagebox.showerror("参加できません", str(ex))
            return
        self._joined_view()

    # ------------------------------------------------------------------ 参加後

    def _joined_view(self):
        self._clear()
        f = self.frame
        ttk.Label(f, text="参加中", font=("", 18, "bold")).pack(anchor="w", padx=16, pady=(16, 4))
        team_name = self.svc.team.team_name if self.svc.team else self.svc.local.team_id
        rows = [
            ("チーム", team_name),
            ("共有フォルダ", self.svc.local.shared_folder),
            ("あなた", "%s（%s）" % (self.svc.local.declared_name, ", ".join(self.svc.my_emails()))),
            ("このPCの確認コード", self.svc.my_pc_fingerprint() or "—"),
            ("共有される内容", "依頼文まで（すべて共有に同意）"),
            ("最終報告", app._fmt_ms(self.svc.local.last_upload)),
        ]
        g = ttk.Frame(f); g.pack(fill="x", padx=16, pady=6)
        for i, (k, v) in enumerate(rows):
            ttk.Label(g, text=k, foreground="#777").grid(row=i, column=0, sticky="nw", pady=2)
            ttk.Label(g, text=v, wraplength=440, justify="left").grid(row=i, column=1, sticky="w", padx=10, pady=2)
        g.columnconfigure(1, weight=1)

        self.status = ttk.Label(f, text="自動で報告しています（変更があったとき・最短5分ごと）。", foreground="#1c8574")
        self.status.pack(anchor="w", padx=16, pady=8)

        bar = ttk.Frame(f); bar.pack(anchor="w", padx=16, pady=8)
        ttk.Button(bar, text="今すぐ報告", command=self._report_now).pack(side="left")
        ttk.Button(bar, text="チームから抜ける", command=self._leave).pack(side="left", padx=8)

        ttk.Label(f, text="この画面は閉じても構いません。メニューバーからの常駐表示は今後のバージョンで対応予定です。"
                  "自動報告を続けるには、この画面を開いたままにしてください。",
                  wraplength=560, foreground="#777").pack(anchor="w", padx=16, pady=10)

        if self.reporter_stop is None:
            self.reporter_stop = app.start_background_reporter(self.svc, self._on_report)

    def _on_report(self, err):
        def upd():
            if err is None:
                self.status.config(text="報告しました：%s" % app._fmt_ms(self.svc.local.last_upload), foreground="#1c8574")
            else:
                self.status.config(text="報告：%s" % err, foreground="#b0602c")
        try:
            self.root.after(0, upd)
        except Exception:  # noqa: BLE001
            pass

    def _report_now(self):
        def go():
            app._refresh_logs(self.svc)
            err = self.svc.upload(force=True)
            self._on_report(err)
        threading.Thread(target=go, daemon=True).start()

    def _leave(self):
        if not messagebox.askyesno("確認", "チームから抜けますか？共有フォルダのこのPCの報告を削除します。"):
            return
        if self.reporter_stop:
            self.reporter_stop.set()
            self.reporter_stop = None
        self.svc.leave()
        self._join_view({"padx": 16, "pady": 6})


def _default_name():
    import os
    return os.environ.get("USER") or "me"
