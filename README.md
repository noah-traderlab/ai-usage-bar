# AI Usage Bar

Claude CodeとCodexの利用状況を表示するWindowsアプリと、Mac用のチーム参加・報告ツールです。

## 構成

- `src/`: Windowsアプリ（C# / .NET Framework）
- `installer/`: Windowsインストーラー
- `mac/`: PythonのMac用ツール
- `tests/`: Windows用テスト
- `docs/`: 製品説明と仕様

Windows版は `build.ps1` と同梱の「はじめに」を参照してください。Mac版は `mac/はじめに（Mac）.txt` を参照してください。

Windows専用の配布ZIPは[Windows 0.1.0リリース](https://github.com/noah-traderlab/ai-usage-bar/releases/tag/windows-v0.1.0)から入手できます。[インストール案内](docs/windows-install.md)と署名の制限を確認してください。公開ソースから再ビルドした配布物で、旧ZIPの画像・Macソースは含みません。

## セキュリティ修正の検証

```sh
cd mac
python3 -m unittest test_secure_writes -v
python3 selftest.py
```

2026-10-23の検証では、追加した3テストメソッドと既存36チェックが成功しています。GitHub ActionsのmacOS・Linux環境でも実行し、POSIXの0600権限を確認しました。MacのGUI操作を含む利用者端末での動作確認は未完了です。

チームの状態ファイルには秘密鍵が含まれます。状態ファイル・実際のログ・認証情報をリポジトリやIssueに添付しないでください。

## 公開ソースの状態

このリポジトリは開発中のソース公開版です。利用者端末でのGUI動作確認は未完了で、動作保証付きのバイナリリリースではありません。

Windowsでの確認: 単体テスト305件、チーム機能147件、C#/Python相互運用30件が成功しています。CIの結果はGitHub Actionsを参照してください。

元アイコンは出所・再配布許諾を確認できないため除外しました。カスタムアイコンなしでビルドできます。詳細は `THIRD_PARTY_NOTICES.md` を参照してください。

ClaudeやCodexなどの名称は対応先を示すものです。本ツールは独立したプロジェクトです。

## ライセンス

[MIT License](LICENSE)。コード・文章の作成者による公開とMIT適用の承認に基づき公開しています。第三者のランタイムや商標を再許諾するものではありません。

## MacアプリとPythonパッケージ

Mac用ツールのPython同梱アプリ・DMGをApple Silicon / Intel別にビルドします。[インストールと検証範囲](docs/macos-app.md)を参照してください。現段階はアドホック署名の開発用ビルドで、Developer ID署名・Apple公証は未実施です。

[PyPI版0.1.0](https://pypi.org/project/ai-usage-bar-mac/0.1.0/)も公開済みで、引き続き利用できます。ビルド、インストール検査、公開手順と採用統計の扱いは [配布手順](docs/python-package-release.md) を参照してください。
