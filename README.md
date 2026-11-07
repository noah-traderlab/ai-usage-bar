# AI Usage Bar — public source archive

This repository preserves previously published MIT-licensed AI Usage Bar source and release provenance. Active development was consolidated into the private `ai-usage-bar/` directory of [noah-traderlab/ai-project](https://github.com/noah-traderlab/ai-project) on 2026-11-03. That repository requires access; this archive does not grant access to it or contain subsequent private changes.

On 2026-11-05, the maintainer restored this previously public repository to make existing fixed source and maintenance links readable again. This is an archive for inspection, not a second development location. The restored historical source ends at [`610d9fff`](https://github.com/noah-traderlab/ai-usage-bar/tree/610d9fff7fd14eb02bc6325e5b85c6dcbffdb0ec). Archival presentation adds this notice, the separately dated verification workflow/results, and the current feedback notice dated below; historical source and fixed revisions remain unchanged.

## Current feedback channel — added 2026-11-08

[Share feedback — optional, no sign-in or name required](https://docs.google.com/forms/d/e/1FAIpQLScucMa18bwOd0HByQKaym4i5IFk9-P_EEkQg2sbT11G-9GJpg/viewform). Short comments, difficulties, and questions are welcome, including from people who have not used the product. Responses are kept non-public for product improvement.

[使用感を送る（匿名・ログイン不要・任意）](https://docs.google.com/forms/d/e/1FAIpQLScucMa18bwOd0HByQKaym4i5IFk9-P_EEkQg2sbT11G-9GJpg/viewform)。短い感想・困った点・不明点を歓迎します。未使用でも構いません。回答は一般公開せず、製品改善のため保管します。

This dated notice does not update any archived executable or historical verification result. No feedback is required to download or use the tool. For ordinary support and private security reporting, see the [current support repository](https://github.com/noah-traderlab/ai-usage-bar-support). Do not submit vulnerability details through the feedback form. Privacy and correction/deletion information is in the form.

## Source and maintenance evidence

- Named maintainer: [水野拓真 / @noah-traderlab](https://github.com/noah-traderlab). The public profile association is not independent identity verification.
- [Submitted maintenance index at its original revision](https://github.com/noah-traderlab/ai-usage-bar/blob/02f534140308e9408e1211c8cc9489621cabe549/docs/security-evidence.md) and [responsibilities with AI-assisted attribution](https://github.com/noah-traderlab/ai-usage-bar/blob/02f534140308e9408e1211c8cc9489621cabe549/docs/security-maintenance.md).
- [Fixed implementation](https://github.com/noah-traderlab/ai-usage-bar/blob/02f534140308e9408e1211c8cc9489621cabe549/mac/aiusagebar/team.py), [synthetic regression tests](https://github.com/noah-traderlab/ai-usage-bar/blob/02f534140308e9408e1211c8cc9489621cabe549/mac/test_secure_writes.py), and [contemporaneous explanation and limits](https://github.com/noah-traderlab/ai-usage-bar/blob/02f534140308e9408e1211c8cc9489621cabe549/docs/security-fix-2026-10-23.md). The correction was included in the initial public commit; this public history does not contain a pre-fix revision.
- [MIT license](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).
- [Windows 0.1.0 release](https://github.com/noah-traderlab/ai-usage-bar/releases/tag/windows-v0.1.0) uses [source 1148ae8](https://github.com/noah-traderlab/ai-usage-bar/tree/1148ae8f7ba302f3981a62798b5b58476ce3c63a). It is not Authenticode signed.
- [macOS Python companion 0.1.0 on PyPI](https://pypi.org/project/ai-usage-bar-mac/0.1.0/) retains its source distribution and publishing attestations. No package was republished for this archive.

Historical documents describe the status at their dates. The historical Actions runs checked during restoration were unavailable; those runs are not presented as newly verified here. Any new verification below is a distinct, dated run against the unchanged historical source. The standalone macOS app has not been published as a signed/notarized release.

## Verification performed on 2026-11-05

[Historical source verification run 42003193783 (public GitHub API status)](https://api.github.com/repos/noah-traderlab/ai-usage-bar/actions/runs/42003193783) completed successfully on all three operating systems. The workflow definition is commit `58fdd0e02c9325fec9689e65f39976b87058b98b`; each job explicitly checked out the historical source `610d9fff7fd14eb02bc6325e5b85c6dcbffdb0ec`.

| Environment | Checks observed in this run |
| --- | --- |
| Windows | Build, 305 unit checks, 147 team checks, 30 C#/Python interoperability checks; zero reported failures |
| macOS | 3 state-file regression tests and 36 selftest checks; zero reported failures |
| Linux | 3 state-file regression tests and 36 selftest checks; zero reported failures |

The public GitHub API status above is accessible without login; the run HTML page is unavailable for this archive. This was one new manual verification run, not restoration of the missing historical CI logs. It did not publish a package or release, exercise end-user GUI operation, or independently audit security. Japanese labels in the Windows log are partly garbled; the numeric pass/fail totals and job exit results remain readable.

This archive is not an active vulnerability-reporting channel. Historical references to private reporting describe the earlier repository configuration. Do not post credentials, private logs, personal information, or vulnerability details publicly; the [security policy](SECURITY.md) provides the fallback when a private channel is unavailable.

Availability, tests, and maintainer-authored documentation do not establish third-party adoption, a CVE, independent disclosure acceptance, or eligibility for an external program. Restoration and documentation were prepared with AI assistance at the maintainer's direction.

---

## Historical README (preserved below)

The following text describes the former development repository. Its present-tense maintenance and CI statements must be read with the archive notice above.

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
