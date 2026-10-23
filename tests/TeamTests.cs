using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIUsageBar.Tests
{
    /// <summary>チーム機能（マスター／一般ユーザー、共有フォルダ、暗号化・署名、権限）のテスト</summary>
    public static partial class TestMain
    {
        class Install
        {
            public AppPaths Paths;
            public UsageStore Store;
            public Activity Activity;
            public TeamService Svc;
        }

        static Install NewInstall(Action<AppPaths> setup)
        {
            var paths = FakePaths(Tmp());
            if (setup != null) setup(paths);
            var store = new UsageStore(paths, new Settings(), null);
            store.Logs.Scan(null);
            var act = new Activity(store);
            return new Install { Paths = paths, Store = store, Activity = act, Svc = new TeamService(store, act) };
        }

        /// <summary>本物のPCの鍵で、作成日時だけを変えた報告を作る（未来日時の検査用）</summary>
        static void ForgeReport(Install inst, DateTime createdUtc)
        {
            var svc = inst.Svc;
            var team = svc.LoadTeam();
            byte[] plain = TeamService.Pad(Encoding.UTF8.GetBytes(Json.Serialize(svc.BuildPayload(ShareScope.Usage))));
            var header = new Dictionary<string, object> {
                { "format", "aiub-report-2" }, { "teamId", team.TeamId }, { "keyGeneration", (long)team.KeyGeneration },
                { "installId", svc.Local.InstallId }, { "createdAt", Fmt.ToUnixMs(createdUtc) } };
            using (var mk = TeamCrypto.ImportPrivateJson(TeamCrypto.DpapiUnprotect(svc.Local.MemberKeyProtected, "member-key")))
            using (var tp = TeamCrypto.ImportPublic(team.PublicKey, TeamCrypto.TeamKeyBits))
            {
                header["memberPublicKey"] = TeamCrypto.ExportPublic(mk);
                var doc = new Dictionary<string, object>(header);
                doc["payload"] = TeamCrypto.EncryptForTeam(tp, plain, TeamCrypto.Canonical(header));
                doc["signature"] = TeamCrypto.Sign(mk, doc);
                File.WriteAllText(Path.Combine(TeamService.ReportsDir(svc.Local.SharedFolder), svc.Local.InstallId + ".aiubr"), Json.Serialize(doc));
            }
        }

        static bool Throws2<T>(Action a) where T : Exception
        {
            try { a(); return false; } catch (T) { return true; } catch { return false; }
        }

        static bool Throws(Action a, string contains = null)
        {
            try { a(); return false; }
            catch (TeamException ex) { return contains == null || ex.Message.Contains(contains); }
        }

        static void TeamUnit()
        {
            Console.WriteLine("== チーム：作成（マスター）");
            string shared = Path.Combine(Tmp(), "共有 フォルダ");
            Directory.CreateDirectory(shared);
            const string pass = "correct horse battery staple";
            var boss = NewInstall(null);
            Check(Throws(() => boss.Svc.CreateTeam(shared, "開発部", "ボス", "boss@example.com", "short"), "12文字以上"), "短いパスフレーズは拒否");
            Check(Throws(() => boss.Svc.CreateTeam(shared, "開発部", "ボス", "not-an-email", pass), "メールアドレス"), "不正なメールアドレスは拒否");
            boss.Svc.CreateTeam(shared, "開発部", "ボス", "boss@example.com", pass);
            string folder = Path.Combine(shared, TeamService.FolderName);
            Check(File.Exists(TeamService.TeamFile(folder)) && File.Exists(TeamService.KeyFile(folder)) && Directory.Exists(TeamService.ReportsDir(folder)), "共有フォルダに team.json・鍵ファイル・reports を作成");
            Check(boss.Svc.IsMaster, "作成した人はマスター");
            string keyFile = File.ReadAllText(TeamService.KeyFile(folder));
            Check(!keyFile.Contains("\"d\"") && !keyFile.Contains("\"p\":") && keyFile.Contains("pbkdf2-sha256"), "チーム秘密鍵はパスフレーズで暗号化して保存（平文の鍵を置かない）");
            string localM = File.ReadAllText(TeamLocal.PathIn(boss.Paths.DataDir));
            Check(!localM.Contains("\"d\"") && !localM.Contains(pass), "このPCの鍵は DPAPI で保護し、パスフレーズは保存しない");
            Check(Throws(() => boss.Svc.CreateTeam(shared, "二重", "x", "x@example.com", pass), "既にチーム"), "同じフォルダに2つ目のチームは作らない");

            Console.WriteLine("== チーム：参加（一般ユーザー）");
            var now = DateTime.UtcNow.AddMinutes(-30);
            string sid = "cccccccc-bbbb-cccc-dddd-eeeeeeeeeeee";
            var alice = NewInstall(p => WriteLines(Path.Combine(p.ClaudeProjects, "C--work-secret", sid + ".jsonl"),
                "{\"type\":\"ai-title\",\"aiTitle\":\"見積もりの作成\",\"sessionId\":\"" + sid + "\"}",
                ClaudeUser(sid, now, "顧客A社の見積もりを作って"),
                ClaudeAssistant(sid, "msg_t1", "req_t1", now.AddSeconds(5), 1000, 2000, 30000, 4000)));
            string folderSeen;
            var peek = TeamService.Peek(shared, out folderSeen);
            Check(peek.Fingerprint == boss.Svc.Team.Fingerprint && folderSeen == folder, "参加前にチーム確認コードを表示できる（マスターと照合）");
            Check(peek.Fingerprint.Replace("-", "").Length == 32, "チーム確認コードは128bit（32桁）");
            Check(Throws(() => alice.Svc.Join(shared, "0000-1111-2222-3333-4444-5555-6666-7777", "アリス", "alice@example.com", ShareScope.Prompts), "確認コード"), "入力した確認コードが一致しなければ参加しない");
            {
                string other = Path.Combine(Tmp(), "別チーム");
                Directory.CreateDirectory(other);
                NewInstall(null).Svc.CreateTeam(other, "別", "x", "x@example.com", pass);
                string otherFolder;
                var otherPeek = TeamService.Peek(other, out otherFolder);
                Check(Throws(() => alice.Svc.Join(shared, peek.Fingerprint, "アリス", "alice@example.com", ShareScope.Prompts, otherPeek), "確認した後に"), "確認した内容と参加時に読んだ内容が違えば参加しない（すり替え対策）");
            }
            Check(!alice.Svc.Local.Joined, "参加に失敗した場合は未参加のまま");
            alice.Svc.Join(shared, peek.Fingerprint.ToLowerInvariant().Replace("-", ""), "アリス", "alice@example.com", ShareScope.Prompts, peek);
            Check(alice.Svc.Local.Joined && !alice.Svc.IsMaster, "参加した人は一般ユーザー（確認コードは大文字小文字・ハイフンを問わない）");
            Check(alice.Svc.MyPcFingerprint() != null && alice.Svc.MyPcFingerprint().Length == 39, "このPCのチーム確認コードを表示できる（承認時の照合用）");

            Console.WriteLine("== チーム：報告と閲覧範囲");
            Check(alice.Svc.EffectiveScope(alice.Svc.LoadTeam()) == ShareScope.Usage, "未登録の人は、チームの既定（利用量のみ）で共有");
            Check(alice.Svc.Upload(true) == null, "報告を書き込める");
            string reportPath = Path.Combine(TeamService.ReportsDir(folder), alice.Svc.Local.InstallId + ".aiubr");
            string reportRaw = File.ReadAllText(reportPath);
            Check(!reportRaw.Contains("alice@example.com") && !reportRaw.Contains("アリス") && !reportRaw.Contains("見積もり") && !reportRaw.Contains("claude-opus-5"), "報告の中身は暗号化されている（メール・名前・内容が読めない）");
            Check(new FileInfo(reportPath).Length > 64 * 1024, "報告は段階的な長さにそろえてから暗号化（サイズから内容量を推測しにくい）");
            alice.Svc.Local.LastUploadUtc = DateTime.UtcNow.AddMinutes(-10);
            Check(alice.Svc.Upload(false) == "変更なし", "内容が変わっていなければ書き込まない");
            Check(Throws(() => alice.Svc.ReadReports(), "マスターだけ"), "一般ユーザーは報告を読めない");

            var reports = boss.Svc.ReadReports();
            var ra = reports.FirstOrDefault(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra != null && ra.Error == null && ra.SignatureOk && !ra.Approved, "マスターは報告を開ける（初めてのPCは承認待ち）", ra == null ? "null" : ra.Error);
            Check(ra != null && ra.Scope == ShareScope.Usage && ra.Sessions.Count == 0 && ra.Prompts.Count == 0 && ra.Daily.Sum(d => d.Sum.Total) == 37000, "利用量のみの報告には作業内容・依頼文が含まれない");
            boss.Svc.SetApproval(ra, true);
            Check(boss.Svc.ReadReports().First(r => r.InstallId == ra.InstallId).Approved, "マスターが承認すると集計に含まれる");

            var edit = boss.Svc.Team.Clone();
            edit.Members.Add(new MemberEntry { Id = "alice", Name = "アリス", Emails = { "Alice@Example.com" }, Scope = ShareScope.Prompts, Plans = { new PlanEntry { Name = "Claude Max", MonthlyFeeJpy = 30000 } } });
            boss.Svc.SaveTeam(edit, "ボス");
            string teamRaw = File.ReadAllText(TeamService.TeamFile(folder));
            Check(!teamRaw.Contains("Claude Max") && teamRaw.Contains("privateData"), "契約プラン・メモは team.json に暗号化して保存（メンバーには読めない）");
            Check(alice.Svc.LoadTeam().Members.First(m => m.Name == "アリス").Plans.Count == 0 && boss.Svc.Team.Members.First(m => m.Name == "アリス").MonthlyFeeJpy == 30000, "プラン月額はマスターだけが読める");
            alice.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Scope == ShareScope.Prompts && ra.Prompts.Any(p => Json.Str(p, "text") == "顧客A社の見積もりを作って") && ra.Sessions.Any(s => Json.Str(s, "title") == "見積もりの作成"), "閲覧範囲「依頼文まで」＋本人の同意で、依頼文とセッションが見える");
            Check(boss.Svc.Team.MemberFor(ra.AllEmails) != null && boss.Svc.Team.MemberFor(ra.AllEmails).Name == "アリス", "メールアドレス（大文字小文字を区別しない）で報告とメンバーを結び付ける");

            alice.Svc.UpdateConsent(ShareScope.Summary);
            Check(!File.Exists(reportPath), "同意の範囲を狭めると、共有フォルダの報告をすぐに削除する");
            alice.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Scope == ShareScope.Summary && ra.Prompts.Count == 0 && ra.Sessions.Count > 0 && alice.Svc.NeedsConsent(alice.Svc.Team), "本人の同意が「作業の概要まで」なら、マスターの設定が広くても依頼文は送らない");
            alice.Svc.UpdateConsent(ShareScope.Prompts);
            alice.Store.Policy = new Policy { HidePromptText = true };
            Check(alice.Svc.EffectiveScope(alice.Svc.Team) == ShareScope.Summary, "組織ポリシー HidePromptText で依頼文の共有を止める");
            alice.Store.Policy = new Policy { TeamMaxScope = ShareScope.Usage };
            Check(alice.Svc.EffectiveScope(alice.Svc.Team) == ShareScope.Usage, "組織ポリシー TeamMaxScope で共有範囲の上限を決める");
            alice.Store.Policy = new Policy { DisableTeamSharing = true };
            Check(alice.Svc.Upload(true) != null && !alice.Svc.Allowed && !File.Exists(reportPath), "組織ポリシー DisableTeamSharing で報告せず、置いてある報告も削除する");
            alice.Store.Policy = new Policy();
            alice.Svc.Upload(true);

            Console.WriteLine("== チーム：メールアドレスの変更・あいまいな一致");
            alice.Svc.Local.DeclaredEmail = "alice.new@example.com";
            alice.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error == null && ra.EmailsChanged && !ra.Approved, "承認後にメールアドレスが変わった報告は、承認待ちに戻す");
            alice.Svc.Local.DeclaredEmail = "alice@example.com";
            alice.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error == null && !ra.EmailsChanged && ra.Approved, "承認時のメールアドレスに戻ると承認済みに戻る");
            {
                var tc = boss.Svc.Team.Clone();
                bool amb;
                var hit = tc.ResolveMember(new[] { "boss@example.com" }, "alice@example.com", out amb);
                Check(hit == null && amb, "検出メールと申告メールが別のメンバーに一致する報告は、どちらにも集計しない");
                hit = tc.ResolveMember(new[] { "ALICE@example.com" }, "alice@example.com", out amb);
                Check(hit != null && hit.Name == "アリス" && !amb, "同じメンバーに一致する場合は集計する");
                var before = tc.Clone();
                tc.Members.First(m => m.Name == "ボス").IsMaster = false;
                Check(TeamManagerForm.LosesMaster(before, tc), "管理画面：マスターを外す変更を検出し、鍵の更新なしでは保存しない");
            }

            Console.WriteLine("== チーム：報告の差し戻し・未来の日時");
            {
                string snapshot = File.ReadAllText(reportPath);
                System.Threading.Thread.Sleep(20);
                alice.Svc.Upload(true);
                boss.Svc.ReadReports();
                File.WriteAllText(reportPath, snapshot);
                ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
                Check(ra.Error != null && ra.Error.Contains("古い報告"), "以前の正しい報告に差し戻すと検知する", ra.Error);
                ForgeReport(alice, DateTime.UtcNow.AddDays(2));
                ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
                Check(ra.Error != null && ra.Error.Contains("未来"), "作成日時が未来の報告は受け付けない（以後の報告を締め出せない）", ra.Error);
                alice.Svc.Upload(true);
                ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
                Check(ra.Error == null && ra.Approved, "本物のPCが報告し直すと正常");
            }

            Console.WriteLine("== チーム：改ざん・なりすまし");
            string teamFile = TeamService.TeamFile(folder);
            string good = File.ReadAllText(teamFile);
            // 一般ユーザーが team.json を書き換えて自分をマスターにする
            File.WriteAllText(teamFile, good.Replace("\"role\": \"general\"", "\"role\": \"master\""));
            Check(Throws(() => alice.Svc.LoadTeam(), "署名"), "署名のない書き換え（権限の昇格）は拒否");
            // 攻撃者が自分の鍵で作り直したチーム設定に置き換える
            var fake = TeamConfig.FromJson(Json.Parse(good));
            using (var attackerKey = TeamCrypto.NewKey(TeamCrypto.TeamKeyBits))
            {
                fake.PublicKey = TeamCrypto.ExportPublic(attackerKey);
                fake.Version += 10;
                var doc = fake.ToJson();
                doc["signature"] = TeamCrypto.Sign(attackerKey, doc);
                File.WriteAllText(teamFile, Json.Serialize(doc));
            }
            Check(Throws(() => alice.Svc.LoadTeam(), "鍵が変わっています"), "鍵をすり替えたチーム設定は拒否（報告を盗めない）");
            Check(alice.Svc.Upload(true) != null, "すり替えを検知している間は報告しない");
            File.WriteAllText(teamFile, good);
            Check(alice.Svc.TryRefreshTeam(), "元に戻せば正常");
            // 古い版への巻き戻し
            var older = TeamConfig.FromJson(Json.Parse(good));
            edit = boss.Svc.Team.Clone();
            edit.DefaultScope = ShareScope.Summary;
            boss.Svc.SaveTeam(edit, "ボス");
            string v3 = File.ReadAllText(teamFile);
            alice.Svc.LoadTeam();
            File.WriteAllText(teamFile, good);
            Check(Throws(() => alice.Svc.LoadTeam(), "古い版"), "古い版への巻き戻しは拒否");
            File.WriteAllText(teamFile, v3);
            Check(alice.Svc.TryRefreshTeam(), "最新の正しい版に戻せば正常");

            // 報告の改ざん
            alice.Svc.Upload(true);
            string rep = File.ReadAllText(reportPath);
            var repDoc = Json.Parse(rep) as Dictionary<string, object>;
            var payload = Json.Obj(repDoc, "payload");
            var ct = Convert.FromBase64String(Json.Str(payload, "ciphertext"));
            ct[ct.Length / 2] ^= 0x55;
            payload["ciphertext"] = Convert.ToBase64String(ct);
            File.WriteAllText(reportPath, Json.Serialize(repDoc));
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error != null && ra.Error.Contains("署名"), "報告の中身を書き換えると署名エラー", ra.Error);
            // 別のPCが同じ installId でなりすます（署名は正しいが鍵が違う）
            var mallory = NewInstall(null);
            mallory.Svc.Join(shared, peek.Fingerprint, "アリス", "alice@example.com", ShareScope.Usage);
            mallory.Svc.Local.InstallId = alice.Svc.Local.InstallId;
            mallory.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error == null && ra.KeyChanged && !ra.Approved, "同じPCを名乗る別の鍵の報告は「鍵が変わった」として集計に含めない");
            alice.Svc.Upload(true);
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error == null && !ra.KeyChanged && ra.Approved, "本物のPCが報告し直すと元に戻る");
            // 新しいPCが同じメールアドレスを名乗る
            var mallory2 = NewInstall(null);
            mallory2.Svc.Join(shared, peek.Fingerprint, "アリス", "alice@example.com", ShareScope.Usage);
            mallory2.Svc.Upload(true);
            var rm = boss.Svc.ReadReports().First(r => r.InstallId == mallory2.Svc.Local.InstallId);
            Check(!rm.Approved, "同じメールアドレスを名乗る新しいPCは、マスターが承認するまで集計しない");
            boss.Svc.DeleteReport(rm);
            Check(!File.Exists(rm.FilePath), "マスターは不審な報告を削除できる");

            Console.WriteLine("== チーム：権限の変更（マスターの追加・解除・鍵の更新）");
            edit = boss.Svc.Team.Clone();
            edit.Members.First(m => m.Name == "アリス").IsMaster = true;
            boss.Svc.SaveTeam(edit, "ボス");
            alice.Svc.LoadTeam();
            Check(!alice.Svc.IsMaster, "マスターに指定されても、パスフレーズで有効にするまではマスターではない");
            Check(Throws(() => alice.Svc.UnlockMaster("wrong passphrase!!"), "パスフレーズが違います"), "違うパスフレーズでは有効にできない");
            alice.Svc.UnlockMaster(pass);
            Check(alice.Svc.IsMaster && alice.Svc.ReadReports().Count > 0, "正しいパスフレーズでマスター権限が有効になり、報告を読める");


            var stale = boss.Svc.Team.Clone();
            edit = alice.Svc.Team.Clone();
            edit.Members.First(m => m.Name == "アリス").IsMaster = false;
            alice.Svc.SaveTeam(edit, "アリス");
            Check(Throws(() => boss.Svc.SaveTeam(stale, "ボス"), "他のマスター"), "他のマスターと同時に編集した場合は上書きしない");
            boss.Svc.LoadTeam();
            Check(!alice.Svc.IsMaster && !alice.Svc.Local.HasMasterKey, "マスターから外すと、そのPCに保存したチーム秘密鍵も消す");
            Check(Throws(() => mallory.Svc.UnlockMaster(pass), "マスターに指定されていません"), "マスターに指定されていない人は、パスフレーズを知っていても有効にできない");
            edit = boss.Svc.Team.Clone();
            edit.Members.First(m => m.Name == "ボス").IsMaster = false;
            Check(Throws(() => boss.Svc.SaveTeam(edit, "ボス"), "マスターを少なくとも1人"), "マスターが1人もいなくなる変更は拒否");
            edit = boss.Svc.Team.Clone();
            edit.Members.Add(new MemberEntry { Id = "dup", Name = "別人", Emails = { "alice@example.com" } });
            Check(Throws(() => boss.Svc.SaveTeam(edit, "ボス"), "複数のメンバー"), "同じメールアドレスを複数のメンバーに登録しない");

            const string pass2 = "another long passphrase 2";
            string oldFingerprint = boss.Svc.Team.Fingerprint;
            boss.Svc.RotateKey(pass2, "ボス");
            Check(boss.Svc.Team.Fingerprint != oldFingerprint && boss.Svc.Team.KeyGeneration == 2 && boss.Svc.IsMaster, "鍵を更新できる（マスターは新しい鍵で継続）");
            Check(alice.Svc.TryRefreshTeam() && alice.Svc.Local.PinnedGeneration == 2 && !alice.Svc.Local.HasMasterKey, "メンバーのPCは、前の鍵の署名を確認して新しい鍵に切り替える（古い鍵は破棄）");
            Check(alice.Svc.Upload(true) == null, "新しい鍵で報告できる");
            ra = boss.Svc.ReadReports().First(r => r.InstallId == alice.Svc.Local.InstallId);
            Check(ra.Error == null, "マスターは新しい鍵で報告を開ける", ra.Error);
            Check(Throws(() => { var x = NewInstall(null); x.Svc.Join(shared, boss.Svc.Team.Fingerprint, "b", "boss@example.com", ShareScope.Usage); x.Svc.UnlockMaster(pass); }, "パスフレーズが違います"), "古いパスフレーズでは新しい鍵を開けない");

            // 鍵の更新を続けて2回（メンバーが間の世代を見ていなくても、つながりを確認して追従する）
            const string pass3 = "third long passphrase 333";
            const string pass4 = "fourth long passphrase 44";
            edit = boss.Svc.Team.Clone();
            edit.Members.First(m => m.Name == "アリス").IsMaster = true;
            boss.Svc.SaveTeam(edit, "ボス");
            alice.Svc.LoadTeam();
            alice.Svc.UnlockMaster(pass2);
            Check(alice.Svc.IsMaster, "新しいパスフレーズでマスター権限を有効にできる");
            var demote = boss.Svc.Team.Clone();
            demote.Members.First(m => m.Name == "アリス").IsMaster = false;
            boss.Svc.RotateKey(pass3, "ボス", demote);
            boss.Svc.RotateKey(pass4, "ボス");
            Check(boss.Svc.Team.KeyGeneration == 4 && boss.Svc.Team.Members.First(m => m.Name == "アリス").Plans.Count == 1, "マスターの解除と鍵の更新を同時に行える（プランは引き継ぐ）");
            Check(alice.Svc.TryRefreshTeam() && alice.Svc.Local.PinnedGeneration == 4 && !alice.Svc.IsMaster && !alice.Svc.Local.HasMasterKey, "2世代先の鍵にも、更新の署名をたどって切り替える（外されたマスターの鍵は消す）", alice.Svc.TeamError);
            Check(Throws(() => alice.Svc.UnlockMaster(pass2)), "外されたマスターは、以前のパスフレーズで権限を戻せない");
            {
                string goodTeam = File.ReadAllText(teamFile);
                var forged = TeamConfig.FromJson(Json.Parse(goodTeam));
                using (var attackerKey = TeamCrypto.NewKey(TeamCrypto.TeamKeyBits))
                {
                    forged.KeyGeneration += 1;
                    forged.Version += 1;
                    forged.PublicKey = TeamCrypto.ExportPublic(attackerKey);
                    forged.Rotations.Add(new RotationStep { Generation = forged.KeyGeneration, PublicKey = forged.PublicKey, Proof = TeamCrypto.SignBytes(attackerKey, Encoding.UTF8.GetBytes("x")) });
                    var doc = forged.ToJson();
                    doc["signature"] = TeamCrypto.Sign(attackerKey, doc);
                    File.WriteAllText(teamFile, Json.Serialize(doc));
                }
                Check(Throws(() => alice.Svc.LoadTeam(), "鍵の更新を確認できません"), "前の鍵の署名がない「鍵の更新」は拒否");
                File.WriteAllText(teamFile, goodTeam);
                Check(alice.Svc.TryRefreshTeam(), "元に戻せば正常");
            }

            Console.WriteLine("== チーム：鍵ファイル・鍵の形式");
            {
                string kf = TeamService.KeyFile(folder);
                string goodKey = File.ReadAllText(kf);
                var kd = Json.Parse(goodKey) as Dictionary<string, object>;
                kd["iterations"] = 1000L;
                File.WriteAllText(kf, Json.Serialize(kd));
                Check(Throws(() => boss.Svc.UnlockMaster(pass4), "形式"), "反復回数を書き換えた鍵ファイルは受け付けない");
                kd = Json.Parse(goodKey) as Dictionary<string, object>;
                kd["purpose"] = "aiub-team-key:other:1";
                File.WriteAllText(kf, Json.Serialize(kd));
                Check(Throws(() => boss.Svc.UnlockMaster(pass4), "形式"), "別のチーム・別の世代の鍵ファイルは受け付けない");
                File.WriteAllText(kf, goodKey);
                boss.Svc.UnlockMaster(pass4);
                Check(boss.Svc.IsMaster, "正しい鍵ファイルなら有効にできる");
                using (var k = TeamCrypto.NewKey(3072))
                {
                    var pub = TeamCrypto.ExportPublic(k);
                    pub["e"] = TeamCrypto.B64(new byte[] { 3 });
                    Check(Throws(() => TeamCrypto.ImportPublic(pub), "鍵の形式"), "公開指数が 65537 以外の鍵は受け付けない");
                }
                using (var k = TeamCrypto.NewKey(2048))
                    Check(Throws(() => TeamCrypto.ImportPublic(TeamCrypto.ExportPublic(k), TeamCrypto.TeamKeyBits), "鍵の長さ"), "チーム鍵は3072bit未満を受け付けない");
            }

            Console.WriteLine("== チーム：表示・書き出しの無害化");
            Check(Hardening.CsvCell("＝1+1").StartsWith("\"'") && Hardening.CsvCell(" =cmd").StartsWith("\"'") && Hardening.CsvCell("＠x").StartsWith("\"'"), "CSV：全角の＝＠や先頭の空白付きの式も無害化");
            Check(Hardening.SafeLine("abc\u202Edef\u200B\nxyz", 80) == "abcdef xyz", "他のPCから届いた文字列の方向制御・ゼロ幅・改行を除く");
            Check(Hardening.SafeLine(new string('あ', 500), 80).Length == 81, "長すぎる文字列は切り詰める");

            Console.WriteLine("== チーム：参加情報ファイルの破損");
            {
                var z = NewInstall(null);
                z.Svc.Join(shared, boss.Svc.Team.Fingerprint, "z", "z@example.com", ShareScope.Usage);
                File.WriteAllText(TeamLocal.PathIn(z.Paths.DataDir), "{ broken");
                var z2 = new TeamService(z.Store, z.Activity);
                Check(z2.Local.Corrupt && !z2.Local.Joined && z2.TeamError != null, "壊れた参加情報は黙って初期化せず、エラーとして表示する");
                Check(z2.Upload(true) != null, "壊れている間は報告しない");
                z2.Leave();
                Check(Directory.GetFiles(z.Paths.DataDir, "team-local.json.corrupt-*").Length == 1 && !new TeamService(z.Store, z.Activity).Local.Corrupt, "保存し直すと、壊れたファイルは退避して新しく作る");
            }

            Console.WriteLine("== チーム：メールアドレスで追加・自動集計・招待コード");
            {
                var errs = new List<string>();
                var parsed = TeamConfig.ParseMemberList("山田 太郎 <Yamada@Example.co.jp>\r\n佐藤, sato@example.com\n\nfoo@bar.com\nメールなし\nsato@example.com", errs);
                Check(parsed.Count == 3 && parsed[0].Name == "山田 太郎" && parsed[0].Emails[0] == "yamada@example.co.jp" && parsed[1].Name == "佐藤" && parsed[2].Name == "foo" && errs.Count == 1 && errs[0].StartsWith("5行目"),
                    "メールアドレスの一覧を貼り付けて読み取る（名前の形式・重複・エラー行）", string.Join("/", parsed.Select(x => x.Name + ":" + x.Emails[0])) + " " + string.Join("/", errs));

                string msg = TeamInvite.Message(boss.Svc.Team, folder);
                string invFolder, invTeam, invFp, brokenFolder, brokenTeam, brokenFp;
                string mailto = TeamInvite.MailtoUrl(new[] { "Carol@Example.com", "bad address" }, boss.Svc.Team, @"\\fileserver\share & co\AIUsageBar-Team");
                string mbody = Uri.UnescapeDataString(mailto.Substring(mailto.IndexOf("&body=") + 6));
                string mf, mt, mp;
                Check(mailto.StartsWith("mailto:carol%40example.com?subject=") && mailto.Split('&').Length == 2 && mailto.Length < 2000 && TeamInvite.TryDecode(mbody, out mf, out mt, out mp) && mf.Contains("share & co"),
                    "招待メール（mailto）：宛先・件名・本文を安全に組み立て、本文を貼り付ければ参加でき、メールアプリが扱える長さ", mailto.Length.ToString());
                string link = TeamInvite.LinkUrl(boss.Svc.Team, folder);
                string lf, lt, lp;
                Check(link.StartsWith("aiusagebar:join?c=") && TeamInvite.TryDecodeLink(link, out lf, out lt, out lp) && lf == folder && lp == boss.Svc.Team.Fingerprint,
                    "招待リンク（aiusagebar:）を組み立て・解読できる（クリックで参加画面が開く）");
                string gmailUrl = TeamInvite.GmailUrl(new[] { "carol@example.com" }, boss.Svc.Team, folder);
                Check(gmailUrl.StartsWith("https://mail.google.com/mail/?view=cm&fs=1&to=carol%40example.com&su=") && gmailUrl.Split('&').Length == 5, "招待メール（Gmail の作成画面）の URL");
                Check(TeamInvite.TryDecode("こんにちは\n" + msg + "\nよろしく", out invFolder, out invTeam, out invFp) && invFolder == folder && invFp == boss.Svc.Team.Fingerprint && invTeam == boss.Svc.Team.TeamName,
                    "招待文の中から招待コードを読み取る（共有フォルダ・チーム名・確認コード）");
                Check(!msg.Contains(pass4) && !msg.Contains("\"d\"") && !TeamInvite.TryDecode("AIUB-INVITE-1:壊れたコード", out brokenFolder, out brokenTeam, out brokenFp), "招待文にパスフレーズ・秘密鍵を含めない。壊れたコードは読まない");

                var skipped = new List<string>();
                int added = boss.Svc.AddMembers(TeamConfig.ParseMemberList("キャロル <carol@example.com>\nfrank@example.com", null), ShareScope.Usage, "ボス", skipped);
                Check(added == 2 && boss.Svc.Team.Members.Any(m => m.Name == "キャロル" && !m.IsMaster && m.Scope == ShareScope.Usage), "マスターはメールアドレスで一般ユーザーを追加できる");
                Check(boss.Svc.AddMembers(TeamConfig.ParseMemberList("carol@example.com", null), ShareScope.Usage, "ボス", skipped) == 0 && skipped.Contains("carol@example.com"), "登録済みのメールアドレスは二重に追加しない");
                Check(Throws(() => alice.Svc.AddMembers(TeamConfig.ParseMemberList("x@example.com", null), ShareScope.Usage, "アリス", null), "マスターだけ"), "一般ユーザーはメンバーを追加できない");

                Func<string, Install> withLogin = email => NewInstall(p =>
                {
                    File.WriteAllText(Path.Combine(p.Home, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"" + email + "\"}}");
                    string sid2 = Guid.NewGuid().ToString();
                    WriteLines(Path.Combine(p.ClaudeProjects, "p", sid2 + ".jsonl"), ClaudeAssistant(sid2, "m" + sid2, "r" + sid2, DateTime.UtcNow.AddMinutes(-20), 100, 200, 300, 400));
                });
                var carol = withLogin("Carol@Example.com");
                carol.Svc.Join(invFolder, invFp, "キャロル", "carol@example.com", ShareScope.Usage);
                Check(carol.Svc.Upload(true) == null, "招待コードの内容で参加して報告できる");
                var rc = boss.Svc.ReadReports().First(r => r.InstallId == carol.Svc.Local.InstallId);
                Check(rc.Error == null && rc.Approved && rc.AutoApproved, "登録したメールで Claude にログインしているPCは、承認なしで集計される", rc.Error);

                var dave = NewInstall(null);   // ログインなし。メールを入力しただけ
                dave.Svc.Join(invFolder, invFp, "キャロル？", "carol@example.com", ShareScope.Usage);
                dave.Svc.Upload(true);
                var rd = boss.Svc.ReadReports().First(r => r.InstallId == dave.Svc.Local.InstallId);
                Check(rd.Error == null && !rd.Approved, "メールアドレスを入力しただけ（ログインしていない）のPCは自動で集計しない");

                var erin = withLogin("carol@example.com");   // ログインはキャロル、申告は別のメンバー
                erin.Svc.Join(invFolder, invFp, "?", "boss@example.com", ShareScope.Usage);
                erin.Svc.Upload(true);
                var re = boss.Svc.ReadReports().First(r => r.InstallId == erin.Svc.Local.InstallId);
                Check(re.Error == null && !re.Approved, "ログインと申告が別々のメンバーを指すPCは自動で集計しない");

                boss.Svc.SetApproval(rc, false);
                carol.Svc.Upload(true);
                rc = boss.Svc.ReadReports().First(r => r.InstallId == carol.Svc.Local.InstallId);
                Check(!rc.Approved, "マスターが承認を取り消したPCは、ログインが一致しても自動で戻さない");

                var tcfg = boss.Svc.Team.Clone();
                tcfg.AutoApproveByLogin = false;
                boss.Svc.SaveTeam(tcfg, "ボス");
                Check(!TeamConfig.FromJson(Json.Parse(File.ReadAllText(TeamService.TeamFile(folder)))).AutoApproveByLogin, "自動集計の設定はチーム設定（署名付き）に保存");
                var frank = withLogin("frank@example.com");
                frank.Svc.Join(invFolder, invFp, "フランク", "frank@example.com", ShareScope.Usage);
                frank.Svc.Upload(true);
                var rf = boss.Svc.ReadReports().First(r => r.InstallId == frank.Svc.Local.InstallId);
                Check(rf.Error == null && !rf.Approved, "自動集計をオフにすると、ログインが一致しても承認待ちになる");
                tcfg = boss.Svc.Team.Clone();
                tcfg.AutoApproveByLogin = true;
                boss.Svc.SaveTeam(tcfg, "ボス");
                rf = boss.Svc.ReadReports().First(r => r.InstallId == frank.Svc.Local.InstallId);
                Check(rf.Approved && rf.AutoApproved, "オンに戻すと自動で集計される");
                foreach (var x in new[] { carol, dave, erin, frank }) x.Svc.Leave();
            }

            Console.WriteLine("== メール送信の設定（DPAPI 保存・本文組み立て）");
            {
                var mpaths = FakePaths(Tmp());
                Hardening.SecureDirectory(mpaths.DataDir);
                var mcfg = new MailConfig();
                MailConfig.ApplyPreset(mcfg, "boss@gmail.com");
                Check(mcfg.Host == "smtp.gmail.com" && mcfg.Port == 587 && mcfg.UseSsl && mcfg.Username == "boss@gmail.com", "メールアドレスから送信サーバーを補完（Gmail）");
                var ocfg = new MailConfig(); MailConfig.ApplyPreset(ocfg, "u@outlook.com");
                Check(ocfg.Host == "smtp-mail.outlook.com", "Outlook の送信サーバーを補完");
                mcfg.FromEmail = "boss@gmail.com";
                Check(!mcfg.IsComplete, "パスワード未設定では送信不可");
                mcfg.SetPassword("abcd efgh ijkl mnop");
                Check(mcfg.IsComplete && mcfg.HasPassword, "アプリパスワードを設定すると送信可能な状態になる");
                mcfg.Save(mpaths);
                string raw = File.ReadAllText(Path.Combine(mpaths.DataDir, "mail.json"));
                Check(!raw.Contains("abcd efgh") && raw.Contains("password"), "パスワードは平文で保存しない（DPAPI）");
                var loaded = MailConfig.Load(mpaths);
                Check(loaded.IsComplete && loaded.Host == "smtp.gmail.com" && loaded.FromEmail == "boss@gmail.com", "保存した設定を読み込める（パスワードも復元）");
                bool sendRejected = false; try { mcfg.Send(new string[0], "件名", "本文"); } catch (MailException) { sendRejected = true; }
                Check(sendRejected, "宛先が無ければ送信しない（ネットワークに接続しない）");
                // 実際の送信は行わない（ネットワークに接続しないことの担保）。設定と本文の組み立てのみ検証。
                string invite = TeamInvite.Message(boss.Svc.Team, folder);
                Check(invite.Contains("AIUB-INVITE-1:") && !invite.Contains(pass) && !invite.Contains(pass2), "招待メール本文に招待コードを含み、パスフレーズは含まない");
            }

            Console.WriteLine("== AI提案（設定・要約・応答解析。ネットワークには接続しない）");
            {
                var apaths = FakePaths(Tmp());
                Hardening.SecureDirectory(apaths.DataDir);
                var acfg = new AiConfig();
                Check(!acfg.IsComplete && acfg.Model == "claude-opus-5", "既定はモデル opus-5・キー未設定で使用不可");
                acfg.SetKey("sk-ant-test-XYZ");
                acfg.Model = "claude-sonnet-5";
                acfg.Save(apaths);
                string araw = File.ReadAllText(Path.Combine(apaths.DataDir, "ai.json"));
                Check(!araw.Contains("sk-ant-test-XYZ") && araw.Contains("apiKey"), "APIキーは平文で保存しない（DPAPI）");
                var aloaded = AiConfig.Load(apaths);
                Check(aloaded.IsComplete && aloaded.Model == "claude-sonnet-5" && aloaded.GetKey() == "sk-ant-test-XYZ", "保存した設定を読み込める（キーも復元）");

                var erows = new List<ExecRow> {
                    new ExecRow { Name = "山田", IsMember = true, Claude = 5000000, Codex = 0, Output = 200000, Jpy = 1200, ActiveDays = 6, Prompts = 40, UsesClaude = true, UsesCodex = false, Models = 1 },
                    new ExecRow { Name = "佐藤", IsMember = true, Claude = 0, Codex = 0, ActiveDays = 0, Prompts = 0 },
                };
                var adaily = new List<DailyPoint> { new DailyPoint { Date = "2026-09-17", Claude = 5000000, Codex = 0 } };
                string summary = AiClient.BuildSummary("開発部", "今月", erows, adaily, 2, new[] { "未活用：佐藤" });
                Check(summary.Contains("山田") && summary.Contains("平均AI活用度スコア") && !summary.Contains("顧客A社の見積もり") && !summary.Contains("依頼文") ,
                    "要約は数値・傾向のみで、依頼文の本文を含まない");

                Check(AiClient.ParseText("{\"content\":[{\"type\":\"text\",\"text\":\"提案です\"}],\"stop_reason\":\"end_turn\"}") == "提案です", "応答の text ブロックを取り出す");
                Check(Throws2<AiException>(() => AiClient.ParseText("{\"content\":[],\"stop_reason\":\"refusal\"}")), "拒否（refusal）は分かりやすいエラーにする");
                Check(AiClient.ParseOpenAiText("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"提案です(OpenAI互換)\"}}]}") == "提案です(OpenAI互換)", "OpenAI互換の応答（choices[0].message.content）を取り出す");
                Check(Throws2<AiException>(() => AiClient.ParseOpenAiText("{\"error\":{\"message\":\"invalid api key\"}}")), "OpenAI互換のエラー応答もエラーにする");
                { var oc = new AiConfig(); oc.Provider = "openai"; oc.SetKey("fish_test"); oc.Model = "fugu"; Check(!oc.IsComplete && (oc.BaseUrl = "https://api.sakana.ai/v1") != null && oc.IsComplete, "OpenAI互換はエンドポイント未設定だと使用不可・設定すると可能"); }
                Check(Throws2<AiException>(() => AiClient.ParseText("{\"type\":\"error\",\"error\":{\"message\":\"invalid x-api-key\"}}")), "APIエラー応答はエラーにする");
                alice.Store.Policy = new Policy { DisableAiSuggest = true };
                Check(alice.Store.Policy.DisableAiSuggest && alice.Store.Policy.AnyEnforced, "組織ポリシー DisableAiSuggest を読み取れる");
                alice.Store.Policy = new Policy();
            }

            Console.WriteLine("== 定期レポートのスケジュール判定");
            {
                var spaths = FakePaths(Tmp());
                Hardening.SecureDirectory(spaths.DataDir);
                var sc = new AiSchedule { Enabled = false };
                Check(!sc.IsDue(new DateTime(2026, 9, 21, 10, 0, 0)), "無効のときは実行しない");
                sc.Enabled = true; sc.Frequency = 0; sc.Hour = 9;   // 毎日9時
                Check(!sc.IsDue(new DateTime(2026, 9, 21, 8, 0, 0)), "指定時刻より前は実行しない");
                Check(sc.IsDue(new DateTime(2026, 9, 21, 9, 30, 0)), "毎日：時刻を過ぎたら実行する");
                sc.MarkRan(spaths, new DateTime(2026, 9, 21, 9, 30, 0));
                Check(!sc.IsDue(new DateTime(2026, 9, 21, 18, 0, 0)), "同じ日には二度実行しない");
                Check(sc.IsDue(new DateTime(2026, 9, 22, 9, 30, 0)), "翌日はまた実行する");
                var loaded = AiSchedule.Load(spaths);
                Check(loaded.Enabled && loaded.Frequency == 0 && loaded.LastRunDate == "2026-09-21", "設定と最終実行日を保存・復元できる");
                sc.Frequency = 1; sc.Weekday = 1; sc.LastRunDate = null;   // 毎週月曜
                Check(sc.IsDue(new DateTime(2026, 9, 21, 9, 30, 0)) && !sc.IsDue(new DateTime(2026, 9, 22, 9, 30, 0)), "毎週：指定曜日だけ実行（2026-09-21は月曜）");
                sc.Frequency = 2; sc.DayOfMonth = 31; sc.LastRunDate = null;   // 毎月31日→2月等は月末
                Check(sc.IsDue(new DateTime(2026, 2, 28, 9, 30, 0)), "毎月：月末に丸める（2月は28日）");
                Check(sc.Describe().Contains("毎月") && sc.Describe().Contains("対象"), "設定の説明文を作れる");
                var mc = new AiSchedule { Enabled = true, Frequency = 0, Hour = 14, Minute = 30 };
                Check(!mc.IsDue(new DateTime(2026, 9, 21, 14, 15, 0)), "分：時が同じでも分が前なら実行しない");
                Check(mc.IsDue(new DateTime(2026, 9, 21, 14, 45, 0)), "分：時分を過ぎたら実行する");
                mc.Save(spaths); Check(AiSchedule.Load(spaths).Minute == 30, "分を保存・復元できる");
                Check(mc.Describe().Contains("14:30"), "説明文に時:分が入る");
            }

            Console.WriteLine("== 顧客別スタートライン（基準値）");
            {
                var bpaths = FakePaths(Tmp());
                Hardening.SecureDirectory(bpaths.DataDir);
                var b = new TeamAnalytics.TeamBaseline();
                Check(!b.Enabled && b.StageIndex == 2, "既定はオフ・一部活用");
                b.ApplyStage(0); Check(b.TargetScore == 20 && b.TargetAdoptionPct == 30, "段階を選ぶと目安値が入る（導入前）");
                b.ApplyStage(3); Check(b.TargetScore == 65 && b.TargetAdoptionPct == 90, "段階を選ぶと目安値が入る（全社活用）");
                b.Enabled = true; b.StageIndex = 2; b.TargetScore = 55; b.TargetAdoptionPct = 60; b.Save(bpaths);
                var lb = TeamAnalytics.TeamBaseline.Load(bpaths);
                Check(lb.Enabled && lb.StageIndex == 2 && lb.TargetScore == 55 && lb.TargetAdoptionPct == 60, "設定を保存・復元できる");
                Check(lb.Describe().Contains("一部活用") && lb.Describe().Contains("55") && lb.Describe().Contains("60%"), "説明文に段階と目標が入る");
                // 目標未達のときは差分を提案、達成のときは次段階を提案
                var lowRows = new List<ExecRow> {
                    new ExecRow { Name = "A", IsMember = true, Claude = 100000, ActiveDays = 1, Prompts = 1, UsesClaude = true },
                    new ExecRow { Name = "B", IsMember = true }, new ExecRow { Name = "C", IsMember = true } };
                var insLow = TeamAnalytics.Insights(lowRows, 3, lb);
                Check(insLow.Any(i => i.Text.Contains("スタートライン")) && insLow.Any(i => i.Text.Contains("目標まで")), "未達：目標との差分を出す");
                var hiRows = new List<ExecRow> {
                    new ExecRow { Name = "A", IsMember = true, Claude = 5000000, ActiveDays = 20, Prompts = 100, Sessions = 20, Models = 3, UsesClaude = true, UsesCodex = true },
                    new ExecRow { Name = "B", IsMember = true, Claude = 4000000, ActiveDays = 18, Prompts = 90, Sessions = 18, Models = 2, UsesClaude = true, UsesCodex = true },
                    new ExecRow { Name = "C", IsMember = true, Claude = 3000000, ActiveDays = 15, Prompts = 60, Sessions = 12, Models = 2, UsesClaude = true, UsesCodex = true } };
                var insHi = TeamAnalytics.Insights(hiRows, 3, lb);
                Check(insHi.Any(i => i.Text.Contains("目標を達成")), "達成：次の段階へを提案");
                // オフのときは基準の行を出さない
                var off = new TeamAnalytics.TeamBaseline { Enabled = false };
                Check(!TeamAnalytics.Insights(lowRows, 3, off).Any(i => i.Text.Contains("スタートライン")), "オフのときは基準を出さない");
                // サマリーに基準行が入る
                string bsum = AiClient.BuildSummary("開発部", "今月", hiRows, new List<DailyPoint>(), 3, new string[0], lb);
                Check(bsum.Contains("スタートライン") && bsum.Contains("目標"), "AI要約に基準を添える");
            }

            Console.WriteLine("== チーム：金額");
            var cfg = boss.Svc.Team;
            var day = new DailyUsage { Date = "2026-09-17", Tool = Tool.ClaudeCode, Model = "claude-opus-5" };
            day.Sum.Input = 1000000; day.Sum.Output = 1000000; day.Sum.CacheRead = 1000000; day.Sum.CacheWrite = 1000000; day.Sum.CacheWrite1h = 500000; day.Sum.Total = 4000000;
            double? usd = CostCalc.Usd(day, cfg);
            Check(usd != null && Math.Abs(usd.Value - 38.625) < 1e-9, "Claude の API 換算（入力5・出力25・読取0.5・書込5分6.25/1時間10ドル）", usd == null ? "null" : usd.Value.ToString());
            var codexDay = new DailyUsage { Date = "2026-09-17", Tool = Tool.Codex, Model = "gpt-5.6-sol" };
            codexDay.Sum.Input = 100; codexDay.Sum.Total = 100;
            Check(CostCalc.Usd(codexDay, cfg) == null, "単価が未設定のモデルは金額にしない（推測しない）");
            var total = CostCalc.Sum(new[] { day, codexDay }, cfg);
            Check(Math.Abs(total.Jpy - 38.625 * cfg.UsdJpy) < 1e-6 && total.UnpricedTokens == 100, "円換算と、単価未設定分の件数");
            Check(cfg.FindPrice("claude-fable-5-1").CacheRead == 0.25 && cfg.FindPrice("claude-fable-5").CacheRead == 1.0, "モデル名は最も長い一致を使う（Fable 5.1 と Fable 5 を区別）");
            Check(cfg.Members.First(m => m.Name == "アリス").MonthlyFeeJpy == 30000, "プラン月額の登録");

            Console.WriteLine("== チーム：退出");
            alice.Svc.Leave();
            Check(!alice.Svc.Local.Joined && !File.Exists(reportPath), "チームから抜けると報告を削除し、以後は報告しない");
            Check(alice.Svc.Upload(true) != null, "抜けた後は報告しない");

            Console.WriteLine("== 日別集計の長期保存");
            {
                var paths = FakePaths(Tmp());
                var t0 = DateTime.UtcNow.AddMinutes(-10);
                WriteLines(Path.Combine(paths.ClaudeProjects, "p", "s.jsonl"), ClaudeAssistant("s1", "m1", "r1", t0, 1, 2, 3, 4));
                var logs = new LocalLogs(paths);
                logs.Scan(null);
                logs.Save();
                string cachePath = Path.Combine(paths.DataDir, "local-logs-v4.json");
                var doc = Json.Parse(File.ReadAllText(cachePath)) as Dictionary<string, object>;
                var hist = (List<object>)doc["history"];
                string oldDate = DateTime.Now.Date.AddDays(-100).ToString("yyyy-MM-dd");
                hist.Add(new List<object> { oldDate, "claude", "claude-opus-5", 10L, 20L, 30L, 40L, 5L, 0L, 100L, 3L });
                File.WriteAllText(cachePath, Json.Serialize(doc));
                var logs2 = new LocalLogs(paths);
                logs2.Load();
                logs2.Scan(null);
                var h = logs2.History();
                Check(h.Any(x => x.Date == oldDate && x.Sum.Total == 100) && h.Any(x => x.Sum.Total == 10 && x.Model == "claude-opus-5"), "記録を消した後も、日別・モデル別の集計は残る（月単位の集計に使う）");
            }
        }
    }
}
