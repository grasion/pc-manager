using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>"PC 관리" 탭: 기능 카드 + 실행 로그</summary>
    internal sealed class ManagePage : UserControl
    {
        private readonly Panel flow; // 카드 영역 (직접 배치 + 자동 스크롤)
        private readonly TextBox log;

        public ManagePage()
        {
            BackColor = Theme.Bg;
            Font = Theme.Regular;
            Padding = new Padding(Theme.S(16));

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ── 왼쪽: 기능 카드 ──
            flow = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                AutoScrollMargin = new Size(0, Theme.S(12)),
                Margin = new Padding(0, 0, Theme.S(12), 0),
                BackColor = Theme.Bg
            };

            AddSection("시스템 정리");
            AddCard("시스템 복구", "DISM + SFC 시스템 파일 복구", Theme.GRepair, Theme.Accent, SystemRepair);
            AddCard("임시 파일 정리", "임시 폴더 비우기", Theme.GDelete, Color.FromArgb(220, 38, 38), TempCleanup);
            AddCard("브라우저 캐시 정리", "설치된 모든 브라우저의 캐시", Theme.GBrowser, Color.FromArgb(202, 138, 4), BrowserCleanup);
            bool hasOffice = OfficeInfo.Detect();
            if (hasOffice)
                AddCard("Office 수정", $"{OfficeInfo.Product} 빠른 복구 실행", Theme.GDoc, Color.FromArgb(234, 88, 12), OfficeRepair);

            AddSection("시스템 최적화");
            AddCard("시작 프로그램 관리", "부팅 시 자동 실행 켜기 · 끄기", Theme.GStartup, Color.FromArgb(22, 163, 74), StartupManager);
            AddCard("디스크 검사", "드라이브 오류 검사 (chkdsk)", Theme.GDisk, Color.FromArgb(124, 58, 237), DiskCheck);

            AddSection("문제 해결");
            AddCard("네트워크 초기화", "인터넷 연결 문제 해결", Theme.GSync, Color.FromArgb(2, 132, 199), NetworkReset);
            AddCard("DNS 캐시 정리", "웹 접속 문제 해결", Theme.GGlobe, Color.FromArgb(13, 148, 136), FlushDns);
            AddCard("프린터 대기열 초기화", "멈춘 인쇄 작업 삭제", Theme.GPrint, Color.FromArgb(71, 85, 105), PrinterReset);
            AddCard("시간 동기화", "PC 시계를 인터넷 시간에 맞춤", Theme.GClock, Color.FromArgb(219, 39, 119), TimeSync);

            AddSection("시스템 확인");
            AddCard("시스템 정보", "사양 · 네트워크 · 디스크", Theme.GInfo, Theme.Accent, SystemInfo);
            AddCard("IP 변경", "DHCP · 고정 IP · DNS", Theme.GNetwork, Color.FromArgb(2, 132, 199), ChangeIp);
            if (SystemTools.HasBattery())
                AddCard("배터리 리포트", "배터리 수명 리포트", Theme.GBattery, Theme.Success, BatteryReport);

            flow.Resize += (s, e) => QueueLayout();
            flow.VisibleChanged += (s, e) => QueueLayout();

            // ── 오른쪽: 실행 로그 ──
            var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.LogBg, Padding = new Padding(1), Margin = new Padding(0) };
            var logHeader = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), BackColor = Theme.LogBg, Padding = new Padding(Theme.S(12), 0, Theme.S(8), 0) };
            var logTitle = new Label { Text = "실행 로그", Dock = DockStyle.Left, AutoSize = false, Width = Theme.S(120), Font = Theme.Bold, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
            var clearBtn = LogButton("지우기");
            var copyBtn = LogButton("복사");
            logHeader.Controls.Add(logTitle);
            logHeader.Controls.Add(copyBtn);
            logHeader.Controls.Add(clearBtn);
            logHeader.Layout += (s, e) =>
            {
                int y = (logHeader.Height - clearBtn.Height) / 2;
                clearBtn.Location = new Point(logHeader.Width - logHeader.Padding.Right - clearBtn.Width, y);
                copyBtn.Location = new Point(clearBtn.Left - Theme.S(6) - copyBtn.Width, y);
            };

            log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,
                Font = new Font(Theme.FontName, 9f),
                WordWrap = true
            };
            log.HandleCreated += (s, e) => Native.SetWindowTheme(log.Handle, "DarkMode_Explorer", null); // 어두운 스크롤바
            var logInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(12), 0, Theme.S(4), Theme.S(8)), BackColor = Theme.LogBg };
            logInner.Controls.Add(log);
            logPanel.Controls.Add(logInner);
            logPanel.Controls.Add(logHeader);
            clearBtn.Click += (s, e) => log.Clear();
            copyBtn.Click += (s, e) => { if (log.TextLength > 0) Clipboard.SetText(log.Text); };

            grid.Controls.Add(flow, 0, 0);
            grid.Controls.Add(logPanel, 1, 0);
            Controls.Add(grid);

            Log(Program.IsAdmin
                ? "관리자 권한으로 실행 중입니다. 모든 기능을 바로 사용할 수 있습니다."
                : "일반 권한으로 실행 중입니다. 관리자 권한이 필요한 기능은 실행할 때 권한을 요청합니다.");
            if (!hasOffice) Log("Office(클릭투런)가 설치되어 있지 않아 'Office 수정' 기능을 숨겼습니다.");
        }

        private static Button LogButton(string text)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Theme.LogText,
                BackColor = Color.FromArgb(45, 48, 54),
                Font = Theme.Small,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 74, 82);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 64, 72);
            return b;
        }

        // ── 레이아웃 ─────────────────────────────────────────────
        private void AddSection(string title)
        {
            var l = new Label
            {
                Text = title,
                Font = Theme.Section,
                ForeColor = Theme.Text,
                AutoSize = false,
                Height = Theme.S(30),
                TextAlign = ContentAlignment.BottomLeft,
                Tag = "section"
            };
            flow.Controls.Add(l);
        }

        private void AddCard(string title, string desc, string glyph, Color color, Func<ActionCard, Task> action)
        {
            var card = new ActionCard(title, desc, glyph) { GlyphColor = color };
            card.Click += async (s, e) =>
            {
                card.Busy = true;
                try { await action(card); }
                catch (Exception ex) { Log("[오류] " + ex.Message); }
                finally { card.Busy = false; }
            };
            flow.Controls.Add(card);
        }

        /// <summary>
        /// 구역 제목과 카드를 직접 배치 (1~2열). FlowLayoutPanel은 숨김 상태로 만든 뒤 표시하면
        /// 스크롤 범위를 잘못 계산해 아래쪽 카드가 잘리는 문제가 있어 사용하지 않음.
        /// </summary>
        private bool layoutQueued;

        /// <summary>크기 변경이 끝난 뒤 한 번만 배치 (크기 변경 도중 배치하면 스크롤 범위가 갱신되지 않음)</summary>
        private void QueueLayout()
        {
            if (!flow.IsHandleCreated) { LayoutCards(); return; }
            if (layoutQueued) return;
            layoutQueued = true;
            flow.BeginInvoke(new Action(() => { layoutQueued = false; LayoutCards(); }));
        }

        private void LayoutCards()
        {
            int avail = flow.ClientSize.Width - Theme.S(2);
            if (avail <= 0) return;
            int gap = Theme.S(10);
            int cols = avail >= Theme.S(520) ? 2 : 1;
            int w = (avail - gap * cols) / cols;
            var origin = flow.AutoScrollPosition; // 스크롤된 만큼 음수
            int y = 0, col = 0, rowH = 0;
            bool first = true;
            flow.SuspendLayout();
            foreach (Control c in flow.Controls)
            {
                if (c is ActionCard)
                {
                    c.SetBounds(origin.X + col * (w + gap), origin.Y + y, w, c.Height);
                    rowH = Math.Max(rowH, c.Height);
                    if (++col == cols) { col = 0; y += rowH + gap; rowH = 0; }
                }
                else
                {
                    if (col != 0) { col = 0; y += rowH + gap; rowH = 0; }
                    if (!first) y += Theme.S(10);
                    c.SetBounds(origin.X + Theme.S(2), origin.Y + y, avail - Theme.S(4), c.Height);
                    y += c.Height + Theme.S(8);
                }
                first = false;
            }
            flow.ResumeLayout();
            // 스크롤 범위를 내용 높이로 직접 지정 (자동 계산은 열 수가 바뀔 때 이전 값이 남는 경우가 있음)
            var min = new Size(0, y + (col != 0 ? rowH : 0));
            if (flow.AutoScrollMinSize != min) flow.AutoScrollMinSize = min;
        }

        // ── 로그 ────────────────────────────────────────────────
        public void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), message); } catch (InvalidOperationException) { }
                return;
            }
            if (log.TextLength > 300_000) log.Text = log.Text.Substring(log.TextLength - 150_000);
            string text = message.Replace("\r\n", "\n").Replace("\n", "\r\n          ");
            log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
        }

        private bool Confirm(string text, string title)
        {
            return MessageBox.Show(FindForm(), text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>새 콘솔 창에서 명령 실행 (일반 권한이면 UAC 요청)</summary>
        private void StartConsole(string title, string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/k title {title} & {command} & echo. & echo ===== 작업이 끝났습니다. 이 창은 닫으셔도 됩니다. =====")
                {
                    UseShellExecute = true,
                    Verb = Program.IsAdmin ? "" : "runas"
                };
                Process.Start(psi);
                Log($"{title}: 별도 창에서 실행했습니다. 진행 상황은 해당 창에서 확인하세요.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log($"{title}: 관리자 권한 요청이 취소되었습니다.");
            }
        }

        private Task<int> Stream(string file, string args, Encoding enc)
            => Task.Run(() => SystemTools.RunStreaming(file, args, enc, line => Log("  " + line)));

        /// <summary>관리자 권한으로 cmd 명령 실행 (필요하면 UAC). -1223 = 취소</summary>
        private Task<int> Elevated(string command)
            => Task.Run(() => SystemTools.RunElevated(command, line => Log("  " + line)));

        /// <summary>선택지 대화 상자. options 각 항목은 (제목, 설명). 반환값 = 선택한 번호, 취소하면 -1</summary>
        private int Choose(string title, string heading, (string Title, string Desc)[] options, string okText)
        {
            using (var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Surface,
                Font = Theme.Regular
            })
            {
                int x = Theme.S(20), y = Theme.S(16), w = Theme.S(420);
                dlg.Controls.Add(new Label { Text = heading, Location = new Point(x, y), AutoSize = true, Font = Theme.Bold });
                y += Theme.S(34);
                var radios = new List<RadioButton>();
                foreach (var opt in options)
                {
                    var rb = new RadioButton { Text = opt.Title, Location = new Point(x, y), AutoSize = true, Checked = radios.Count == 0 };
                    dlg.Controls.Add(rb);
                    radios.Add(rb);
                    y += Theme.S(24);
                    var d = new Label { Text = opt.Desc, Location = new Point(x + Theme.S(18), y), MaximumSize = new Size(w - Theme.S(18), 0), AutoSize = true, ForeColor = Theme.SubText };
                    dlg.Controls.Add(d);
                    y += d.PreferredHeight + Theme.S(14);
                }
                var ok = Theme.FlatButton(okText, true);
                var cancel = Theme.FlatButton("취소");
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.AddRange(new Control[] { ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.ClientSize = new Size(w + Theme.S(40), y + Theme.S(52));
                dlg.Shown += (s, e) =>
                {
                    int by = dlg.ClientSize.Height - Theme.S(16) - ok.Height;
                    cancel.Location = new Point(dlg.ClientSize.Width - Theme.S(20) - cancel.Width, by);
                    ok.Location = new Point(cancel.Left - Theme.S(8) - ok.Width, by);
                };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return -1;
                return radios.FindIndex(r => r.Checked);
            }
        }

        // ── 기능 ────────────────────────────────────────────────
        private async Task SystemRepair(ActionCard card)
        {
            if (!Confirm("DISM(시스템 이미지 복구)과 SFC(시스템 파일 검사)를 차례로 실행합니다.\n" +
                         "10~30분 정도 걸릴 수 있습니다. 계속하시겠습니까?", "시스템 복구"))
                return;

            if (!Program.IsAdmin)
            {
                StartConsole("시스템 복구", "DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow");
                return;
            }

            Log("시스템 복구 시작 — 1단계: DISM 시스템 이미지 복구");
            int c1 = await Stream("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", SystemTools.OemEncoding);
            Log(c1 == 0 ? "[완료] DISM" : $"[주의] DISM 종료 코드 {c1}");
            Log("2단계: SFC 시스템 파일 검사");
            int c2 = await Stream("sfc.exe", "/scannow", Encoding.Unicode); // sfc는 UTF-16으로 출력
            Log(c2 == 0 ? "[완료] 시스템 복구가 끝났습니다." : $"[주의] SFC 종료 코드 {c2}");
        }

        private async Task TempCleanup(ActionCard card)
        {
            Log("임시 파일 정리 중…");
            var r = await Task.Run(() => SystemTools.CleanTemp());
            Log($"[완료] 파일 {r.Files:N0}개 · 폴더 {r.Dirs:N0}개 삭제, {SystemTools.FormatBytes(r.Bytes)} 확보" +
                (r.Skipped > 0 ? $" (사용 중인 파일 {r.Skipped:N0}개는 건너뜀)" : ""));
        }

        private Task OfficeRepair(ActionCard card)
        {
            if (!Confirm($"{OfficeInfo.Product} 빠른 복구를 실행합니다.\n\n" +
                         "실행 중인 Word, Excel, PowerPoint, Outlook 등은 작업을 저장한 뒤 닫아 주세요.\n" +
                         "복구 창의 안내에 따라 진행하면 됩니다. 계속하시겠습니까?", "Office 수정"))
                return Task.CompletedTask;

            if (!OfficeInfo.Detect())
            {
                Log("[오류] Office 클릭투런 실행 파일을 찾을 수 없습니다.");
                return Task.CompletedTask;
            }
            try
            {
                Process.Start(new ProcessStartInfo(OfficeInfo.ClickToRunExe,
                    $"scenario=Repair platform={OfficeInfo.Platform} culture={OfficeInfo.Culture} RepairType=QuickRepair DisplayLevel=True")
                {
                    UseShellExecute = true,
                    Verb = Program.IsAdmin ? "" : "runas"
                });
                Log($"Office 빠른 복구 창을 열었습니다. ({OfficeInfo.Platform}, {OfficeInfo.Culture})");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log("Office 수정: 관리자 권한 요청이 취소되었습니다.");
            }
            return Task.CompletedTask;
        }

        private async Task FlushDns(ActionCard card)
        {
            Log("DNS 캐시 정리 중…");
            int code = await Stream("ipconfig.exe", "/flushdns", SystemTools.OemEncoding);
            Log(code == 0 ? "[완료] DNS 캐시를 정리했습니다." : $"[주의] 종료 코드 {code}");
        }

        private Task DiskCheck(ActionCard card)
        {
            var drives = DriveInfo.GetDrives().Where(d =>
            {
                try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; }
            }).ToArray();
            if (drives.Length == 0)
            {
                Log("[오류] 검사할 수 있는 드라이브가 없습니다.");
                return Task.CompletedTask;
            }

            using (var dlg = new Form
            {
                Text = "디스크 검사",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Surface,
                Font = Theme.Regular,
                ClientSize = new Size(Theme.S(360), Theme.S(210))
            })
            {
                int x = Theme.S(20), w = Theme.S(320);
                var lbl = new Label { Text = "검사할 드라이브", Location = new Point(x, Theme.S(16)), AutoSize = true, Font = Theme.Bold };
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, Theme.S(40)), Width = w };
                foreach (var d in drives)
                {
                    string label = "";
                    try { label = d.VolumeLabel; } catch { }
                    combo.Items.Add($"{d.Name.TrimEnd('\\')}  {label}  ({SystemTools.FormatBytes(d.TotalSize)})");
                }
                combo.SelectedIndex = 0;
                var quick = new RadioButton { Text = "빠른 검사 (/scan) — 사용 중에도 실행, 재부팅 불필요", Location = new Point(x, Theme.S(80)), AutoSize = true, Checked = true };
                var full = new RadioButton { Text = "검사 및 복구 (/f /r) — 오래 걸리며 재부팅 필요할 수 있음", Location = new Point(x, Theme.S(106)), AutoSize = true };
                var ok = Theme.FlatButton("검사 시작", true);
                var cancel = Theme.FlatButton("취소");
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.AddRange(new Control[] { lbl, combo, quick, full, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.Shown += (s, e) =>
                {
                    int y = dlg.ClientSize.Height - Theme.S(16) - ok.Height;
                    cancel.Location = new Point(dlg.ClientSize.Width - Theme.S(20) - cancel.Width, y);
                    ok.Location = new Point(cancel.Left - Theme.S(8) - ok.Width, y);
                };

                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return Task.CompletedTask;
                string drive = drives[combo.SelectedIndex].Name.Substring(0, 2);
                string args = quick.Checked ? "/scan" : "/f /r";
                // chkdsk는 재부팅 예약 여부(Y/N)를 물을 수 있어 항상 콘솔 창에서 실행
                StartConsole("디스크 검사 " + drive, $"chkdsk {drive} {args}");
            }
            return Task.CompletedTask;
        }

        private async Task SystemInfo(ActionCard card)
        {
            var lines = await Task.Run(() => SystemTools.GetSystemInfo().ToList());
            Log("시스템 정보\r\n" + string.Join("\r\n", lines));
        }

        private async Task BatteryReport(ActionCard card)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "battery-report.html");
            Log("배터리 리포트 생성 중…");
            int code = await Stream("powercfg.exe", $"/batteryreport /output \"{path}\"", SystemTools.OemEncoding);
            if (code != 0 || !File.Exists(path))
            {
                Log($"[오류] 배터리 리포트를 만들지 못했습니다. (코드 {code})");
                return;
            }
            Log($"[완료] 바탕화면에 저장했습니다: {path}");
            if (Confirm("배터리 리포트가 생성되었습니다.\n지금 열어 보시겠습니까?", "배터리 리포트"))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private async Task BrowserCleanup(ActionCard card)
        {
            var browsers = await Task.Run(() => BrowserCache.Detect());
            if (browsers.Count == 0)
            {
                Log("브라우저 캐시 정리: 캐시를 정리할 브라우저를 찾지 못했습니다.");
                return;
            }
            var running = browsers.Where(b => b.Running).Select(b => b.Name).ToList();
            string msg = "다음 브라우저의 캐시(임시 인터넷 파일)를 삭제합니다.\n" +
                         "    " + string.Join(", ", browsers.Select(b => b.Name)) + "\n\n" +
                         "로그인 정보, 방문 기록, 저장된 비밀번호, 즐겨찾기는 지워지지 않습니다.";
            if (running.Count > 0)
                msg += $"\n\n실행 중: {string.Join(", ", running)}\n사용 중인 캐시 파일은 건너뜁니다. 브라우저를 모두 닫고 실행하면 더 많이 정리됩니다.";
            if (!Confirm(msg + "\n\n계속하시겠습니까?", "브라우저 캐시 정리")) return;

            Log("브라우저 캐시 정리 중…");
            long total = 0;
            foreach (var b in browsers)
            {
                var r = await Task.Run(() => BrowserCache.Clean(b));
                total += r.Bytes;
                Log($"  {b.Name} (프로필 {b.Profiles}개): {SystemTools.FormatBytes(r.Bytes)} 정리" +
                    (r.Skipped > 0 ? $", 사용 중인 파일 {r.Skipped:N0}개 건너뜀" : ""));
            }
            Log($"[완료] 브라우저 캐시 {SystemTools.FormatBytes(total)}를 정리했습니다.");
        }

        private Task StartupManager(ActionCard card)
        {
            using (var f = new StartupForm(Log)) f.ShowDialog(FindForm());
            return Task.CompletedTask;
        }

        private async Task NetworkReset(ActionCard card)
        {
            int choice = Choose("네트워크 초기화", "초기화 방법을 선택하세요", new[]
            {
                ("빠른 초기화 (IP 다시 받기)",
                 "IP 주소를 반납하고 새로 받은 뒤 DNS 캐시를 정리합니다. 재부팅이 필요 없으며 몇 초간 연결이 끊어집니다."),
                ("전체 초기화 (Winsock · TCP/IP 재설정)",
                 "네트워크 설정을 기본값으로 되돌립니다. 빠른 초기화로 해결되지 않을 때 사용하며, 재부팅해야 적용됩니다.")
            }, "초기화");
            if (choice < 0) return;

            if (choice == 1)
            {
                // 전체 초기화는 고정 IP 설정을 지울 수 있으므로 먼저 기록하고 확인
                var statics = SystemTools.GetStaticIpAdapters();
                if (statics.Count > 0)
                {
                    Log("고정 IP 설정 (전체 초기화 후 다시 입력해야 할 수 있습니다)\r\n" + string.Join("\r\n", statics));
                    if (!Confirm("고정 IP를 사용하는 네트워크가 있습니다. 전체 초기화를 하면 고정 IP 설정이 지워질 수 있습니다.\n\n" +
                                 string.Join("\n", statics) + "\n\n위 설정은 실행 로그에 기록해 두었습니다. 계속하시겠습니까?", "네트워크 초기화"))
                        return;
                }
            }

            Log(choice == 0 ? "네트워크 빠른 초기화 중… (잠시 연결이 끊어집니다)" : "네트워크 전체 초기화 중…");
            string cmd = choice == 0
                ? "ipconfig /release & ipconfig /renew & ipconfig /flushdns"
                : "netsh winsock reset & netsh int ip reset & netsh int ipv6 reset & ipconfig /flushdns";
            int code = await Elevated(cmd);
            if (code == -1223) { Log("네트워크 초기화: 관리자 권한 요청이 취소되었습니다."); return; }

            if (choice == 0)
            {
                Log(code == 0 ? "[완료] IP 주소를 새로 받았습니다." : $"[주의] 일부 어댑터는 IP를 새로 받지 못했습니다. (코드 {code})");
                return;
            }
            Log("[완료] 네트워크 설정을 초기화했습니다. 재부팅하면 적용됩니다.");
            if (Confirm("네트워크 초기화를 적용하려면 PC를 다시 시작해야 합니다.\n" +
                        "열려 있는 작업을 저장한 뒤 [예]를 누르면 10초 후 다시 시작합니다.\n\n지금 다시 시작하시겠습니까?", "네트워크 초기화"))
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10") { UseShellExecute = false, CreateNoWindow = true });
                Log("10초 후 PC를 다시 시작합니다. (취소하려면 Win+R → shutdown /a)");
            }
        }

        private async Task PrinterReset(ActionCard card)
        {
            if (!Confirm("인쇄 스풀러 서비스를 다시 시작하고, 대기 중인 모든 인쇄 작업을 삭제합니다.\n" +
                         "인쇄가 멈추거나 대기열의 문서가 지워지지 않을 때 사용하세요.\n\n계속하시겠습니까?", "프린터 대기열 초기화"))
                return;
            string spool = Path.Combine(Environment.SystemDirectory, @"spool\PRINTERS");
            Log("프린터 대기열 초기화 중…");
            int code = await Elevated($"net stop spooler /y & del /f /q \"{spool}\\*\" & net start spooler");
            if (code == -1223) { Log("프린터 대기열 초기화: 관리자 권한 요청이 취소되었습니다."); return; }
            Log(code == 0 ? "[완료] 인쇄 대기열을 비우고 스풀러를 다시 시작했습니다."
                          : $"[주의] 스풀러를 다시 시작하지 못했습니다. (코드 {code}) 서비스 상태를 확인하세요.");
        }

        private async Task TimeSync(ActionCard card)
        {
            Log($"시간 동기화 — 표준 시간대: {TimeZoneInfo.Local.DisplayName}");
            Log($"  동기화 전 PC 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var (domain, disabled) = SystemTools.GetTimeServiceState();
            string cmd = (disabled ? "sc config w32time start= demand >nul & " : "") +
                         "net start w32time >nul 2>&1 & w32tm /resync /force";
            if (!domain)
            {
                // 동기화가 실패하면(시간 서버 미설정 등) 공용 시간 서버를 지정하고 다시 시도. 도메인 PC는 설정을 건드리지 않음
                cmd += " || (w32tm /config /manualpeerlist:\"time.windows.com,0x9 time.google.com,0x9\" /syncfromflags:manual /update" +
                       " & net stop w32time >nul 2>&1 & net start w32time >nul 2>&1 & w32tm /resync /force)";
            }
            int code = await Elevated(cmd);
            if (code == -1223) { Log("시간 동기화: 관리자 권한 요청이 취소되었습니다."); return; }

            Log($"  동기화 후 PC 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            double? offset = await Task.Run(() => SystemTools.MeasureClockOffset("time.windows.com"));
            if (offset.HasValue) Log($"  인터넷 시간과의 차이: {offset.Value:0.000}초");
            Log(code == 0 ? "[완료] 시간을 동기화했습니다." : $"[주의] 시간 동기화에 실패했습니다. (코드 {code}) 인터넷 연결을 확인하세요.");
        }

        private Task ChangeIp(ActionCard card)
        {
            using (var f = new IPConfigForm(Log)) f.ShowDialog(FindForm());
            return Task.CompletedTask;
        }
    }
}
