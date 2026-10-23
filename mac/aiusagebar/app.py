# -*- coding: utf-8 -*-
"""
Mac 用チーム参加ツール本体。
  ・GUI（Tkinter）：招待コードを貼り付け → 確認コードを照合 → すべての共有に同意して参加。参加後は自動で報告。
  ・CLI：join / report / status / leave（画面が使えない環境や自動化向け）
Mac に最初から入っている Python 3（Tkinter 同梱）だけで動く。追加インストール不要。
"""
import argparse
import os
import sys
import threading
import time

from . import services, team, friends as friendsmod, resetwatch
from .crypto import TeamError
from .logs import LocalLogs


REPORT_INTERVAL_SEC = 5 * 60


def _fmt_remaining(v):
    return "—" if v is None else str(int(v)) + "%"


def _print_friends(others, now=None):
    now = now or friendsmod.now_ms()
    if not others:
        print("  （まだフレンドがいません。相手にも同じ共有フォルダで『フレンド』をオンにしてもらってください）")
        return
    for s in others:
        stale = "  ※しばらく更新なし" if friendsmod.is_stale(s, now) else ""
        cl = s.get("claudeFiveHour")
        clw = s.get("claudeWeekly")
        cx = "・".join("%s %s" % (w["label"], _fmt_remaining(w["remaining"])) for w in s.get("codex", []))
        parts = []
        if cl is not None or clw is not None:
            parts.append("Claude 5h %s / 週 %s" % (_fmt_remaining(cl), _fmt_remaining(clw)))
        if cx:
            parts.append("Codex " + cx)
        print("  %-16s %s%s" % (s["name"], "  ".join(parts) if parts else "残り枠なし", stale))


def _detected_emails():
    out = []
    e = services.claude_email()
    if e:
        out.append(e)
    return out


def _quota():
    q = {"codex": None, "claude": None, "codexServiceDaily": None}
    try:
        snap = services.fetch_codex()
        if snap:
            q["codex"] = services.codex_quota(snap)
            q["codexServiceDaily"] = snap.get("daily")
    except Exception:
        pass
    try:
        c = services.read_claude()
        if c:
            q["claude"] = c
    except Exception:
        pass
    return q


def build_service():
    data_dir = team.default_data_dir()
    logs = LocalLogs()
    logs.scan()
    return team.TeamService(data_dir, logs, _detected_emails, _quota)


def _refresh_logs(svc):
    svc.logs = LocalLogs()
    svc.logs.scan()


# ------------------------------------------------------------------ CLI

