using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AIUsageBar
{
    /// <summary>
    /// 組織の管理者がグループポリシー等で配布する設定。
    /// HKLM\SOFTWARE\Policies\AIUsageBar と HKCU\SOFTWARE\Policies\AIUsageBar の DWORD 値を読み、どちらかで1なら有効。
    /// Policies キーは通常ユーザーが書き換えられないため、利用者が解除できない。
    /// </summary>
    public class Policy
    {
        public const string KeyPath = @"SOFTWARE\Policies\AIUsageBar";

        /// <summary>1：詳細ログ（セッション・入力の一覧）を使わせない。作業フォルダ・依頼文の位置・タイトルも保存しない（トークン数の集計のみ）</summary>
        public bool DisableActivityLog;
        /// <summary>1：依頼文を表示・書き出ししない</summary>
        public bool HidePromptText;
        /// <summary>1：Markdown / CSV の書き出しを禁止</summary>
        public bool DisableExport;
        /// <summary>1：Codex CLI を起動しない（Codexの利用枠を取得しない）</summary>
        public bool DisableCodexFetch;
        /// <summary>1：ログイン時起動の登録を禁止</summary>
        public bool DisableStartupRegistration;
        /// <summary>1：チーム機能（共有フォルダへの報告・マスター機能）を使わせない</summary>
        public bool DisableTeamSharing;
        /// <summary>チームに共有できる範囲の上限（0：利用量のみ／1：作業の概要まで／2：依頼文まで）。未設定なら上限なし</summary>
        public ShareScope? TeamMaxScope;
        /// <summary>1：AI提案機能（外部の Anthropic API へ集計サマリーを送信）を使わせない</summary>
        public bool DisableAiSuggest;
        /// <summary>1：フレンド機能（共有フォルダでの残り枠の見せ合い）を使わせない</summary>
        public bool DisableFriends;

        public readonly List<string> Sources = new List<string>();

        public static readonly string[] Names = { "DisableActivityLog", "HidePromptText", "DisableExport", "DisableCodexFetch", "DisableStartupRegistration", "DisableTeamSharing", "TeamMaxScope", "DisableAiSuggest", "DisableFriends" };

        public bool AnyEnforced
        {
            get { return DisableActivityLog || HidePromptText || DisableExport || DisableCodexFetch || DisableStartupRegistration || DisableTeamSharing || TeamMaxScope != null || DisableAiSuggest || DisableFriends; }
        }

        public static Policy Load()
        {
            var p = new Policy();
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(KeyPath, false))
                    {
                        if (k == null) continue;
                        bool found = false;
                        foreach (var name in Names)
                        {
                            var v = k.GetValue(name);
                            if (!(v is int)) continue;
                            found = true;
                            bool on = (int)v != 0;
                            // HKLM・HKCU のどちらかで 1 なら有効（より厳しい方を採用）
                            switch (name)
                            {
                                case "DisableActivityLog": p.DisableActivityLog |= on; break;
                                case "HidePromptText": p.HidePromptText |= on; break;
                                case "DisableExport": p.DisableExport |= on; break;
                                case "DisableCodexFetch": p.DisableCodexFetch |= on; break;
                                case "DisableStartupRegistration": p.DisableStartupRegistration |= on; break;
                                case "DisableTeamSharing": p.DisableTeamSharing |= on; break;
                                case "DisableAiSuggest": p.DisableAiSuggest |= on; break;
                                case "DisableFriends": p.DisableFriends |= on; break;
                                case "TeamMaxScope":
                                    var cap = (ShareScope)Math.Max(0, Math.Min(2, (int)v));
                                    p.TeamMaxScope = p.TeamMaxScope == null ? cap : ScopeText.Min(p.TeamMaxScope.Value, cap);
                                    break;
                            }
                        }
                        if (found) p.Sources.Add((hive == Registry.LocalMachine ? "HKLM\\" : "HKCU\\") + KeyPath);
                    }
                }
                catch { }
            }
            return p;
        }

        public Dictionary<string, bool> ToDictionary()
        {
            return new Dictionary<string, bool>
            {
                { "DisableActivityLog", DisableActivityLog },
                { "HidePromptText", HidePromptText },
                { "DisableExport", DisableExport },
                { "DisableCodexFetch", DisableCodexFetch },
                { "DisableStartupRegistration", DisableStartupRegistration },
                { "DisableTeamSharing", DisableTeamSharing },
                { "TeamMaxScope", TeamMaxScope != null },
                { "DisableAiSuggest", DisableAiSuggest },
                { "DisableFriends", DisableFriends },
            };
        }
    }
}
