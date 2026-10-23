using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AIUsageBar
{
    /// <summary>チーム機能のダイアログ（標準の WinForms 部品で作る）</summary>
    public static class TeamUi
    {
        static Font font;
        public static Font UiFont { get { return font ?? (font = new Font(Theme.UiFontFamily, 9f)); } }

        // ---- モダン＆ミニマルの共通スタイル ----
        public static readonly Color ModAccent = Theme.Hex("#4F63D6");
        public static readonly Color ModBg = Theme.Hex("#F6F7FA");
        public static readonly Color ModInk = Theme.Hex("#20242B");
        public static readonly Color ModBorder = Theme.Hex("#D9DEE7");

        /// <summary>ボタンをフラットな今風の見た目にする（主ボタンはアクセント色）。</summary>
        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.Padding = new Padding(12, 4, 12, 4);
            int need = b.PreferredSize.Width + 8;   // 文字が切れないよう、テキスト幅を測ってから固定幅にする
            b.AutoSize = false;
            int h = Math.Max(b.Height, 30);
            b.MinimumSize = new Size(96, h);
            b.Size = new Size(Math.Max(96, need), h);
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = ModBorder;
            if (primary)
            {
                b.BackColor = ModAccent; b.ForeColor = Color.White;
                b.Font = new Font(b.Font, FontStyle.Bold);
                b.FlatAppearance.MouseOverBackColor = Theme.Mix(ModAccent, Color.White, 0.14);
                b.FlatAppearance.MouseDownBackColor = Theme.Mix(ModAccent, Color.Black, 0.10);
            }
            else
            {
                b.BackColor = Color.White; b.ForeColor = Theme.Hex("#333A45");
                b.FlatAppearance.MouseOverBackColor = Theme.Hex("#EEF1F7");
                b.FlatAppearance.MouseDownBackColor = Theme.Hex("#E4E8F0");
            }
        }

        /// <summary>フォーム全体を今風に整える（背景色＋ボタンのフラット化）。表示後に呼ぶ。</summary>
        public static void Modernize(Form f)
        {
            try { f.BackColor = ModBg; } catch { }
            StyleButtonsIn(f, f);
        }

        static void StyleButtonsIn(Form f, Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var b = c as Button;
                if (b != null) StyleButton(b, ReferenceEquals(b, f.AcceptButton));
                else if (c.HasChildren) StyleButtonsIn(f, c);
            }
        }

        /// <summary>表（DataGridView）を今風の見た目にする（罫線を控えめ・ヘッダーを淡色・行に余白）。</summary>
        public static void StyleGrid(DataGridView g)
        {
            g.EnableHeadersVisualStyles = false;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = Color.White;
            g.GridColor = Theme.Hex("#EDEFF3");
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.RowHeadersVisible = false;
            g.AllowUserToResizeRows = false;
            g.ColumnHeadersHeight = 34;
            g.RowTemplate.Height = 28;
            g.DefaultCellStyle.Padding = new Padding(6, 3, 6, 3);
            g.DefaultCellStyle.SelectionBackColor = Theme.Hex("#E8ECFB");
            g.DefaultCellStyle.SelectionForeColor = ModInk;
            g.DefaultCellStyle.BackColor = Color.White;
            g.AlternatingRowsDefaultCellStyle.BackColor = Theme.Hex("#FAFBFD");
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Hex("#F1F3F8");
            g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Hex("#5A6270");
            g.ColumnHeadersDefaultCellStyle.Font = new Font(UiFont, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        }

        public static Form NewDialog(string title, int width, int height)
        {
            var f = new Form
            {
                Text = title, Font = UiFont, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(width, height), TopMost = true,
            };
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            f.BackColor = ModBg;
            f.Shown += (s, e) => Modernize(f);
            return f;
        }

        public static Label Note(string text, int width)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 10) };
        }

        public static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(UiFont, FontStyle.Bold), Margin = new Padding(3, 8, 3, 4) };
        }

        public static TableLayoutPanel Grid(int columns)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, AutoScroll = true, Padding = new Padding(14) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 1; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / (columns - 1)));
            return t;
        }

        public static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            bool tall = (c is TextBox && ((TextBox)c).Multiline) || c is DataGridView;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = tall ? AnchorStyles.Left | AnchorStyles.Top : AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            if (c is Button) c.Anchor = AnchorStyles.Left;
            else if (tall) { c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; c.Margin = new Padding(3, 3, 3, 6); }
            else c.Dock = DockStyle.Fill;
            t.Controls.Add(c);
            if (t.ColumnCount > 2) t.SetColumnSpan(c, t.ColumnCount - 1);
        }

        public static void AddFull(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(c);
            t.SetColumnSpan(c, t.ColumnCount);
        }

        public static FlowLayoutPanel Buttons(Form f, string okText, Func<bool> onOk)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(10) };
            var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 28) };
            var ok = new Button { Text = okText, AutoSize = true, MinimumSize = new Size(96, 28) };
            ok.Click += (s, e) =>
            {
                try
                {
                    f.Cursor = Cursors.WaitCursor;
                    if (onOk()) { f.DialogResult = DialogResult.OK; f.Close(); }
                }
                catch (TeamException ex) { Error(f, ex.Message); }
                catch (Exception ex) { Error(f, "処理できませんでした：" + ex.Message); }
                finally { f.Cursor = Cursors.Default; }
            };
            p.Controls.Add(cancel);
            p.Controls.Add(ok);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.Controls.Add(p);
            return p;
        }

        public static void Error(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static void Info(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static TextBox FolderPicker(TableLayoutPanel t, string label, string initial)
        {
            var box = new TextBox { Text = initial ?? "" };
            var browse = new Button { Text = "参照…", AutoSize = true };
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            box.Dock = DockStyle.Fill;
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(browse, 1, 0);
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "チームで使う共有フォルダを選んでください（社内のファイルサーバーや OneDrive / SharePoint の同期フォルダなど）", ShowNewFolderButton = true })
                {
                    if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
                    if (dlg.ShowDialog(box.FindForm()) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            AddRow(t, label, row);
            return box;
        }

        // ------------------------------------------------------------------ チームの作成（マスター）

        public static bool CreateTeam(IWin32Window owner, TeamService svc)
        {
            using (var f = NewDialog("チームを作成（マスターになる）", 560, 520))
            {
                var t = Grid(2);
                AddFull(t, Note("このPCの利用者がマスターになります。マスターは、メンバーをメールアドレスで登録し、各メンバーの利用量・金額・（許可された範囲で）作業内容や依頼文を確認できます。メンバーの報告は共有フォルダに暗号化して保存され、マスター以外は読めません。", 520));
                var name = new TextBox { Text = "" };
                AddRow(t, "チーム名", name);
                var folder = FolderPicker(t, "共有フォルダ", "");
                AddFull(t, Note("選んだフォルダの中に「" + TeamService.FolderName + "」を作ります。メンバー全員が読み書きでき、社外からは見えない場所を選んでください。", 520));
                var me = new TextBox { Text = TeamService.DemoUser ?? Environment.UserName };
                AddRow(t, "あなたの名前", me);
                var email = new TextBox { Text = svc.MyEmails().FirstOrDefault() ?? "" };
                AddRow(t, "メールアドレス", email);
                var pass1 = new TextBox { UseSystemPasswordChar = true };
                AddRow(t, "パスフレーズ", pass1);
                var pass2 = new TextBox { UseSystemPasswordChar = true };
                AddRow(t, "パスフレーズ（確認）", pass2);
                AddFull(t, Note("パスフレーズ（" + TeamService.MinPassphraseLength + "文字以上）は、別のPCでマスター権限を有効にするときや、他の人をマスターにするときに使います。忘れると鍵を作り直すまで報告を読めなくなるので、安全な場所に保管してください。本アプリはパスフレーズを保存しません。", 520));
                f.Controls.Add(t);
                Buttons(f, "作成", () =>
                {
                    if (pass1.Text != pass2.Text) throw new TeamException("パスフレーズが一致しません");
                    svc.CreateTeam(folder.Text, name.Text, me.Text, email.Text, pass1.Text);
                    Info(f, "チームを作成しました。\n\nメンバーには、共有フォルダの場所と、次の「チーム確認コード」を伝えてください（参加時に入力してもらいます）。確認コードは、共有フォルダとは別の方法（対面・電話・社内チャットなど）で伝えてください。\n\nチーム確認コード：" + svc.Team.Fingerprint);
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        // ------------------------------------------------------------------ 参加（一般ユーザー）

        public static bool JoinTeam(IWin32Window owner, TeamService svc, string initialInvite = null)
        {
            using (var f = NewDialog("チームに参加（一般ユーザー）", 580, 740))
            {
                var t = Grid(2);
                AddFull(t, Note("マスターから届いた招待コード（「AIUB-INVITE-1:」で始まる文字列）を貼り付けてください。招待の文章をまるごと貼り付けても読み取れます。招待コードがない場合は、共有フォルダを選んでチーム確認コードを入力します。", 540));
                var invite = new TextBox { Multiline = true, Height = 44, ScrollBars = ScrollBars.Vertical };
                AddRow(t, "招待コード", invite);
                var folder = FolderPicker(t, "共有フォルダ", "");
                var load = new Button { Text = "読み込む", AutoSize = true, Anchor = AnchorStyles.Left };
                AddRow(t, "", load);
                var info = new Label { AutoSize = true, MaximumSize = new Size(540, 0), Text = "（まだ読み込んでいません）", Margin = new Padding(3, 6, 3, 10) };
                AddFull(t, info);
                var fp = new TextBox { Font = new Font(FontFamily.GenericMonospace, 9.5f) };
                AddRow(t, "チーム確認コード", fp);
                var fpNote = Note("マスターから（共有フォルダとは別の方法で）伝えられた32桁の確認コードを入力してください。区切りのハイフンは省略できます。共有フォルダのチームと一致しない場合は参加できません。", 540);
                AddFull(t, fpNote);
                var name = new TextBox { Text = TeamService.DemoUser ?? Environment.UserName };
                AddRow(t, "あなたの名前", name);
                var email = new TextBox { Text = svc.MyEmails().FirstOrDefault() ?? "" };
                AddRow(t, "メールアドレス", email);
                var desc = new Label { AutoSize = true, MaximumSize = new Size(540, 0), Margin = new Padding(3, 4, 3, 8), Text = "マスターに共有される内容：" + ScopeText.Describe(ShareScope.Prompts) };
                AddFull(t, desc);
                var agree = new CheckBox { Text = "上記のすべて（依頼文の本文を含む）をマスターに共有することに同意します", AutoSize = true, MaximumSize = new Size(540, 0), Font = new Font(UiFont, FontStyle.Bold) };
                AddFull(t, agree);
                AddFull(t, Note("同意しない場合は参加できません。共有は、このPCの Claude Code・Codex の記録と利用枠が対象です（ChatGPT・Claude の通常チャットは含みません）。報告は暗号化され、マスター以外は読めません。会社のポリシーで制限されている項目は共有されません。共有をやめるときは「チームから抜ける」を選ぶと、共有フォルダの報告を削除します。", 540));
                f.Controls.Add(t);

                TeamConfig peeked = null;
                Action doLoad = null;
                invite.TextChanged += (s, e) =>
                {
                    string inFolder, inTeam, inFp;
                    if (!TeamInvite.TryDecode(invite.Text, out inFolder, out inTeam, out inFp)) return;
                    fp.Text = inFp;
                    fp.ReadOnly = true;   // 招待コードから自動入力：手打ち不要
                    fpNote.Text = "✓ 招待コードからチーム確認コードを自動入力しました。より安全にするには、マスター本人にこの確認コードが一致するか確認してください。";
                    fpNote.ForeColor = ModAccent;
                    if (!folder.ReadOnly)
                    {
                        folder.Text = inFolder;
                        if (Directory.Exists(inFolder)) doLoad();
                        else info.Text = "招待コードを読み取りました（チーム：" + inTeam + "）。このPCでは共有フォルダ「" + inFolder + "」が見つかりません。OneDrive などで場所が違う場合は、このPCの同じフォルダを「参照…」で選んでください（自動で読み込みます）。";
                    }
                };
                // 共有フォルダが有効になったら、「読み込む」を押さなくても自動で読み込む
                folder.TextChanged += (s, e) =>
                {
                    if (!folder.ReadOnly && peeked == null && !string.IsNullOrWhiteSpace(folder.Text) && Directory.Exists(folder.Text)) doLoad();
                };
                doLoad = () =>
                {
                    try
                    {
                        string resolved;
                        peeked = TeamService.Peek(folder.Text, out resolved);
                        var masters = peeked.Members.Where(m => m.IsMaster).Select(m => m.Name).ToList();
                        info.Text = "チーム名：" + peeked.TeamName + "\nマスター：" + string.Join("、", masters) + "\nチーム確認コードを確認しました。下の共有内容を確認して参加してください。";
                        folder.ReadOnly = true;   // 確認したフォルダから変えない
                    }
                    catch (TeamException ex) { peeked = null; info.Text = ex.Message; }
                    catch (Exception ex) { peeked = null; info.Text = "読み込めませんでした：" + ex.Message; }
                };
                load.Click += (s, e) => doLoad();
                if (!string.IsNullOrEmpty(initialInvite))
                {
                    // 招待リンクから開かれた場合：招待コードを入れて自動で読み込む
                    string inFolder, inTeam, inFp;
                    if (TeamInvite.TryDecodeLink(initialInvite, out inFolder, out inTeam, out inFp)
                        || TeamInvite.TryDecode(initialInvite, out inFolder, out inTeam, out inFp))
                    {
                        invite.Text = TeamInvite.Encode(inFolder, inTeam, inFp);
                    }
                }
                Buttons(f, "参加する", () =>
                {
                    if (peeked == null && !string.IsNullOrWhiteSpace(folder.Text) && Directory.Exists(folder.Text)) doLoad();   // 未読込なら自動で読み込む
                    if (peeked == null) throw new TeamException("共有フォルダを選ぶか、招待コードを貼り付けてください（自動で内容を読み込みます）");
                    if (!agree.Checked) throw new TeamException("共有への同意にチェックを入れてください");
                    if (TeamCrypto.NormalizeFingerprint(fp.Text).Length != 32) throw new TeamException("チーム確認コード（32桁）を入力してください");
                    svc.Join(folder.Text, fp.Text, name.Text, email.Text, ShareScope.Prompts, peeked);
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static bool Consent(IWin32Window owner, TeamService svc)
        {
            var team = svc.Team;
            if (team == null) return false;
            bool full = svc.Local.ConsentedScope == ShareScope.Prompts;
            var effective = svc.EffectiveScope(team);
            using (var f = NewDialog("共有される内容", 540, 380))
            {
                var t = Grid(2);
                AddFull(t, Note(full
                    ? "あなたは、次の内容をマスターに共有することに同意しています。共有をやめる場合は「チームから抜ける」を選んでください（共有フォルダの報告を削除します）。"
                    : "以前の版で一部の範囲だけに同意しています。このチームでは、すべて（依頼文の本文を含む）の共有への同意が必要です。同意しない場合は「チームから抜ける」を選んでください。", 500));
                AddFull(t, new Label { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(3, 4, 3, 8), Text = "共有される内容：" + ScopeText.Describe(ShareScope.Prompts) });
                if (effective < ShareScope.Prompts)
                    AddFull(t, Note("※会社のポリシーやマスターの設定により、現在実際に共有しているのは「" + ScopeText.Label(effective) + "」までです。", 500));
                f.Controls.Add(t);
                if (full)
                {
                    var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(10) };
                    var close = new Button { Text = "閉じる", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(96, 28) };
                    p.Controls.Add(close);
                    f.CancelButton = close;
                    f.Controls.Add(p);
                    f.ShowDialog(owner);
                    return false;
                }
                Buttons(f, "すべての共有に同意する", () => { svc.UpdateConsent(ShareScope.Prompts); return true; });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        /// <summary>メールアドレスでメンバー（一般ユーザー）をまとめて追加する</summary>
        public static bool AddMembers(IWin32Window owner, TeamService svc)
        {
            using (var f = NewDialog("メンバーを追加（一般ユーザー）", 560, 520))
            {
                var t = Grid(2);
                AddFull(t, Note("追加する人のメールアドレスを1行に1人ずつ入力してください。「名前, メールアドレス」「名前 <メールアドレス>」の形や、Excel・Slack からの貼り付けにも対応しています。", 520));
                var list = new TextBox { Multiline = true, Height = 170, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
                AddRow(t, "メンバー", list);
                var scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (ShareScope s in Enum.GetValues(typeof(ShareScope))) scope.Items.Add(ScopeText.Label(s));
                scope.SelectedIndex = (int)ShareScope.Prompts;
                AddRow(t, "閲覧範囲", scope);
                var desc = new Label { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8) };
                AddFull(t, desc);
                scope.SelectedIndexChanged += (s, e) => desc.Text = ScopeText.Describe((ShareScope)scope.SelectedIndex) + "（本人の同意の範囲まで）";
                desc.Text = ScopeText.Describe(ShareScope.Prompts) + "（本人の同意の範囲まで）";
                AddFull(t, Note("登録した人が、同じメールアドレスで Claude Code・Codex にログインしたPCからチームに参加すると、自動で利用量が集計されます（チーム設定で変更できます）。追加した後に表示される招待文を、Slack やメールで送ってください。", 520));
                f.Controls.Add(t);
                bool added = false;
                var addedEmails = new List<string>();
                Buttons(f, "追加", () =>
                {
                    var errors = new List<string>();
                    var members = TeamConfig.ParseMemberList(list.Text, errors);
                    if (errors.Count > 0) throw new TeamException(string.Join("\n", errors.Take(10)));
                    if (members.Count == 0) throw new TeamException("メールアドレスを入力してください");
                    var skipped = new List<string>();
                    int n = svc.AddMembers(members, (ShareScope)scope.SelectedIndex, svc.Local.DeclaredName ?? Environment.UserName, skipped);
                    added = n > 0;
                    addedEmails.AddRange(members.SelectMany(x => x.Emails).Where(x => !skipped.Contains(x)));
                    Info(f, n + "人を追加しました。" + (skipped.Count > 0 ? "\n\n登録済みのため飛ばしたメールアドレス：\n" + string.Join("\n", skipped.Take(20)) : "") + (added ? "\n\n次の画面で、招待メールを作成できます。" : ""));
                    return true;
                });
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                if (added) ShowInvite(owner, svc, addedEmails);
                return added;
            }
        }

        /// <summary>招待文（招待コード付き）を表示してコピーできるようにする</summary>
        public static void ShowInvite(IWin32Window owner, TeamService svc, IEnumerable<string> recipients = null)
        {
            var team = svc.Team;
            if (team == null || svc.Local.SharedFolder == null) { Error(owner, "チーム設定を読み込めていません"); return; }
            using (var f = NewDialog("招待を送る", 640, 560))
            {
                var t = Grid(2);
                AddFull(t, Note("招待メールを送ってください。「メールで送信」はこのアプリから直接送ります（初回に送信設定を入力）。「メールアプリで作成」「Gmail で作成」は宛先・件名・本文を入れた作成画面を開くだけです（送信はご自身で）。Slack で送る場合は「コピー」を使います。招待コードに秘密の情報は含まれません。パスフレーズは絶対に送らないでください。", 600));
                var to = new TextBox { Text = string.Join(", ", (recipients ?? new string[0])) };
                AddRow(t, "宛先", to);
                var text = new TextBox { Multiline = true, ReadOnly = true, Height = 280, ScrollBars = ScrollBars.Vertical, Text = TeamInvite.Message(team, svc.Local.SharedFolder), BackColor = SystemColors.Window };
                AddFull(t, text);
                text.Dock = DockStyle.Fill;
                f.Controls.Add(t);
                var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(10) };
                var close = new Button { Text = "閉じる", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 28) };
                var copy = new Button { Text = "コピー", AutoSize = true, MinimumSize = new Size(90, 28) };
                var gmail = new Button { Text = "Gmail で作成", AutoSize = true, MinimumSize = new Size(104, 28) };
                var mailApp = new Button { Text = "メールアプリで作成", AutoSize = true, MinimumSize = new Size(122, 28) };
                var send = new Button { Text = "メールで送信", AutoSize = true, MinimumSize = new Size(104, 28), Font = new Font(UiFont, FontStyle.Bold) };
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(text.Text); copy.Text = "コピーしました"; }
                    catch (Exception ex) { Error(f, "コピーできませんでした：" + ex.Message); }
                };
                Func<List<string>> rcpts = () =>
                {
                    var list = to.Text.Split(new[] { ',', ';', ' ', '\r', '\n', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                    var bad = list.Where(x => TeamConfig.NormalizeEmail(x) == null).ToList();
                    if (list.Count == 0) { Error(f, "宛先のメールアドレスを入力してください"); return null; }
                    if (bad.Count > 0) { Error(f, "宛先のメールアドレスが正しくありません：" + string.Join(", ", bad)); return null; }
                    return list;
                };
                mailApp.Click += (s, e) => { var r = rcpts(); if (r != null) OpenMailUrl(f, TeamInvite.MailtoUrl(r, team, svc.Local.SharedFolder)); };
                gmail.Click += (s, e) => { var r = rcpts(); if (r != null) OpenMailUrl(f, TeamInvite.GmailUrl(r, team, svc.Local.SharedFolder)); };
                send.Click += (s, e) =>
                {
                    var r = rcpts();
                    if (r == null) return;
                    if (!svc.Store.ExportAllowed) { Error(f, "組織のポリシーで無効になっています"); return; }
                    var cfg = MailConfig.Load(svc.Store.Paths);
                    if (!cfg.IsComplete && !MailSettings(f, cfg, svc.Store.Paths, r.FirstOrDefault())) return;
                    try
                    {
                        f.Cursor = Cursors.WaitCursor;
                        cfg.Send(r, TeamInvite.MailSubject(team), TeamInvite.Message(team, svc.Local.SharedFolder));
                        Info(f, r.Count + "件の宛先に招待メールを送信しました。");
                        f.DialogResult = DialogResult.OK; f.Close();
                    }
                    catch (MailException ex) { Error(f, ex.Message); }
                    finally { f.Cursor = Cursors.Default; }
                };
                var gear = new Button { Text = "送信設定…", AutoSize = true, MinimumSize = new Size(88, 28) };
                gear.Click += (s, e) => { var cfg = MailConfig.Load(svc.Store.Paths); MailSettings(f, cfg, svc.Store.Paths, rcpts() == null ? null : to.Text); };
                p.Controls.Add(close);
                p.Controls.Add(copy);
                p.Controls.Add(gmail);
                p.Controls.Add(mailApp);
                p.Controls.Add(send);
                p.Controls.Add(gear);
                f.AcceptButton = send;
                f.CancelButton = close;
                f.Controls.Add(p);
                f.ShowDialog(owner);
            }
        }

        /// <summary>SMTP 送信の設定ダイアログ。パスワード（アプリパスワード）は DPAPI で保存する。保存したら true</summary>
        public static bool MailSettings(IWin32Window owner, MailConfig cfg, AppPaths paths, string forEmail)
        {
            using (var f = NewDialog("メール送信の設定", 560, 460))
            {
                var t = Grid(2);
                AddFull(t, Note("このアプリから招待メールを直接送るための設定です。送信サーバー（SMTP）にログインして送ります。GmailやOutlookは、通常のパスワードではなく「アプリパスワード」（2段階認証をオンにして発行する専用パスワード）が必要です。パスワードはこのPCの本人だけが読める形（DPAPI）で保存し、外部には送りません。", 520));
                var from = new TextBox { Text = cfg.FromEmail ?? forEmail ?? "" };
                AddRow(t, "あなたのメール", from);
                var name = new TextBox { Text = cfg.FromName ?? "" };
                AddRow(t, "差出人名（任意）", name);
                var host = new TextBox { Text = cfg.Host ?? "" };
                AddRow(t, "送信サーバー(SMTP)", host);
                var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.Port > 0 ? cfg.Port : 587, Width = 90, Anchor = AnchorStyles.Left };
                AddRow(t, "ポート", port);
                var ssl = new CheckBox { Text = "暗号化して送信（TLS/STARTTLS。ほとんどの場合オン）", Checked = cfg.UseSsl, AutoSize = true };
                AddFull(t, ssl);
                var user = new TextBox { Text = cfg.Username ?? "" };
                AddRow(t, "ユーザー名（任意）", user);
                var pass = new TextBox { UseSystemPasswordChar = true };
                AddRow(t, "アプリパスワード", pass);
                AddFull(t, Note("メールアドレスを入れると、Gmail・Outlook・Office365 などは送信サーバーを自動で補います。ユーザー名は通常メールアドレスと同じです（空欄で可）。パスワードは、変更するときだけ入力してください（空欄なら前回の設定を保持）。", 520));
                from.Leave += (s, e) => { if (string.IsNullOrWhiteSpace(host.Text) && from.Text.Contains("@")) { var tmp = new MailConfig(); MailConfig.ApplyPreset(tmp, from.Text.Trim()); host.Text = tmp.Host; port.Value = tmp.Port; ssl.Checked = tmp.UseSsl; if (string.IsNullOrWhiteSpace(user.Text)) user.Text = tmp.Username; } };
                f.Controls.Add(t);
                bool saved = false;
                Buttons(f, "保存", () =>
                {
                    if (TeamConfig.NormalizeEmail(from.Text) == null) throw new TeamException("あなたのメールアドレスを正しく入力してください");
                    if (string.IsNullOrWhiteSpace(host.Text)) throw new TeamException("送信サーバー(SMTP)を入力してください");
                    if (!cfg.HasPassword && string.IsNullOrEmpty(pass.Text)) throw new TeamException("アプリパスワードを入力してください");
                    cfg.FromEmail = from.Text.Trim();
                    cfg.FromName = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
                    cfg.Host = host.Text.Trim();
                    cfg.Port = (int)port.Value;
                    cfg.UseSsl = ssl.Checked;
                    cfg.Username = string.IsNullOrWhiteSpace(user.Text) ? cfg.FromEmail : user.Text.Trim();
                    if (!string.IsNullOrEmpty(pass.Text)) cfg.SetPassword(pass.Text);
                    cfg.Save(paths);
                    saved = true;
                    return true;
                });
                f.ShowDialog(owner);
                return saved && cfg.IsComplete;
            }
        }

        /// <summary>メール作成画面を開く（アプリ自身は通信しない。既定のメールアプリ・ブラウザに URL を渡すだけ）</summary>
        static void OpenMailUrl(IWin32Window owner, string url)
        {
            if (!(url.StartsWith("mailto:") || url.StartsWith("https://mail.google.com/"))) return;
            try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })) { } }
            catch (Exception ex) { Error(owner, "メールの作成画面を開けませんでした（既定のメールアプリが設定されていない可能性があります）。「コピー」で本文をコピーして送ってください。\n\n" + ex.Message); }
        }

        public static string AskPassphrase(IWin32Window owner, string title, string message, bool confirm)
        {
            using (var f = NewDialog(title, 460, confirm ? 250 : 210))
            {
                var t = Grid(2);
                AddFull(t, Note(message, 420));
                var p1 = new TextBox { UseSystemPasswordChar = true };
                AddRow(t, "パスフレーズ", p1);
                TextBox p2 = null;
                if (confirm) { p2 = new TextBox { UseSystemPasswordChar = true }; AddRow(t, "確認", p2); }
                f.Controls.Add(t);
                string result = null;
                Buttons(f, "OK", () =>
                {
                    if (p2 != null && p1.Text != p2.Text) throw new TeamException("パスフレーズが一致しません");
                    result = p1.Text;
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK ? result : null;
            }
        }

        // ------------------------------------------------------------------ メンバー編集

        public static bool EditMember(IWin32Window owner, MemberEntry m, bool isNew)
        {
            using (var f = NewDialog(isNew ? "メンバーを追加" : "メンバーを編集", 560, 560))
            {
                var t = Grid(2);
                var name = new TextBox { Text = m.Name ?? "" };
                AddRow(t, "名前", name);
                var emails = new TextBox { Text = string.Join("\r\n", m.Emails), Multiline = true, Height = 60, ScrollBars = ScrollBars.Vertical };
                AddRow(t, "メールアドレス", emails);
                AddFull(t, Note("1行に1つ。Claude・Codex にログインしているメールアドレスを登録すると、そのPCの報告と自動で結び付きます。", 520));
                var role = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                role.Items.Add("一般ユーザー（自分の分だけ見られる）");
                role.Items.Add("マスター（全員を見られる・管理できる）");
                role.SelectedIndex = m.IsMaster ? 1 : 0;
                AddRow(t, "権限", role);
                var scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (ShareScope s in Enum.GetValues(typeof(ShareScope))) scope.Items.Add(ScopeText.Label(s));
                scope.SelectedIndex = (int)m.Scope;
                AddRow(t, "閲覧範囲", scope);
                var desc = new Label { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8) };
                AddFull(t, desc);
                scope.SelectedIndexChanged += (s, e) => desc.Text = ScopeText.Describe((ShareScope)scope.SelectedIndex) + "（本人の同意と組織ポリシーの範囲まで）";
                desc.Text = ScopeText.Describe(m.Scope) + "（本人の同意と組織ポリシーの範囲まで）";
                var plans = new DataGridView { Height = 120, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window };
                plans.Columns.Add("name", "契約プラン（例：ChatGPT Pro、Claude Max）");
                plans.Columns.Add("fee", "月額（円）");
                foreach (var p in m.Plans) plans.Rows.Add(p.Name, p.MonthlyFeeJpy.ToString(CultureInfo.InvariantCulture));
                AddRow(t, "契約プラン", plans);
                var note = new TextBox { Text = m.Note ?? "", Multiline = true, Height = 48, ScrollBars = ScrollBars.Vertical };
                AddRow(t, "メモ", note);
                AddFull(t, Note("契約プランとメモは暗号化して保存され、マスターだけが読めます。", 520));
                f.Controls.Add(t);
                Buttons(f, isNew ? "追加" : "保存", () =>
                {
                    var list = emails.Text.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();
                    if (string.IsNullOrWhiteSpace(name.Text)) throw new TeamException("名前を入力してください");
                    if (list.Count == 0 || list.Any(e => TeamConfig.NormalizeEmail(e) == null)) throw new TeamException("メールアドレスを正しく入力してください");
                    var newPlans = new List<PlanEntry>();
                    foreach (DataGridViewRow row in plans.Rows)
                    {
                        if (row.IsNewRow) continue;
                        string pn = Convert.ToString(row.Cells[0].Value);
                        string fee = Convert.ToString(row.Cells[1].Value);
                        if (string.IsNullOrWhiteSpace(pn) && string.IsNullOrWhiteSpace(fee)) continue;
                        long yen;
                        if (!long.TryParse((fee ?? "").Replace(",", "").Replace("円", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out yen) || yen < 0)
                            throw new TeamException("プラン「" + pn + "」の月額を数字で入力してください");
                        newPlans.Add(new PlanEntry { Name = pn, MonthlyFeeJpy = yen });
                    }
                    m.Name = name.Text.Trim();
                    m.Emails = list;
                    m.IsMaster = role.SelectedIndex == 1;
                    m.Scope = (ShareScope)scope.SelectedIndex;
                    m.Plans = newPlans;
                    m.Note = Hardening.SafeText(note.Text, 2000);
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        // ------------------------------------------------------------------ 単価表

        public static bool EditPrices(IWin32Window owner, TeamConfig team)
        {
            using (var f = NewDialog("API換算の単価と為替", 900, 560))
            {
                f.FormBorderStyle = FormBorderStyle.Sizable;
                var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
                top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var rate = new NumericUpDown { Minimum = 1, Maximum = 1000, DecimalPlaces = 2, Value = (decimal)Math.Max(1, Math.Min(1000, team.UsdJpy)), Width = 120, Anchor = AnchorStyles.Left };
                top.Controls.Add(new Label { Text = "1米ドル＝（円）", AutoSize = true, Margin = new Padding(3, 7, 10, 3) });
                top.Controls.Add(rate);
                var note = Note("単価は 100万トークンあたりの米ドル。モデル名は先頭一致（例：claude-opus-5）。空欄は未設定で、その分は金額に含めず「単価未設定」と表示します。Codex（OpenAI）の行は、OpenAI の料金表を確認して入力してください。Claude の初期値は Anthropic API の公式料金（2026年6月時点の資料）です。サブスクリプション契約の実際の請求額とは異なります。", 860);
                top.Controls.Add(note);
                top.SetColumnSpan(note, 2);
                var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window };
                grid.Columns.Add("pattern", "モデル名（先頭一致）");
                grid.Columns.Add("in", "入力");
                grid.Columns.Add("out", "出力");
                grid.Columns.Add("read", "キャッシュ読取／キャッシュ済み入力");
                grid.Columns.Add("w5", "キャッシュ書込（5分）");
                grid.Columns.Add("w1h", "キャッシュ書込（1時間）");
                grid.Columns.Add("src", "出典・メモ");
                Func<double?, string> fmt = v => v == null ? "" : v.Value.ToString("0.######", CultureInfo.InvariantCulture);
                foreach (var p in team.Prices) grid.Rows.Add(p.Pattern, fmt(p.Input), fmt(p.Output), fmt(p.CacheRead), fmt(p.CacheWrite5m), fmt(p.CacheWrite1h), p.Source);
                f.Controls.Add(grid);
                f.Controls.Add(top);
                Buttons(f, "保存", () =>
                {
                    var list = new List<PriceEntry>();
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        if (row.IsNewRow) continue;
                        string pattern = Convert.ToString(row.Cells[0].Value);
                        if (string.IsNullOrWhiteSpace(pattern)) continue;
                        Func<int, double?> num = i =>
                        {
                            string s = Convert.ToString(row.Cells[i].Value);
                            if (string.IsNullOrWhiteSpace(s)) return null;
                            double d;
                            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d < 0 || d > 100000)
                                throw new TeamException("「" + pattern + "」の単価を数字で入力してください");
                            return d;
                        };
                        list.Add(new PriceEntry { Pattern = pattern.Trim(), Input = num(1), Output = num(2), CacheRead = num(3), CacheWrite5m = num(4), CacheWrite1h = num(5), Source = Convert.ToString(row.Cells[6].Value) });
                    }
                    team.Prices = list;
                    team.UsdJpy = (double)rate.Value;
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    /// <summary>マスター用のチーム管理画面（メンバーごとの利用量・金額・作業内容・依頼文を表で確認）</summary>
    public class TeamManagerForm : Form
    {
        readonly TeamService svc;
        readonly UsageStore store;
        TeamConfig editable;
        List<TeamReport> reports = new List<TeamReport>();
        List<Row> rows = new List<Row>();

        readonly ComboBox period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        readonly Label teamLabel = new Label { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
        readonly Panel pendingBar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(255, 244, 214), Visible = false };
        readonly Label pendingLabel = new Label { AutoSize = true, Location = new Point(12, 11) };
        readonly DataGridView grid = NewGrid();
        readonly DataGridView dailyGrid = NewGrid();
        readonly DataGridView totalsGrid = NewGrid();
        readonly DataGridView sessionGrid = NewGrid();
        readonly DataGridView promptGrid = NewGrid();
        readonly DataGridView pcGrid = NewGrid();
        readonly TextBox promptText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly Label sessionNote = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = SystemColors.GrayText, Padding = new Padding(4, 6, 4, 0) };
        readonly Label promptNote = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = SystemColors.GrayText, Padding = new Padding(4, 6, 4, 0) };
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        class Row
        {
            public MemberEntry Member;
            public List<TeamReport> Reports = new List<TeamReport>();
            public string Name { get { return Member != null ? Member.Name : (Reports.Select(r => r.DeclaredName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "（未登録）"); } }
        }

        static DataGridView NewGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BorderStyle = BorderStyle.None,
            };
        }

        public TeamManagerForm(TeamService svc, UsageStore store)
        {
            this.svc = svc;
            this.store = store;
            Font = TeamUi.UiFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1240, 800);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = TeamUi.ModBg;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 8), WrapContents = true, BackColor = Color.White };
            toolbar.Controls.Add(teamLabel);
            toolbar.Controls.Add(new Label { Text = "期間", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            foreach (var p in new[] { "今日", "直近7日", "直近30日", "今月", "先月" }) period.Items.Add(p);
            period.SelectedIndex = 3;
            period.SelectedIndexChanged += (s, e) => Render();
            toolbar.Controls.Add(period);
            AddButton(toolbar, "読み込み直す", () => Reload());
            AddButton(toolbar, "ダッシュボード（上層部向け）", OpenExecDashboard);
            AddButton(toolbar, "メンバーを追加（メール）", () => { if (TeamUi.AddMembers(this, svc)) Reload(); });
            AddButton(toolbar, "招待を送る", () => TeamUi.ShowInvite(this, svc));
            AddButton(toolbar, "単価・為替", EditPrices);
            AddButton(toolbar, "チーム設定", TeamSettings);
            AddButton(toolbar, "表をCSVで保存", () => SaveCsv(grid, "AIチーム利用状況"));
            AddButton(toolbar, "マスターを外す（鍵を更新）", DemoteOrRemoveMaster);

            var approve = new Button { Text = "確認する", AutoSize = true, Location = new Point(380, 6) };
            approve.Click += (s, e) => ReviewPcs();
            pendingBar.Controls.Add(pendingLabel);
            pendingBar.Controls.Add(approve);
            pendingBar.Resize += (s, e) => approve.Left = pendingBar.Width - approve.Width - 12;

            grid.SelectionChanged += (s, e) => RenderDetail();
            grid.CellDoubleClick += (s, e) => EditSelected();
            var menu = new ContextMenuStrip();
            menu.Items.Add("編集…", null, (s, e) => EditSelected());
            menu.Items.Add("招待メールを作成…", null, (s, e) => { var r = SelectedRow; TeamUi.ShowInvite(this, svc, r != null && r.Member != null ? r.Member.Emails : null); });
            menu.Items.Add("メンバーとして登録…", null, (s, e) => RegisterSelected());
            menu.Items.Add("チームから削除", null, (s, e) => RemoveSelected());
            grid.ContextMenuStrip = menu;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(Page("日別の合計", totalsGrid, "CSVで保存", () => SaveCsv(totalsGrid, "日別の合計")));
            tabs.TabPages.Add(Page("日別・モデル別", dailyGrid, "この期間の内訳をCSVで保存", () => SaveCsv(dailyGrid, "日別モデル別")));
            var sessionPage = Page("セッション", sessionGrid, "CSVで保存", () => SaveCsv(sessionGrid, "セッション"));
            sessionPage.Controls.Add(sessionNote);
            tabs.TabPages.Add(sessionPage);
            var promptSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            promptSplit.Panel1.Controls.Add(promptGrid);
            promptSplit.Panel2.Controls.Add(promptText);
            var promptPage = Page("依頼文（誰が・何を）", promptSplit, "CSVで保存", () => SaveCsv(promptGrid, "依頼文"));
            promptPage.Controls.Add(promptNote);
            // このPC（マスター）の組織ポリシーで依頼文の表示が禁止されている場合は、タブ自体を出さない
            if (store.PromptsAllowed) tabs.TabPages.Add(promptPage);
            tabs.TabPages.Add(Page("PC・報告", pcGrid, "PCの承認・削除…", ReviewPcs));
            promptGrid.SelectionChanged += (s, e) =>
            {
                promptText.Text = promptGrid.SelectedRows.Count == 0 ? "" : Convert.ToString(promptGrid.SelectedRows[0].Tag);
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(grid);
            split.Panel2.Controls.Add(tabs);
            Load += (s, e) =>
            {
                try { split.SplitterDistance = Math.Max(120, (int)(split.Height * 0.45)); } catch { }
                try { promptSplit.SplitterDistance = Math.Max(80, (int)(promptSplit.Height * 0.45)); } catch { }
            };

            var strip = new StatusStrip();
            strip.Items.Add(status);

            Controls.Add(split);
            Controls.Add(pendingBar);
            Controls.Add(toolbar);
            Controls.Add(strip);
            foreach (var dg in new[] { grid, totalsGrid, dailyGrid, sessionGrid, promptGrid, pcGrid }) TeamUi.StyleGrid(dg);
            Shown += (s, e) => { Reload(); TeamUi.Modernize(this); };
        }

        static TabPage Page(string title, Control content, string buttonText, Action onClick)
        {
            var page = new TabPage(title);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            var b = new Button { Text = buttonText, AutoSize = true };
            b.Click += (s, e) => onClick();
            bar.Controls.Add(b);
            page.Controls.Add(content);
            page.Controls.Add(bar);
            return page;
        }

        static void AddButton(FlowLayoutPanel p, string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(6, 3, 0, 3) };
            b.Click += (s, e) => a();
            p.Controls.Add(b);
        }

        // ------------------------------------------------------------------ 読み込み

        public void Reload()
        {
            status.Text = "読み込み中…";
            Cursor = Cursors.WaitCursor;
            var ui = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                List<TeamReport> loaded = null;
                TeamConfig team = null;
                try
                {
                    team = svc.LoadTeam();
                    if (!svc.IsMaster) throw new TeamException("このPCではマスター権限が有効になっていません");
                    loaded = svc.ReadReports();
                }
                catch (TeamException ex) { error = ex.Message; }
                catch (Exception ex) { error = "読み込めませんでした：" + ex.Message; }
                ui.Post(__ =>
                {
                    if (IsDisposed) return;
                    Cursor = Cursors.Default;
                    if (error != null) { status.Text = error; TeamUi.Error(this, error); return; }
                    editable = team.Clone();
                    reports = loaded;
                    Render();
                }, null);
            });
        }

        /// <summary>検査用：同期で読み込んで表示する</summary>
        public void LoadNow()
        {
            var team = svc.LoadTeam();
            reports = svc.ReadReports();
            editable = team.Clone();
            Render();
        }

        /// <summary>検査用：メールアドレスでメンバーの行を選ぶ</summary>
        public void SelectRowByEmail(string email)
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                var r = row.Tag as Row;
                if (r != null && r.Member != null && r.Member.Matches(new[] { email })) { grid.ClearSelection(); row.Selected = true; RenderDetail(); return; }
            }
        }

        /// <summary>検査用：表示中の表の内容</summary>
        public List<string[]> GridText(int which)
        {
            var g = which == 0 ? grid : totalsGrid;
            return g.Rows.Cast<DataGridViewRow>().Select(row => row.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToArray()).ToList();
        }

        public void SelectFirstRow() { if (grid.Rows.Count > 0) { grid.ClearSelection(); grid.Rows[0].Selected = true; RenderDetail(); } }
        public void SelectDetailTab(int index) { foreach (Control c in Controls) SelectTabIn(c, index); }
        static void SelectTabIn(Control c, int index) { var t = c as TabControl; if (t != null) { t.SelectedIndex = index; return; } foreach (Control k in c.Controls) SelectTabIn(k, index); }

        // ------------------------------------------------------------------ 集計

        bool InPeriod(string date)
        {
            var today = DateTime.Now.Date;
            DateTime d;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return false;
            switch (period.SelectedIndex)
            {
                case 0: return d == today;
                case 1: return d > today.AddDays(-7) && d <= today;
                case 2: return d > today.AddDays(-30) && d <= today;
                case 3: return d.Year == today.Year && d.Month == today.Month;
                default: var last = today.AddMonths(-1); return d.Year == last.Year && d.Month == last.Month;
            }
        }

        IEnumerable<DailyUsage> Days(Row r) { return r.Reports.SelectMany(x => x.Daily).Where(d => InPeriod(d.Date)); }

        void BuildRows()
        {
            rows = new List<Row>();
            var usable = reports.Where(r => r.Error == null && r.Approved).ToList();
            var used = new HashSet<TeamReport>();
            var byMember = new Dictionary<string, List<TeamReport>>();
            ambiguousCount = 0;
            foreach (var r in usable)
            {
                bool ambiguous;
                var m = editable.ResolveMember(r.DetectedEmails, r.DeclaredEmail, out ambiguous);
                // 複数のメンバーのメールに一致する報告は、どちらにも集計しない（なりすまし・登録ミス対策）
                if (ambiguous) { ambiguousCount++; used.Add(r); continue; }
                if (m == null) continue;
                List<TeamReport> l;
                if (!byMember.TryGetValue(m.Id, out l)) byMember[m.Id] = l = new List<TeamReport>();
                l.Add(r);
                used.Add(r);
            }
            foreach (var m in editable.Members)
            {
                var row = new Row { Member = m };
                List<TeamReport> l;
                if (byMember.TryGetValue(m.Id, out l)) row.Reports.AddRange(l);
                rows.Add(row);
            }
            foreach (var g in usable.Where(r => !used.Contains(r)).GroupBy(r => r.AllEmails.FirstOrDefault() ?? r.InstallId))
            {
                var row = new Row();
                row.Reports.AddRange(g);
                rows.Add(row);
            }
        }

        int ambiguousCount;

        void Render()
        {
            if (editable == null) return;
            teamLabel.Text = "チーム：" + editable.TeamName + "　（メンバー " + editable.Members.Count + "人・チーム確認コード " + editable.Fingerprint + "）";
            Text = AppInfo.Name + " — チーム管理（" + editable.TeamName + "）";
            BuildRows();
            string selected = grid.SelectedRows.Count > 0 ? Convert.ToString(grid.SelectedRows[0].Cells[0].Value) + "|" + Convert.ToString(grid.SelectedRows[0].Cells[1].Value) : null;

            grid.Columns.Clear();
            foreach (var c in new[] { "名前", "メール", "権限", "閲覧範囲（設定／実際）", "PC", "最終報告", "Claude Code トークン", "Codex トークン", "合計トークン", "API換算（円・目安）", "プラン月額（円）", "Codex残り（5時間／週間）", "Claude残り（5時間／週間）", "状態" })
                grid.Columns.Add(c, c);
            foreach (int i in new[] { 6, 7, 8, 9, 10, 11, 12 }) grid.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                var days = Days(r).ToList();
                long claude = days.Where(d => d.Tool == Tool.ClaudeCode).Sum(d => d.Sum.Total);
                long codex = days.Where(d => d.Tool == Tool.Codex).Sum(d => d.Sum.Total);
                // PC内に Codex の記録がない場合（別のPC・アプリで使っている等）は、サービスの集計値を表示する
                long codexService = ServiceDaily(r).Values.Sum();
                bool useService = codex == 0 && codexService > 0;
                string codexText = useService ? Fmt.Full(codexService) + "（サービス集計）" : Fmt.Full(codex);
                var cost = CostCalc.Sum(days, editable);
                var latest = r.Reports.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
                string role = r.Member == null ? "未登録" : (r.Member.IsMaster ? "マスター" : "一般ユーザー");
                string scope = r.Member == null ? ScopeText.Label(editable.DefaultScope) : ScopeText.Label(r.Member.Scope);
                if (latest != null) scope += "／" + ScopeText.Label(latest.Scope);
                string state;
                if (r.Reports.Count == 0) state = "報告なし";
                else if ((now - latest.CreatedAtUtc).TotalHours > 24) state = "24時間以上報告なし";
                else if (r.Member != null && latest.Scope < r.Member.Scope) state = "本人の同意待ち・ポリシー制限";
                else state = "正常";
                string costText = cost.Tokens == 0 ? "0" : (cost.Jpy.ToString("#,0", CultureInfo.InvariantCulture) + (cost.UnpricedTokens > 0 ? "（一部単価未設定）" : ""));
                int idx = grid.Rows.Add(r.Name, r.Member != null ? string.Join(", ", r.Member.Emails) : string.Join(", ", r.Reports.SelectMany(x => x.AllEmails).Distinct()),
                    role, scope, string.Join(", ", r.Reports.Select(x => x.Machine).Distinct()),
                    latest == null ? "—" : latest.CreatedAtUtc.ToLocalTime().ToString("M/d HH:mm"),
                    Fmt.Full(claude), codexText, Fmt.Full(claude + (useService ? codexService : codex)), costText,
                    r.Member == null ? "—" : !editable.PrivateOpened ? "（読めません）" : r.Member.MonthlyFeeJpy.ToString("#,0", CultureInfo.InvariantCulture),
                    QuotaText(latest, "codex"), QuotaText(latest, "claude"), state);
                grid.Rows[idx].Tag = r;
                if (r.Member == null) grid.Rows[idx].DefaultCellStyle.ForeColor = Color.DarkOrange;
                if (selected != null && selected == r.Name + "|" + Convert.ToString(grid.Rows[idx].Cells[1].Value)) grid.Rows[idx].Selected = true;
            }

            var pending = reports.Where(r => r.Error == null && !r.Approved).ToList();
            var broken = reports.Where(r => r.Error != null).ToList();
            pendingBar.Visible = pending.Count > 0 || ambiguousCount > 0;
            pendingLabel.Text = (pending.Count > 0 ? "承認待ちのPCが " + pending.Count + "台あります（鍵の変更 " + pending.Count(r => r.KeyChanged) + "台・メールの変更 " + pending.Count(r => r.EmailsChanged && !r.KeyChanged) + "台）。承認するまで集計に含めません。" : "") +
                (ambiguousCount > 0 ? "　複数のメンバーに一致する報告が " + ambiguousCount + "件あり、集計から除いています（メールの登録を確認してください）。" : "");
            long totalJpy = (long)rows.Sum(r => CostCalc.Sum(Days(r), editable).Jpy);
            long plans = editable.Members.Sum(m => m.MonthlyFeeJpy);
            status.Text = "報告 " + reports.Count + "件（承認済み " + reports.Count(r => r.Approved && r.Error == null) + "・承認待ち " + pending.Count + "・読めない " + broken.Count + "）　期間のAPI換算合計 約" + totalJpy.ToString("#,0") +
                "円　プラン月額合計 " + plans.ToString("#,0") + "円　※API換算は目安で、サブスクリプションの実際の請求額とは異なります。読み込み " + DateTime.Now.ToString("HH:mm");
            RenderDetail();
        }

        /// <summary>期間内の Codex サービス集計（日付ごと）。同じアカウントを複数のPCで使う場合も重複して数えない</summary>
        /// <summary>上層部向けダッシュボードを開く（現在の期間・メンバーで集計）</summary>
        void OpenExecDashboard()
        {
            var f = BuildExecDashboard();
            if (f != null) using (f) f.ShowDialog(this);
        }

        /// <summary>検査・見本用：ダッシュボードを組み立てて返す（表示はしない）</summary>
        public TeamDashboardForm BuildExecDashboard()
        {
            if (editable == null) return null;
            var res = TeamAnalytics.Build(editable, reports, InPeriod);
            return new TeamDashboardForm(res.TeamName, period.Text, res.Rows, res.Daily, res.UsdJpy, res.FeesReadable, res.TeamSize, store.Paths, !store.Policy.DisableAiSuggest);
        }

        Dictionary<string, long> ServiceDaily(Row r)
        {
            var svc = new Dictionary<string, long>();
            foreach (var rep in r.Reports)
                foreach (var x in rep.CodexServiceDaily.Where(x => x.Date != null && InPeriod(x.Date)))
                {
                    long cur;
                    svc.TryGetValue(x.Date, out cur);
                    svc[x.Date] = Math.Max(cur, x.Tokens);
                }
            return svc;
        }

        /// <summary>残り枠：「5時間 99%／週間 0%」（古い版の報告は少ない方の値だけ）</summary>
        static string QuotaText(TeamReport r, string tool)
        {
            if (r == null || r.Quota == null) return "—";
            var q = Json.Obj(r.Quota, tool);
            if (q == null) return "—";
            double? five = Json.Num(q, "fiveHourRemaining"), week = Json.Num(q, "weeklyRemaining");
            if (five == null && week == null)
            {
                double? v = Json.Num(q, "remaining");
                return v == null ? "—" : Math.Floor(v.Value) + "%";
            }
            Func<double?, string> pct = x => x == null ? "—" : Math.Floor(x.Value) + "%";
            return pct(five) + "／" + pct(week);
        }

        static double? MinOf(double? a, double? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return Math.Min(a.Value, b.Value);
        }

        Row SelectedRow { get { return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as Row; } }

        void RenderDetail()
        {
            var r = SelectedRow;
            totalsGrid.Columns.Clear();
            foreach (var c in new[] { "日付", "Claude Code（このPCの記録）", "Codex（このPCの記録）", "Codex（サービスの集計）" }) totalsGrid.Columns.Add(c, c);
            foreach (int i in new[] { 1, 2, 3 }) totalsGrid.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            dailyGrid.Columns.Clear();
            foreach (var c in new[] { "日付", "ツール", "モデル", "入力", "出力", "キャッシュ読取／キャッシュ済み", "キャッシュ作成（うち1時間）", "推論", "合計", "API換算（円）" }) dailyGrid.Columns.Add(c, c);
            sessionGrid.Columns.Clear();
            foreach (var c in new[] { "開始", "終了", "ツール", "タイトル", "作業フォルダ", "経路", "入力の種類", "モデル", "依頼", "トークン", "PC" }) sessionGrid.Columns.Add(c, c);
            promptGrid.Columns.Clear();
            foreach (var c in new[] { "日時", "ツール", "経路", "入力の種類", "PC", "依頼文" }) promptGrid.Columns.Add(c, c);
            pcGrid.Columns.Clear();
            foreach (var c in new[] { "PC", "Windowsユーザー", "申告の名前", "申告メール", "検出メール", "共有範囲（実際）", "本人の同意", "最終報告", "アプリ版", "PC確認コード", "状態" }) pcGrid.Columns.Add(c, c);
            promptText.Text = "";
            if (r == null) { sessionNote.Text = promptNote.Text = "上の表でメンバーを選んでください。"; return; }

            // 日別の合計（Mac版の「履歴」と同じ単位：ツールごとの1日の合計トークン）
            var days = Days(r).ToList();
            var svc = ServiceDaily(r);
            foreach (var date in days.Select(d => d.Date).Concat(svc.Keys).Distinct().OrderByDescending(x => x))
            {
                long cc = days.Where(d => d.Date == date && d.Tool == Tool.ClaudeCode).Sum(d => d.Sum.Total);
                long cx = days.Where(d => d.Date == date && d.Tool == Tool.Codex).Sum(d => d.Sum.Total);
                long sv;
                bool hasSvc = svc.TryGetValue(date, out sv);
                Func<long, string> both = n => n == 0 ? "—" : Fmt.Tokens(n) + "（" + Fmt.Full(n) + "）";
                totalsGrid.Rows.Add(date, both(cc), both(cx), hasSvc ? both(sv) : "—");
            }

            foreach (var d in Days(r).OrderByDescending(d => d.Date).ThenBy(d => d.Tool))
            {
                var usd = CostCalc.Usd(d, editable);
                dailyGrid.Rows.Add(d.Date, d.Tool == Tool.Codex ? "Codex" : "Claude Code", d.Model ?? "（不明）", Fmt.Full(d.Sum.Input), Fmt.Full(d.Sum.Output),
                    Fmt.Full(d.Sum.CacheRead), Fmt.Full(d.Sum.CacheWrite) + (d.Sum.CacheWrite1h > 0 ? "（" + Fmt.Full(d.Sum.CacheWrite1h) + "）" : ""),
                    Fmt.Full(d.Sum.Reasoning), Fmt.Full(d.Sum.Total), usd == null ? "単価未設定" : (usd.Value * editable.UsdJpy).ToString("#,0", CultureInfo.InvariantCulture));
            }

            int sessions = 0, prompts = 0;
            foreach (var rep in r.Reports)
            {
                foreach (var s in rep.Sessions)
                {
                    long start = Json.Long(s, "start") ?? 0, end = Json.Long(s, "end") ?? 0;
                    if (!Fmt.ValidUnixMs(start) || !Fmt.ValidUnixMs(end)) continue;
                    if (!InPeriod(Fmt.FromUnixMs(end).ToLocalTime().ToString("yyyy-MM-dd")) && !InPeriod(Fmt.FromUnixMs(start).ToLocalTime().ToString("yyyy-MM-dd"))) continue;
                    sessionGrid.Rows.Add(Fmt.FromUnixMs(start).ToLocalTime().ToString("M/d HH:mm"), Fmt.FromUnixMs(end).ToLocalTime().ToString("M/d HH:mm"),
                        Json.Str(s, "tool") == "codex" ? "Codex" : "Claude Code", Json.Str(s, "title") ?? "（タイトルなし）", Json.Str(s, "cwd"),
                        Json.Str(s, "via"), Json.Str(s, "inputKind"), Json.Str(s, "model"), Json.Long(s, "prompts") ?? 0, Fmt.Full(Json.Long(s, "totalTokens") ?? 0), rep.Machine);
                    sessions++;
                }
                foreach (var p in rep.Prompts)
                {
                    long t = Json.Long(p, "t") ?? 0;
                    if (!Fmt.ValidUnixMs(t) || !InPeriod(Fmt.FromUnixMs(t).ToLocalTime().ToString("yyyy-MM-dd"))) continue;
                    string text = Json.Str(p, "text") ?? "（読み取れませんでした）";
                    int idx = promptGrid.Rows.Add(Fmt.FromUnixMs(t).ToLocalTime().ToString("M/d HH:mm"), Json.Str(p, "tool") == "codex" ? "Codex" : "Claude Code",
                        Json.Str(p, "via"), Json.Str(p, "inputKind"), rep.Machine, Fmt.OneLine(text, 160));
                    promptGrid.Rows[idx].Tag = text;
                    prompts++;
                }
                pcGrid.Rows.Add(rep.Machine, rep.WindowsUser, rep.DeclaredName, rep.DeclaredEmail, string.Join(", ", rep.DetectedEmails), ScopeText.Label(rep.Scope),
                    ScopeText.Label(rep.ConsentedScope), rep.CreatedAtUtc.ToLocalTime().ToString("M/d HH:mm"), rep.AppVersion, rep.MemberFingerprint, rep.AutoApproved ? "自動承認（ログイン中のメールが一致）" : "承認済み");
            }
            sessionGrid.Sort(sessionGrid.Columns[1], System.ComponentModel.ListSortDirection.Descending);
            var scopeNow = r.Reports.Count == 0 ? ShareScope.Usage : r.Reports.Max(x => x.Scope);
            sessionNote.Text = scopeNow >= ShareScope.Summary ? sessions + "件（直近" + TeamService.SessionDays + "日分まで報告されます）" : "このメンバーの共有範囲には作業の概要が含まれていません（閲覧範囲の設定と本人の同意が必要です）";
            promptNote.Text = scopeNow >= ShareScope.Prompts ? prompts + "件（直近" + TeamService.SessionDays + "日分まで。行を選ぶと下に全文を表示）" : "このメンバーの共有範囲には依頼文が含まれていません（閲覧範囲を「依頼文まで」にし、本人の同意が必要です）";
        }

        // ------------------------------------------------------------------ 操作

        void Commit(Action<TeamConfig> change)
        {
            if (editable == null) return;
            var copy = editable.Clone();
            change(copy);
            try
            {
                if (LosesMaster(editable, copy)) throw new TeamException("マスターを外す・削除するときは、鍵の更新が必要です。ツールバーの「マスターを外す（鍵を更新）」を使ってください");
                svc.SaveTeam(copy, svc.Local.DeclaredName ?? Environment.UserName);
                editable = svc.Team.Clone();
                Render();
            }
            catch (TeamException ex) { TeamUi.Error(this, ex.Message); Reload(); }
        }

        /// <summary>変更前にマスターだった人が、変更後にマスターでない（または削除された）</summary>
        public static bool LosesMaster(TeamConfig before, TeamConfig after)
        {
            return before.Members.Where(m => m.IsMaster).Any(m => { var x = after.Members.FirstOrDefault(y => y.Id == m.Id); return x == null || !x.IsMaster; });
        }

        /// <summary>
        /// マスターを外す・削除する。外された人は旧パスフレーズと旧鍵を知っているので、同じ保存で鍵を作り直す。
        /// </summary>
        void DemoteOrRemoveMaster()
        {
            if (editable == null) return;
            var masters = editable.Members.Where(m => m.IsMaster).ToList();
            if (masters.Count < 2) { TeamUi.Info(this, "マスターが1人だけのため、外せません。先に別の人をマスターにしてください。"); return; }
            using (var f = TeamUi.NewDialog("マスターを外す・削除する", 520, 300))
            {
                var t = TeamUi.Grid(2);
                TeamUi.AddFull(t, TeamUi.Note("外された人は、以前のパスフレーズと鍵を知っています。以後の報告を読めないように、同時にチームの鍵を作り直し、新しいパスフレーズを設定します。他のマスターには新しいパスフレーズを安全な方法で伝えてください。", 480));
                var who = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (var m in masters) who.Items.Add(m.Name + "（" + string.Join(", ", m.Emails) + "）");
                who.SelectedIndex = 0;
                TeamUi.AddRow(t, "対象", who);
                var action = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                action.Items.Add("一般ユーザーにする");
                action.Items.Add("チームから削除する");
                action.SelectedIndex = 0;
                TeamUi.AddRow(t, "操作", action);
                f.Controls.Add(t);
                TeamUi.Buttons(f, "次へ（新しいパスフレーズ）", () =>
                {
                    var target = masters[who.SelectedIndex];
                    string pass = TeamUi.AskPassphrase(f, "新しいパスフレーズ", "新しいパスフレーズ（" + TeamService.MinPassphraseLength + "文字以上）を入力してください。", true);
                    if (pass == null) return false;
                    var copy = editable.Clone();
                    if (action.SelectedIndex == 1) copy.Members.RemoveAll(x => x.Id == target.Id);
                    else copy.Members.First(x => x.Id == target.Id).IsMaster = false;
                    svc.RotateKey(pass, svc.Local.DeclaredName ?? Environment.UserName, copy);
                    TeamUi.Info(f, "「" + target.Name + "」を" + (action.SelectedIndex == 1 ? "削除" : "一般ユーザーに") + "し、鍵を更新しました。\n新しいチーム確認コード：" + svc.Team.Fingerprint);
                    return true;
                });
                f.ShowDialog(this);
            }
            Reload();
        }

        void AddMember()
        {
            var m = new MemberEntry { Id = Guid.NewGuid().ToString("N"), Scope = editable == null ? ShareScope.Usage : editable.DefaultScope };
            if (TeamUi.EditMember(this, m, true)) Commit(t => t.Members.Add(m));
        }

        void EditSelected()
        {
            var r = SelectedRow;
            if (r == null) return;
            if (r.Member == null) { RegisterSelected(); return; }
            var copy = editable.Clone().Members.First(x => x.Id == r.Member.Id);
            bool wasMaster = copy.IsMaster;
            if (!TeamUi.EditMember(this, copy, false)) return;
            if (wasMaster && !copy.IsMaster) { TeamUi.Error(this, "マスターを外すときは、鍵の更新が必要です。ツールバーの「マスターを外す（鍵を更新）」を使ってください"); return; }
            Commit(t => { int i = t.Members.FindIndex(x => x.Id == copy.Id); t.Members[i] = copy; });
            if (!wasMaster && copy.IsMaster)
                TeamUi.Info(this, "「" + copy.Name + "」をマスターにしました。\n\n本人のPCで、AI Usage Bar の「チーム」タブから「マスター権限を有効にする」を選び、チームのパスフレーズを入力してもらってください（パスフレーズは安全な方法で伝えてください）。");
        }

        void RegisterSelected()
        {
            var r = SelectedRow;
            if (r == null || r.Member != null) return;
            var m = new MemberEntry { Id = Guid.NewGuid().ToString("N"), Name = r.Name, Emails = r.Reports.SelectMany(x => x.AllEmails).Distinct().ToList(), Scope = editable.DefaultScope };
            if (TeamUi.EditMember(this, m, true)) Commit(t => t.Members.Add(m));
        }

        void RemoveSelected()
        {
            var r = SelectedRow;
            if (r == null || r.Member == null) return;
            if (r.Member.IsMaster) { TeamUi.Error(this, "マスターを削除するときは、鍵の更新が必要です。ツールバーの「マスターを外す（鍵を更新）」を使ってください"); return; }
            if (MessageBox.Show(this, "「" + r.Member.Name + "」をチームから削除しますか？\n（その人のPCからの報告は、未登録として表示されます。報告ファイルを消す場合は「PC・報告」タブから削除してください）", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Commit(t => t.Members.RemoveAll(x => x.Id == r.Member.Id));
        }

        void EditPrices()
        {
            if (editable == null) return;
            var copy = editable.Clone();
            if (!TeamUi.EditPrices(this, copy)) return;
            Commit(t => { t.Prices = copy.Prices; t.UsdJpy = copy.UsdJpy; });
        }

        void TeamSettings()
        {
            if (editable == null) return;
            using (var f = TeamUi.NewDialog("チーム設定", 560, 470))
            {
                var t = TeamUi.Grid(2);
                var name = new TextBox { Text = editable.TeamName };
                TeamUi.AddRow(t, "チーム名", name);
                var scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (ShareScope s in Enum.GetValues(typeof(ShareScope))) scope.Items.Add(ScopeText.Label(s));
                scope.SelectedIndex = (int)editable.DefaultScope;
                TeamUi.AddRow(t, "未登録の人の閲覧範囲", scope);
                var auto = new CheckBox { Text = "登録したメールアドレスでログインしているPCは、承認なしで集計する", Checked = editable.AutoApproveByLogin, AutoSize = true, MaximumSize = new Size(520, 0) };
                TeamUi.AddFull(t, auto);
                TeamUi.AddFull(t, TeamUi.Note("Claude Code・Codex に実際にログインしているメールアドレスが、登録したメンバー1人だけに一致する場合に自動で集計します。本人が入力しただけのメールアドレスや、鍵・メールが変わったPC、マスターが承認を取り消したPCは、承認待ちになります。", 520));
                TeamUi.AddRow(t, "共有フォルダ", new TextBox { Text = svc.Local.SharedFolder, ReadOnly = true });
                TeamUi.AddRow(t, "チーム確認コード", new TextBox { Text = editable.Fingerprint + "（第" + editable.KeyGeneration + "世代）", ReadOnly = true });
                var rotate = new Button { Text = "鍵を更新（パスフレーズを変更）…", AutoSize = true, Anchor = AnchorStyles.Left };
                TeamUi.AddRow(t, "", rotate);
                TeamUi.AddFull(t, TeamUi.Note("鍵を更新すると、以前のパスフレーズや鍵では新しい報告を読めなくなります。マスターを外したときや、パスフレーズが漏れた可能性があるときに使います。メンバーのPCは自動で新しい鍵に切り替わります（次の報告まで最大数分かかります）。", 520));
                f.Controls.Add(t);
                rotate.Click += (s, e) =>
                {
                    string pass = TeamUi.AskPassphrase(f, "鍵を更新", "新しいパスフレーズ（" + TeamService.MinPassphraseLength + "文字以上）を入力してください。他のマスターには、新しいパスフレーズで権限を有効にし直してもらう必要があります。", true);
                    if (pass == null) return;
                    try
                    {
                        f.Cursor = Cursors.WaitCursor;
                        svc.RotateKey(pass, svc.Local.DeclaredName ?? Environment.UserName);
                        TeamUi.Info(f, "鍵を更新しました。新しいチーム確認コード：" + svc.Team.Fingerprint);
                        f.DialogResult = DialogResult.Cancel;
                        f.Close();
                    }
                    catch (TeamException ex) { TeamUi.Error(f, ex.Message); }
                    catch (Exception ex) { TeamUi.Error(f, "鍵を更新できませんでした：" + ex.Message); }
                    finally { f.Cursor = Cursors.Default; }
                };
                TeamUi.Buttons(f, "保存", () =>
                {
                    if (string.IsNullOrWhiteSpace(name.Text)) throw new TeamException("チーム名を入力してください");
                    Commit(c => { c.TeamName = name.Text.Trim(); c.DefaultScope = (ShareScope)scope.SelectedIndex; c.AutoApproveByLogin = auto.Checked; });
                    return true;
                });
                f.ShowDialog(this);
            }
            Reload();
        }

        void ReviewPcs()
        {
            using (var f = TeamUi.NewDialog("PCの承認・報告の削除", 980, 460))
            {
                f.FormBorderStyle = FormBorderStyle.Sizable;
                var list = NewGrid();
                foreach (var c in new[] { "状態", "PC", "Windowsユーザー", "申告の名前", "メール", "共有範囲", "最終報告", "PC確認コード", "エラー" }) list.Columns.Add(c, c);
                foreach (var r in reports.OrderBy(x => x.Approved).ThenBy(x => x.Machine))
                {
                    string state = r.Error != null ? "読めない" : r.KeyChanged ? "鍵が変わった（要確認）" : r.EmailsChanged ? "メールが変わった（要確認）" : r.AutoApproved ? "自動承認" : r.Approved ? "承認済み" : "承認待ち";
                    int i = list.Rows.Add(state, r.Machine, r.WindowsUser, r.DeclaredName, string.Join(", ", r.AllEmails), r.Error == null ? ScopeText.Label(r.Scope) : "",
                        r.CreatedAtUtc == DateTime.MinValue ? "" : r.CreatedAtUtc.ToLocalTime().ToString("M/d HH:mm"), r.MemberFingerprint, r.Error);
                    list.Rows[i].Tag = r;
                    if (state != "承認済み") list.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 214);
                }
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                Func<TeamReport> sel = () => list.SelectedRows.Count == 0 ? null : list.SelectedRows[0].Tag as TeamReport;
                var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.OK };
                var del = new Button { Text = "報告を削除", AutoSize = true };
                var ng = new Button { Text = "承認を取り消す", AutoSize = true };
                var ok = new Button { Text = "承認する", AutoSize = true };
                ok.Click += (s, e) =>
                {
                    var r = sel(); if (r == null || r.Error != null) return;
                    string warn = r.KeyChanged ? "【注意】以前と異なる鍵です。PCの再セットアップ以外の理由がない場合は、なりすましの可能性があります。\n\n"
                        : r.EmailsChanged ? "【注意】以前承認したときとメールアドレスが変わっています。別の人の利用量として集計されるおそれがあります。\n\n" : "";
                    bool ambiguous;
                    var member = editable == null ? null : editable.ResolveMember(r.DetectedEmails, r.DeclaredEmail, out ambiguous);
                    string msg = warn + "PC確認コード：" + r.MemberFingerprint + "\nPC：" + r.Machine + "\nWindowsユーザー：" + r.WindowsUser +
                        "\n検出メール：" + (r.DetectedEmails.Count == 0 ? "なし" : string.Join(", ", r.DetectedEmails)) + "\n申告メール：" + r.DeclaredEmail +
                        "\n集計先：" + (member != null ? member.Name : "（未登録）") +
                        "\n\nこのPCの報告を集計に含めますか？ 不明なPCの場合は、本人に確認コードを照合してから承認してください（本人のPCの「チーム」タブに表示されます）。";
                    if (MessageBox.Show(f, msg, AppInfo.Name, MessageBoxButtons.OKCancel, warn.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
                    try { svc.SetApproval(r, true); } catch (Exception ex) { TeamUi.Error(f, ex.Message); return; }
                    f.DialogResult = DialogResult.OK; f.Close();
                };
                ng.Click += (s, e) =>
                {
                    var r = sel(); if (r == null || r.Error != null) return;
                    try { svc.SetApproval(r, false); } catch (Exception ex) { TeamUi.Error(f, ex.Message); return; }
                    f.DialogResult = DialogResult.OK; f.Close();
                };
                del.Click += (s, e) =>
                {
                    var r = sel(); if (r == null) return;
                    if (MessageBox.Show(f, "報告ファイルを共有フォルダから削除しますか？（そのPCがチームに参加したままなら、次の報告で再び作られます）", AppInfo.Name, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    try { svc.DeleteReport(r); } catch (Exception ex) { TeamUi.Error(f, ex.Message); }
                    f.DialogResult = DialogResult.OK; f.Close();
                };
                bar.Controls.Add(close); bar.Controls.Add(del); bar.Controls.Add(ng); bar.Controls.Add(ok);
                f.Controls.Add(list);
                f.Controls.Add(bar);
                f.ShowDialog(this);
            }
            Reload();
        }

        void SaveCsv(DataGridView source, string baseName)
        {
            if (!store.ExportAllowed) { TeamUi.Error(this, "組織のポリシーで書き出しは無効になっています"); return; }
            if (source == promptGrid && MessageBox.Show(this, "依頼文の全文をCSVファイルに書き出します。書き出したファイルは暗号化されず、このアプリの管理外になります。保存先と共有範囲に注意してください。続けますか？", AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = baseName + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", RestoreDirectory = true })
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", source.Columns.Cast<DataGridViewColumn>().Select(c => Hardening.CsvCell(c.HeaderText))));
                foreach (DataGridViewRow row in source.Rows)
                {
                    var cells = row.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToList();
                    // 依頼文の表は、一覧の1行表示ではなく全文を書き出す
                    if (source == promptGrid && row.Tag is string) cells[cells.Count - 1] = (string)row.Tag;
                    sb.AppendLine(string.Join(",", cells.Select(Hardening.CsvCell)));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Hardening.OpenFolder(Path.GetDirectoryName(dlg.FileName), dlg.FileName);
            }
        }
    }
}