def cli(argv):
    ap = argparse.ArgumentParser(prog="aiusagebar", description="AI Usage Bar Mac 参加ツール")
    sub = ap.add_subparsers(dest="cmd")

    j = sub.add_parser("join", help="チームに参加")
    j.add_argument("--invite", help="招待コード、または招待文（ファイルパスでも可）")
    j.add_argument("--folder", help="共有フォルダ（招待コードにフォルダが無い場合）")
    j.add_argument("--fingerprint", help="チーム確認コード（32桁）")
    j.add_argument("--name", default=os.environ.get("USER") or "me")
    j.add_argument("--email", required=True)
    j.add_argument("--yes", action="store_true", help="共有内容に同意して参加")

    sub.add_parser("report", help="今すぐ報告")
    sub.add_parser("status", help="参加状態を表示")
    sub.add_parser("leave", help="チームから抜ける")
    w = sub.add_parser("watch", help="常駐して定期的に報告（フレンド公開・リセット通知も行う）")
    w.add_argument("--reset", type=int, choices=[0, 1, 2], default=0, help="リセット通知 0:しない 1:残りが少なかったときだけ 2:毎回")
    w.add_argument("--threshold", type=int, default=20, help="「少なかったとき」の残り%%（既定20）")
    fr = sub.add_parser("friends", help="フレンドの残り枠（任意・既定オフ）")
    fr.add_argument("action", nargs="?", choices=["show", "on", "off"], default="show", help="show:一覧 on:公開開始 off:公開停止")
    fr.add_argument("--folder", help="共有フォルダ（この中に AIUsageBar-Friends を作る）")
    fr.add_argument("--invite", help="フレンド招待コード（AIUB-FRIEND-1:… フォルダの場所だけ）")
    fr.add_argument("--name", help="フレンドに見せる表示名")
    sub.add_parser("gui", help="画面を開く")

    args = ap.parse_args(argv)
    if args.cmd in (None, "gui"):
        from . import gui
        return gui.main()

    svc = build_service()

    if args.cmd == "status":
        if not svc.local.joined:
            print("未参加です。 join で参加してください。")
            return 0
        print("チーム:", svc.local.team_id)
        print("共有フォルダ:", svc.local.shared_folder)
        print("あなた:", svc.local.declared_name, "/", ", ".join(svc.my_emails()))
        print("このPCの確認コード:", svc.my_pc_fingerprint())
        print("最終報告:", _fmt_ms(svc.local.last_upload))
        return 0

    if args.cmd == "join":
        invite_text = args.invite or ""
        if invite_text and os.path.isfile(invite_text):
            invite_text = open(invite_text, encoding="utf-8").read()
        folder = args.folder
        fp = args.fingerprint
        dec = team.decode_invite(invite_text) if invite_text else None
        if dec:
            folder = folder or dec[0]
            fp = fp or dec[2]
        if not folder or not fp:
            print("共有フォルダとチーム確認コードが必要です（--invite か --folder/--fingerprint）", file=sys.stderr)
            return 2
        try:
            conf, resolved = team.peek(folder)
        except TeamError as ex:
            print("読み込めません:", ex, file=sys.stderr)
            return 1
        print("チーム名:", conf.team_name)
        print("チーム確認コード:", conf.fingerprint)
        print("共有される内容:", _describe_full())
        if not args.yes:
            print("\n※ すべて（依頼文の本文を含む）をマスターに共有することに同意する場合は --yes を付けて実行してください。")
            return 1
        try:
            svc.join(folder, fp, args.name, args.email, confirmed=conf)
        except TeamError as ex:
            print("参加できません:", ex, file=sys.stderr)
            return 1
        print("参加しました。最初の報告を送ります…")
        err = svc.upload(force=True)
        print("報告:", "完了" if err is None else err)
        return 0

    if args.cmd == "friends":
        return _cmd_friends(args)

    if args.cmd == "watch":
        return _cmd_watch(args, svc)

    if not svc.local.joined:
        print("未参加です。 join で参加してください。", file=sys.stderr)
        return 1

    if args.cmd == "report":
        err = svc.upload(force=True)
        print("報告:", "完了" if err is None else err)
        return 0 if err is None else 1

    if args.cmd == "leave":
        svc.leave()
        print("チームから抜けました。共有フォルダの報告を削除しました。")
        return 0

    ap.print_help()
    return 0


def _cmd_friends(args):
    data_dir = team.default_data_dir()
    cfg = friendsmod.load_config(data_dir)
    if args.action == "off":
        friendsmod.withdraw(cfg)
        cfg["enabled"] = False
        friendsmod.save_config(data_dir, cfg)
        print("フレンドの公開をやめ、共有フォルダから自分のファイルを消しました。")
        return 0
    if args.action == "on":
        folder = args.folder or (friendsmod.decode_invite(args.invite) if args.invite else None)
        if not folder:
            print("共有フォルダが必要です（--folder または --invite）", file=sys.stderr)
            return 2
        if not os.path.isdir(folder):
            print("共有フォルダが見つかりません：" + folder, file=sys.stderr)
            return 1
        cfg["folder"] = folder
        cfg["enabled"] = True
        if args.name:
            cfg["displayName"] = args.name
        if not cfg.get("displayName"):
            cfg["displayName"] = os.environ.get("USER") or "me"
        friendsmod.save_config(data_dir, cfg)
        snap = _try(services.fetch_codex)
        claude = _try(services.read_claude)
        try:
            friendsmod.publish(cfg, friendsmod.build_mine(cfg, claude, snap))
        except Exception as ex:  # noqa: BLE001
            print("公開できませんでした：", ex, file=sys.stderr)
            return 1
        print("フレンドを公開しました（フォルダ：%s／表示名：%s）。" % (folder, cfg["displayName"]))
        print("招待コード（相手に伝える）：" + friendsmod.encode_invite(folder))
        print("現在のフレンド：")
        _print_friends(friendsmod.read_others(cfg))
        return 0
    # show
    if not friendsmod.is_configured(cfg):
        print("フレンドはオフです。 friends on --folder <共有フォルダ> でオンにできます。")
        return 0
    print("フレンド（共有フォルダ：%s）：" % cfg["folder"])
    _print_friends(friendsmod.read_others(cfg))
    return 0


