using System;
using System.Collections.Generic;

namespace AIUsageBar.Tests
{
    /// <summary>
    /// 「AIに提案してもらう」を、アプリと同じ経路（AiClient）で実際に試す確認用。
    /// APIキーは環境変数 ANTHROPIC_API_KEY から読む（コマンド履歴に残さないため）。
    /// モデルは環境変数 AIUB_MODEL で変更可（既定 claude-opus-5）。実際にAPIを呼ぶので少額の費用がかかる。
    /// </summary>
    public static partial class TestMain
    {
        static int AiCheck()
        {
            string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine("環境変数 ANTHROPIC_API_KEY が設定されていません。");
                Console.WriteLine("PowerShell 例： $env:ANTHROPIC_API_KEY=\"sk-ant-...\"; .\\build\\tests.exe --ai-check");
                return 2;
            }
            var cfg = new AiConfig();
            cfg.SetKey(key);
            string provider = Environment.GetEnvironmentVariable("AIUB_PROVIDER");
            if (!string.IsNullOrWhiteSpace(provider)) cfg.Provider = provider.Trim().ToLowerInvariant();
            string model = Environment.GetEnvironmentVariable("AIUB_MODEL");
            if (!string.IsNullOrWhiteSpace(model)) cfg.Model = model.Trim();
            string baseUrl = Environment.GetEnvironmentVariable("AIUB_BASEURL");
            if (!string.IsNullOrWhiteSpace(baseUrl)) { cfg.Provider = "openai"; cfg.BaseUrl = baseUrl.Trim(); }
            if (cfg.IsOpenAi && string.IsNullOrWhiteSpace(cfg.BaseUrl))
            {
                Console.WriteLine("OpenAI互換モードでは AIUB_BASEURL（例：https://.../v1）が必要です。");
                return 2;
            }
            Console.WriteLine("提供元：" + cfg.Provider + (cfg.IsOpenAi ? "  エンドポイント：" + cfg.BaseUrl : "") + "  モデル：" + cfg.Model);

            var rows = new List<ExecRow>
            {
                new ExecRow { Name = "山田", IsMember = true, Claude = 5200000, Codex = 0, Output = 210000, Jpy = 1250, ActiveDays = 6, Prompts = 42, Sessions = 12, UsesClaude = true, UsesCodex = false, Models = 1 },
                new ExecRow { Name = "佐藤", IsMember = true, Claude = 800000, Codex = 300000, Output = 40000, Jpy = 260, ActiveDays = 3, Prompts = 8, Sessions = 4, UsesClaude = true, UsesCodex = true, Models = 2 },
                new ExecRow { Name = "鈴木", IsMember = true, Claude = 0, Codex = 0, ActiveDays = 0, Prompts = 0 },
            };
            var today = DateTime.Now.Date;
            var daily = new List<DailyPoint>();
            for (int i = 6; i >= 0; i--)
                daily.Add(new DailyPoint { Date = today.AddDays(-i).ToString("yyyy-MM-dd"), Claude = 700000 + i * 50000, Codex = i == 1 ? 300000 : 0 });

            var insights = new List<string>();
            foreach (var it in TeamAnalytics.Insights(rows, 3)) insights.Add(it.Text);
            string summary = AiClient.BuildSummary("開発部（見本）", "今月", rows, daily, 3, insights);

            Console.WriteLine("=== 送信するサマリー（依頼文の本文は含まない） ===");
            Console.WriteLine(summary);
            Console.WriteLine("=== 送信中…（" + (cfg.IsOpenAi ? cfg.BaseUrl : "Anthropic") + "） ===");
            try
            {
                string result = AiClient.Suggest(cfg, summary);
                Console.WriteLine("=== AIの提案 ===");
                Console.WriteLine(result);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("エラー：" + ex.Message);
                return 1;
            }
        }
    }
}
