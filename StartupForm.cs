using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PCManagerCS
{
    /// <summary>
    /// 시작 프로그램 관리. 작업 관리자 '시작 앱'과 같은 방식(StartupApproved 레지스트리)으로 켜고 끄므로
    /// 항목을 지우지 않고 언제든 되돌릴 수 있음.
    /// </summary>
    internal sealed class StartupForm : Form
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        private sealed class Entry
        {
            public string Name;        // 레지스트리 값 이름 또는 시작 폴더 파일 이름
            public string Display;     // 표시 이름
            public string Command;     // 실행 명령
            public string Target;      // 실행 파일 경로
            public bool Machine;       // 모든 사용자(HKLM)
            public string Approved;    // Run / Run32 / StartupFolder
            public string Source;      // 표시용 위치
            public bool Enabled;
        }

        private readonly Action<string> log;
        private readonly ListView list;
        private readonly Label summary;
        private readonly ImageList icons;
        private List<Entry> entries = new List<Entry>();
        private bool loading;

        public StartupForm(Action<string> log)
        {
            this.log = log;
            Text = "시작 프로그램 관리";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Surface;
            Font = Theme.Regular;
            ClientSize = new Size(Theme.S(860), Theme.S(500));
            MinimumSize = new Size(Theme.S(600), Theme.S(360));
            Padding = new Padding(Theme.S(16));

            var head = new Label
            {
                Dock = DockStyle.Top,
                Height = Theme.S(46),
                ForeColor = Theme.SubText,
                Text = "체크를 해제하면 Windows 시작 시 자동으로 실행되지 않습니다. 항목은 삭제되지 않으며, 다시 체크하면 복구됩니다.\r\n" +
                       "(작업 관리자의 '시작 앱'과 같은 설정입니다. 변경 내용은 다음 로그인부터 적용됩니다.)"
            };

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add("이름", Theme.S(200));
            list.Columns.Add("게시자", Theme.S(150));
            list.Columns.Add("상태", Theme.S(70));
            list.Columns.Add("범위", Theme.S(140));
            list.Columns.Add("명령", Theme.S(400));
            icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Theme.S(16), Theme.S(16)) };
            list.SmallImageList = icons;
            list.HandleCreated += (s, e) => Native.SetWindowTheme(list.Handle, "Explorer", null);
            FormClosed += (s, e) => icons.Dispose();
            list.ItemCheck += OnItemCheck;
            list.DoubleClick += (s, e) => OpenLocation();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(48) };
            summary = new Label { AutoSize = true, ForeColor = Theme.SubText };
            var openBtn = Theme.FlatButton("파일 위치 열기");
            var refreshBtn = Theme.FlatButton("새로 고침");
            var closeBtn = Theme.FlatButton("닫기", true);
            closeBtn.DialogResult = DialogResult.Cancel;
            CancelButton = closeBtn;
            openBtn.Click += (s, e) => OpenLocation();
            refreshBtn.Click += (s, e) => LoadEntries();
            bottom.Controls.AddRange(new Control[] { summary, openBtn, refreshBtn, closeBtn });
            bottom.Layout += (s, e) =>
            {
                int y = bottom.Height - closeBtn.Height;
                closeBtn.Location = new Point(bottom.Width - closeBtn.Width, y);
                refreshBtn.Location = new Point(closeBtn.Left - Theme.S(8) - refreshBtn.Width, y);
                openBtn.Location = new Point(0, y);
                summary.Location = new Point(openBtn.Right + Theme.S(12), y + (closeBtn.Height - summary.Height) / 2);
            };

            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(head);
            Load += (s, e) => LoadEntries();
        }

        // ── 목록 ─────────────────────────────────────────────────
        private void LoadEntries()
        {
            entries = Collect();
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            icons.Images.Clear();
            foreach (var en in entries)
            {
                string publisher = "";
                bool exists = false;
                try
                {
                    exists = !string.IsNullOrEmpty(en.Target) && File.Exists(en.Target);
                    if (exists) publisher = FileVersionInfo.GetVersionInfo(en.Target).CompanyName ?? "";
                }
                catch { }
                if (!exists) publisher = "(파일 없음)";

                var item = new ListViewItem(en.Display)
                {
                    Checked = en.Enabled,
                    Tag = en,
                    ImageIndex = exists ? IconFor(en.Target) : -1,
                    ForeColor = exists ? Theme.Text : Theme.SubText
                };
                item.SubItems.Add(publisher.Trim());
                item.SubItems.Add(en.Enabled ? "사용" : "사용 안 함");
                item.SubItems.Add(en.Source);
                item.SubItems.Add(en.Command);
                list.Items.Add(item);
            }
            list.EndUpdate();
            loading = false;
            UpdateSummary();
        }

        private int IconFor(string path)
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info),
                Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
            if (info.hIcon == IntPtr.Zero) return -1;
            try
            {
                using (var ico = Icon.FromHandle(info.hIcon))
                    icons.Images.Add(ico.ToBitmap());
                return icons.Images.Count - 1;
            }
            catch { return -1; }
            finally { Native.DestroyIcon(info.hIcon); }
        }

        private void UpdateSummary()
        {
            int on = entries.Count(e => e.Enabled);
            summary.Text = $"전체 {entries.Count}개 · 사용 {on}개 · 사용 안 함 {entries.Count - on}개";
        }

        private static List<Entry> Collect()
        {
            var result = new List<Entry>();
            using (var hku = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                ReadRun(hku, false, "Run", "현재 사용자", result);
                ReadRun(hklm64, true, "Run", "모든 사용자", result);
                if (Environment.Is64BitOperatingSystem)
                    ReadRun(hklm32, true, "Run32", "모든 사용자 (32비트)", result);
                ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), false, hku, result);
                ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true, hklm64, result);
            }
            return result.OrderBy(e => e.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void ReadRun(RegistryKey root, bool machine, string approved, string source, List<Entry> result)
        {
            try
            {
                using (var k = root.OpenSubKey(RunKey))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        string cmd = k.GetValue(name)?.ToString() ?? "";
                        result.Add(new Entry
                        {
                            Name = name,
                            Display = name,
                            Command = cmd,
                            Target = ExtractPath(cmd),
                            Machine = machine,
                            Approved = approved,
                            Source = source,
                            Enabled = IsEnabled(machine, approved, name)
                        });
                    }
                }
            }
            catch { }
        }

        private static void ReadFolder(string dir, bool machine, RegistryKey root, List<Entry> result)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { return; }
            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string target = f;
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    target = ResolveShortcut(f) ?? f;
                result.Add(new Entry
                {
                    Name = name,
                    Display = Path.GetFileNameWithoutExtension(name),
                    Command = target,
                    Target = target,
                    Machine = machine,
                    Approved = "StartupFolder",
                    Source = machine ? "모든 사용자 (시작 폴더)" : "현재 사용자 (시작 폴더)",
                    Enabled = IsEnabled(machine, "StartupFolder", name)
                });
            }
        }

        private static bool IsEnabled(bool machine, string approved, string name)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                           machine ? RegistryView.Registry64 : RegistryView.Default))
                using (var k = root.OpenSubKey(ApprovedKey + approved))
                {
                    var b = k?.GetValue(name) as byte[];
                    return b == null || b.Length == 0 || (b[0] & 1) == 0; // 홀수 = 사용 안 함
                }
            }
            catch { return true; }
        }

        /// <summary>명령줄에서 실행 파일 경로만 추출</summary>
        private static string ExtractPath(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd ?? "").Trim();
            if (cmd.Length == 0) return null;
            string path;
            if (cmd[0] == '"')
            {
                int end = cmd.IndexOf('"', 1);
                path = end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            else
            {
                int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) path = cmd.Substring(0, exe + 4);
                else
                {
                    int sp = cmd.IndexOf(' ');
                    path = sp > 0 ? cmd.Substring(0, sp) : cmd;
                }
            }
            try
            {
                if (!Path.IsPathRooted(path))
                {
                    string sys = Path.Combine(Environment.SystemDirectory, path);
                    if (!Path.HasExtension(sys)) sys += ".exe";
                    if (File.Exists(sys)) return sys;
                }
            }
            catch { }
            return path;
        }

        private static string ResolveShortcut(string lnk)
        {
            object shell = null, sc = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (sc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
                if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        // ── 켜기 / 끄기 ──────────────────────────────────────────
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (loading) return;
            var item = list.Items[e.Index];
            var en = (Entry)item.Tag;
            bool enable = e.NewValue == CheckState.Checked;
            if (enable == en.Enabled) return;

            string error = SetEnabled(en, enable);
            if (error != null)
            {
                e.NewValue = e.CurrentValue;
                if (error.Length > 0)
                    MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            en.Enabled = enable;
            item.SubItems[2].Text = enable ? "사용" : "사용 안 함";
            log($"시작 프로그램: '{en.Display}' {(enable ? "사용" : "사용 안 함")}으로 변경했습니다.");
            UpdateSummary();
        }

        /// <summary>반환값: null = 성공, "" = 사용자가 취소, 그 외 = 오류 메시지</summary>
        private static string SetEnabled(Entry en, bool enable)
        {
            var data = new byte[12];
            data[0] = (byte)(enable ? 2 : 3);
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

            if (!en.Machine || Program.IsAdmin)
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(en.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                               en.Machine ? RegistryView.Registry64 : RegistryView.Default))
                    using (var k = root.CreateSubKey(ApprovedKey + en.Approved))
                        k.SetValue(en.Name, data, RegistryValueKind.Binary);
                    return null;
                }
                catch (Exception ex) { return "설정을 바꾸지 못했습니다.\n" + ex.Message; }
            }

            // 모든 사용자 항목 + 일반 권한 → reg.exe만 관리자 권한으로 실행
            if (en.Name.IndexOfAny(new[] { '"', '%', '^', '&', '|', '<', '>' }) >= 0)
                return "이름에 특수 문자가 있어 이 항목은 관리자 권한으로 프로그램을 다시 시작한 뒤 변경해 주세요.";
            string hex = string.Concat(data.Select(b => b.ToString("X2")));
            string cmd = $"reg add \"HKLM\\{ApprovedKey}{en.Approved}\" /v \"{en.Name}\" /t REG_BINARY /d {hex} /f /reg:64";
            int code = SystemTools.RunElevated(cmd, _ => { });
            if (code == -1223) return "";
            return code == 0 ? null : $"설정을 바꾸지 못했습니다. (코드 {code})";
        }

        private void OpenLocation()
        {
            if (list.SelectedItems.Count == 0) return;
            var en = (Entry)list.SelectedItems[0].Tag;
            string path = en.Target;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                    MessageBox.Show(this, "실행 파일을 찾을 수 없습니다.\n" + en.Command, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }
    }
}