def _try(fn):
    try:
        return fn()
    except Exception:  # noqa: BLE001
        return None


def _cmd_watch(args, svc):
    data_dir = team.default_data_dir()
    fcfg = friendsmod.load_config(data_dir)
    watcher = resetwatch.ResetWatcher()
    last_claude = None
    do_team = svc.local.joined
    do_friends = friendsmod.is_configured(fcfg)
    if not do_team and not do_friends and not args.reset:
        print("参加もフレンドもオフです。 join で参加するか、 friends on でフレンドをオンにしてください。", file=sys.stderr)
        return 1
    msg = "常駐を開始します（Ctrl+C で終了）。"
    if args.reset:
        msg += "　リセット通知：" + resetwatch.MODE_NAMES[args.reset]
    print(msg)
    try:
        while True:
            snap = _try(services.fetch_codex) if (do_friends or args.reset) else None
            claude = _try(services.read_claude) if (do_friends or args.reset) else None
            if do_team:
                _refresh_logs(svc)
                err = svc.upload(force=False)
                print(time.strftime("%H:%M"), "報告:", "完了" if err is None else err)
            if do_friends:
                try:
                    friendsmod.publish(fcfg, friendsmod.build_mine(fcfg, claude, snap))
                    others = friendsmod.read_others(fcfg)
                    print(time.strftime("%H:%M"), "フレンド:", len(others), "人")
                except Exception as ex:  # noqa: BLE001
                    print(time.strftime("%H:%M"), "フレンド: 更新できません（", ex, "）")
            if args.reset:
                for n in watcher.check_codex(snap, args.reset, args.threshold):
                    resetwatch.notify(n["title"], n["text"])
                    print("  通知:", n["title"])
                if last_claude is not None:
                    for n in watcher.check_claude(last_claude, claude, args.reset, args.threshold):
                        resetwatch.notify(n["title"], n["text"])
                        print("  通知:", n["title"])
                if claude is not None:
                    last_claude = claude
            time.sleep(REPORT_INTERVAL_SEC)
    except KeyboardInterrupt:
        print("\n終了します。")
    return 0


def _fmt_ms(ms):
    if not ms:
        return "まだありません"
    return time.strftime("%Y-%m-%d %H:%M", time.localtime(ms / 1000))


def _describe_full():
    return ("PC名・ユーザー名・メールアドレス、利用量（トークン数・残り枠・日別・モデル別）、"
            "セッションのタイトル・作業フォルダ・時間帯、Claude Code・Codex に入力した依頼文の本文（直近14日）")


def start_background_reporter(svc, on_status=None):
    """GUI から使う：別スレッドで定期的に報告する。"""
    stop = threading.Event()

    def loop():
        first = True
        while not stop.is_set():
            try:
                _refresh_logs(svc)
                err = svc.upload(force=first)
                if on_status:
                    on_status(err)
            except Exception as ex:  # noqa: BLE001
                if on_status:
                    on_status(str(ex))
            first = False
            stop.wait(REPORT_INTERVAL_SEC)

    th = threading.Thread(target=loop, daemon=True)
    th.start()
    return stop
