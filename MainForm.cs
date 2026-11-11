using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCManagerCS
{
    internal sealed class MainForm : Form
    {
        private readonly IndexManager index = new IndexManager();
        private readonly Panel content;
        private readonly NavTab tabSearch, tabManage;
        private readonly SearchPage searchPage;
        private ManagePage managePage; // 처음 열 때 생성 (시작 속도)
        private readonly PillButton updateButton;
        private Updater.Release pendingUpdate;

        public MainForm(string startTab)
        {
            Text = "PC 관리 도구";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = Theme.Regular;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.S(1100), Theme.S(720));
            MinimumSize = new Size(Theme.S(760), Theme.S(480));
            KeyPreview = true;

            // ── 상단 헤더 ──
            var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(52), BackColor = Theme.Surface };
            header.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            var title = new Label
            {
                Text = "PC 관리 도구",
                Font = Theme.Title,
                ForeColor = Theme.Text,
                AutoSize = true,
                BackColor = Theme.Surface
            };
            tabSearch = new NavTab("파일 검색", Theme.GSearch) { BackColor = Theme.Surface };
            tabManage = new NavTab("PC 관리", Theme.GSettings) { BackColor = Theme.Surface };
            Control adminInfo;
            if (Program.IsAdmin)
            {
                adminInfo = new Label { Text = "● 관리자 모드", ForeColor = Theme.Success, AutoSize = true, Font = Theme.Small, BackColor = Theme.Surface };
            }
            else
            {
                var link = new LinkLabel
                {
                    Text = "관리자 권한으로 다시 시작",
                    AutoSize = true,
                    Font = Theme.Small,
                    LinkColor = Theme.SubText,
                    ActiveLinkColor = Theme.Accent,
                    LinkBehavior = LinkBehavior.HoverUnderline,
                    BackColor = Theme.Surface
                };
                link.LinkClicked += (s, e) => Program.RestartAsAdmin(managePage != null && managePage.Visible ? "manage" : "search");
                adminInfo = link;
            }
            var coffee = new CoffeeButton { BackColor = Theme.Surface };
            updateButton = new PillButton("업데이트", Theme.Accent, Color.FromArgb(29, 78, 216), Theme.Accent, Color.White, null)
            {
                BackColor = Theme.Surface,
                Visible = false
            };
            updateButton.Click += async (s, e) => await InstallUpdate();
            header.Controls.AddRange(new Control[] { title, tabSearch, tabManage, adminInfo, coffee, updateButton });
            header.Layout += (s, e) =>
            {
                int h = header.Height;
                title.Location = new Point(Theme.S(18), (h - title.Height) / 2);
                int x = title.Right + Theme.S(24);
                tabSearch.SetBounds(x, 0, Theme.S(118), h);
                tabManage.SetBounds(tabSearch.Right, 0, Theme.S(110), h);
                adminInfo.Location = new Point(header.Width - Theme.S(18) - adminInfo.Width, (h - adminInfo.Height) / 2);
                coffee.Location = new Point(adminInfo.Left - Theme.S(16) - coffee.Width, (h - coffee.Height) / 2);
                coffee.Visible = coffee.Left > tabManage.Right + Theme.S(8); // 창이 좁으면 숨김
                var anchor = coffee.Visible ? (Control)coffee : adminInfo;
                updateButton.Location = new Point(anchor.Left - Theme.S(8) - updateButton.Width, (h - updateButton.Height) / 2);
            };

            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            searchPage = new SearchPage(index) { Dock = DockStyle.Fill };
            content.Controls.Add(searchPage);

            Controls.Add(content);
            Controls.Add(header);

            tabSearch.Click += (s, e) => ShowTab(0);
            tabManage.Click += (s, e) => ShowTab(1);

            Shown += async (s, e) =>
            {
                index.Start();
                ShowTab(startTab == "manage" ? 1 : 0);
                await CheckUpdate();
            };
            Activated += (s, e) => searchPage.OnActivated();
            FormClosing += (s, e) => index.Dispose();
        }

        // ── 자동 업데이트 (GitHub Release) ─────────────────────────────
        private async Task CheckUpdate()
        {
            Updater.CleanupOld();
            await Task.Delay(3000); // 시작 직후 색인 로딩과 겹치지 않게
            var rel = await Updater.CheckAsync();
            if (rel == null || IsDisposed) return;
            pendingUpdate = rel;
            updateButton.Text = $"⬆  새 버전 {rel.Tag}";
            new ToolTip().SetToolTip(updateButton, $"현재 v{Updater.Current} → {rel.Tag}. 눌러서 업데이트");
            updateButton.Visible = true;
            updateButton.Parent.PerformLayout();
        }

        private async Task InstallUpdate()
        {
            var rel = pendingUpdate;
            if (rel == null || !updateButton.Enabled) return;
            string notes = string.IsNullOrWhiteSpace(rel.Notes) ? ""
                : "\n\n변경 내용:\n" + (rel.Notes.Length > 600 ? rel.Notes.Substring(0, 600) + "…" : rel.Notes);
            var answer = MessageBox.Show(this,
                $"새 버전 {rel.Tag}이(가) 있습니다. (현재 v{Updater.Current}){notes}\n\n" +
                "지금 업데이트하고 프로그램을 다시 시작할까요?\n[아니요]를 누르면 릴리스 페이지를 엽니다.",
                "업데이트", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (answer == DialogResult.No) { Updater.OpenPage(rel); return; }
            if (answer != DialogResult.Yes) return;

            string label = updateButton.Text;
            updateButton.Enabled = false;
            SetUpdateText("내려받는 중…");
            bool restarting = await Updater.InstallAsync(rel, this, p => BeginInvoke(new Action(() => SetUpdateText($"내려받는 중 {p}%"))));
            if (restarting) { Close(); return; }
            SetUpdateText(label);
            updateButton.Enabled = true;
        }

        private void SetUpdateText(string text)
        {
            if (updateButton.Text == text) return;
            updateButton.Text = text;
            updateButton.Parent.PerformLayout();
        }

        private void ShowTab(int i)
        {
            if (i == 1 && managePage == null)
            {
                managePage = new ManagePage { Dock = DockStyle.Fill, Visible = false };
                content.Controls.Add(managePage);
            }
            tabSearch.Selected = i == 0;
            tabManage.Selected = i == 1;
            searchPage.Visible = i == 0;
            if (managePage != null) managePage.Visible = i == 1;
            if (i == 0) searchPage.FocusSearch();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.F:
                case Keys.Control | Keys.L:
                case Keys.Control | Keys.D1:
                    ShowTab(0);
                    return true;
                case Keys.Control | Keys.D2:
                    ShowTab(1);
                    return true;
                case Keys.F5:
                    if (searchPage.Visible) searchPage.Rebuild();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
