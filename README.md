# AI Usage Bar

Claude CodeとCodexの利用状況を表示するWindowsアプリと、Mac用のチーム参加・報告ツールです。

## 構成

- `src/`: Windowsアプリ（C# / .NET Framework）
- `installer/`: Windowsインストーラー
- `mac/`: PythonのMac用ツール
- `tests/`: Windows用テスト
- `docs/`: 製品説明と仕様

Windows版は `build.ps1` と同梱の「はじめに」を参照してください。Mac版は `mac/はじめに（Mac）.txt` を参照してください。

## セキュリティ修正の検証

```sh
cd mac
python3 -m unittest test_secure_writes -v
python3 selftest.py
```

2026-10-23のWindows上での検証では、追加した3テストメソッドと既存36チェックが成功しています。Mac実機での実行とPOSIX権限の確認は未完了です。

チームの状態ファイルには秘密鍵が含まれます。状態ファイル・実際のログ・認証情報をリポジトリやIssueに添付しないでください。

## 公開ソースの状態

このリポジトリは開発中のソース公開版です。Mac実機での動作確認は未完了で、動作保証付きのバイナリリリースではありません。

Windowsでの確認: 単体テスト305件、チーム機能147件、C#/Python相互運用30件が成功しています。CIの結果はGitHub Actionsを参照してください。

元アイコンは出所・再配布許諾を確認できないため除外しました。カスタムアイコンなしでビルドできます。詳細は `THIRD_PARTY_NOTICES.md` を参照してください。

ClaudeやCodexなどの名称は対応先を示すものです。本ツールは独立したプロジェクトです。

## ライセンス

[MIT License](LICENSE)。コード・文章の作成者による公開とMIT適用の承認に基づき公開しています。第三者のランタイムや商標を再許諾するものではありません。
