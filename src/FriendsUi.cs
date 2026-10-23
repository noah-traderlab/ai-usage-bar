using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AIUsageBar
{
    /// <summary>フレンド機能の設定画面</summary>
    public static class FriendsUi
    {
        /// <summary>設定を開く。保存したら true</summary>
        public static bool Configure(IWin32Window owner, FriendsService svc)
        {
            if (!svc.Allowed) { TeamUi.Error(owner, "組織のポリシーでフレンド機能は無効になっています"); return false; }
            var cur = svc.Config;
            using (var f = TeamUi.NewDialog("フレンドの残り枠", 560, 600))
            {
                var t = TeamUi.Grid(2);
                TeamUi.AddFull(t, TeamUi.Note("フレンド同士で、Claude・Codex の残り枠を見せ合えます。OneDrive・Googleドライブ・Dropbox・社内サーバーなど、フレンドと共有しているフォルダを選んでください。アプリ自体は通信せず、そのフォルダに小さなファイルを書き、フレンドのファイルを読むだけです。", 520));

                TeamUi.AddFull(t, TeamUi.Heading("フレンドから招待コードをもらった場合"));
                var invite = new TextBox { Multiline = true, Height = 40, ScrollBars = ScrollBars.Vertical };
                TeamUi.AddRow(t, "招待コード", invite);

                TeamUi.AddFull(t, TeamUi.Heading("共有フォルダ"));
                var folder = new TextBox { Text = cur.Folder ?? "" };
                var browse = new Button { Text = "参照…", AutoSize = true };
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                folder.Dock = DockStyle.Fill;
                row.Controls.Add(folder, 0, 0);
                row.Controls.Add(browse, 1, 0);
                TeamUi.AddRow(t, "フォルダ", row);
                browse.Click += (s, e) =>
                {
                    using (var dlg = new FolderBrowserDialog { Description = "フレンドと共有しているフォルダを選んでください（OneDrive・Googleドライブ・Dropbox の共有フォルダ、社内サーバーなど）", ShowNewFolderButton = true })
                    {
                        if (Directory.Exists(folder.Text)) dlg.SelectedPath = folder.Text;
                        if (dlg.ShowDialog(f) == DialogResult.OK) folder.Text = dlg.SelectedPath;
                    }
                };
                invite.TextChanged += (s, e) =>
                {
                    string decoded = FriendInvite.TryDecode(invite.Text);
                    if (decoded != null) folder.Text = decoded;
                };
                TeamUi.AddFull(t, TeamUi.Note("選んだフォルダの中に「" + Friends.FolderName + "」を作ります。フォルダを共有していない人には見えません。", 520));

                var name = new TextBox { Text = string.IsNullOrWhiteSpace(cur.DisplayName) ? (TeamService.DemoUser ?? Environment.UserName) : cur.DisplayName, MaxLength = Friends.MaxNameLength };
                TeamUi.AddRow(t, "表示名", name);

                var enabled = new CheckBox { Text = "自分の残り枠をフレンドに見せる（フレンドの残り枠も表示する）", AutoSize = true, Checked = cur.Enabled || !cur.IsConfigured, Font = new Font(TeamUi.UiFont, FontStyle.Bold) };
                TeamUi.AddFull(t, enabled);
                TeamUi.AddFull(t, TeamUi.Note("見せるのは「表示名」「Claude（5時間・週間）とCodexの残り%」「Codexのリセット時刻」「更新時刻」だけです。メールアドレス・トークン数・作業内容・依頼文は書きません。オフにすると、共有フォルダから自分のファイルを削除します。", 520));

                TeamUi.AddFull(t, TeamUi.Heading("フレンドを招待する"));
                var myCode = new TextBox { ReadOnly = true, Font = new Font(FontFamily.GenericMonospace, 9f) };
                var copy = new Button { Text = "コピー", AutoSize = true };
                var codeRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
                codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                myCode.Dock = DockStyle.Fill;
                codeRow.Controls.Add(myCode, 0, 0);
                codeRow.Controls.Add(copy, 1, 0);
                TeamUi.AddRow(t, "招待コード", codeRow);
                Action updateCode = () => { myCode.Text = string.IsNullOrWhiteSpace(folder.Text) ? "" : FriendInvite.Encode(folder.Text.Trim()); };
                folder.TextChanged += (s, e) => updateCode();
                updateCode();
                copy.Click += (s, e) =>
                {
                    if (myCode.Text.Length == 0) return;
                    try { Clipboard.SetText(myCode.Text); copy.Text = "コピーしました"; } catch { }
                };
                TeamUi.AddFull(t, TeamUi.Note("先にフォルダをフレンドと共有してから、このコードを送ってください。相手のPCでフォルダの場所が違う場合（同期先のドライブ名が違うなど）は、相手が自分でフォルダを選び直します。", 520));

                f.Controls.Add(t);
                TeamUi.Buttons(f, "保存", () =>
                {
                    var next = new FriendsConfig
                    {
                        Folder = folder.Text.Trim(),
                        DisplayName = Hardening.SafeLine(name.Text, Friends.MaxNameLength),
                        Enabled = enabled.Checked,
                    };
                    if (next.Enabled)
                    {
                        if (string.IsNullOrWhiteSpace(next.Folder)) throw new TeamException("共有フォルダを選んでください");
                        if (!Directory.Exists(next.Folder)) throw new TeamException("フォルダが見つかりません：" + next.Folder);
                        if (string.IsNullOrWhiteSpace(next.DisplayName)) throw new TeamException("表示名を入力してください");
                    }
                    svc.Apply(next);
                    return true;
                });
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
