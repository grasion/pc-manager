using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>시스템 아이콘 목록 (확장자별 1회 조회 후 캐시 → 디스크 접근 없음)</summary>
    internal static class IconCache
    {
        private static IntPtr list;
        private static readonly Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static IntPtr SmallImageList
        {
            get
            {
                if (list == IntPtr.Zero)
                {
                    var info = new Native.SHFILEINFO();
                    list = Native.SHGetFileInfo("C:\\", 0, ref info, Marshal.SizeOf(info),
                        Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON);
                }
                return list;
            }
        }

        public static int Get(string name, bool dir)
        {
            string ext = "";
            if (!dir)
            {
                int dot = name.LastIndexOf('.');
                if (dot >= 0) ext = name.Substring(dot);
            }
            string key = dir ? "<dir>" : ext;
            if (map.TryGetValue(key, out int idx)) return idx;
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(dir ? "folder" : "file" + ext, dir ? 0x10 : 0x80, ref info, Marshal.SizeOf(info),
                Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES);
            map[key] = info.iIcon;
            return info.iIcon;
        }
    }

    /// <summary>가상 모드 결과 목록 (수백만 항목도 보이는 줄만 그림)</summary>
    internal sealed class ResultList : ListView
    {
        public ResultList()
        {
            View = View.Details;
            VirtualMode = true;
            FullRowSelect = true;
            HideSelection = false;
            DoubleBuffered = true;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            AllowColumnReorder = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x40; // LVS_SHAREIMAGELISTS: 시스템 이미지 목록을 파괴하지 않도록
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowTheme(Handle, "Explorer", null);
            var il = IconCache.SmallImageList;
            if (il != IntPtr.Zero)
                Native.SendMessage(Handle, Native.LVM_SETIMAGELIST, (IntPtr)Native.LVSIL_SMALL, il);
        }

        /// <summary>우클릭 또는 메뉴 키(Shift+F10). bool = 키보드로 연 경우</summary>
        public event Action<Point, bool> ContextMenuRequested;

        protected override void WndProc(ref Message m)
        {
            if (ShellMenu.HandleMessage(ref m)) return;
            const int WM_CONTEXTMENU = 0x7B;
            if (m.Msg == WM_CONTEXTMENU && m.WParam == Handle && ContextMenuRequested != null)
            {
                long lp = m.LParam.ToInt64();
                bool keyboard = (int)lp == -1;
                ContextMenuRequested(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)), keyboard);
                return;
            }
            base.WndProc(ref m);
        }

        public void SetSortArrow(int column, bool asc)
        {
            if (!IsHandleCreated) return;
            IntPtr header = Native.SendMessage(Handle, Native.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < Columns.Count; i++)
            {
                var item = new Native.HDITEM { mask = Native.HDI_FORMAT };
                Native.SendMessage(header, Native.HDM_GETITEM, (IntPtr)i, ref item);
                item.fmt &= ~(Native.HDF_SORTUP | Native.HDF_SORTDOWN);
                if (i == column) item.fmt |= asc ? Native.HDF_SORTUP : Native.HDF_SORTDOWN;
                Native.SendMessage(header, Native.HDM_SETITEM, (IntPtr)i, ref item);
            }
        }
    }

    internal sealed class SearchPage : UserControl
    {
        private struct Meta
        {
            public bool Ok;
            public long Size;
            public DateTime Modified;
        }

        private sealed class Category
        {
            public string Name;
            public int Type;
            public string[] Exts;
            public Category(string name, int type, string exts = null)
            {
                Name = name; Type = type;
                Exts = exts?.Split(' ');
            }
            public override string ToString() => Name;
        }

        private static readonly Category[] Categories =
        {
            new Category("전체", 0),
            new Category("폴더", 2),
            new Category("파일", 1),
            new Category("문서", 1, "doc docx docm xls xlsx xlsm xlsb ppt pptx pptm pdf hwp hwpx hwt txt csv rtf odt ods odp md xps cell show"),
            new Category("이미지", 1, "jpg jpeg png gif bmp webp heic heif tif tiff svg ico psd ai raw cr2 nef dng"),
            new Category("오디오", 1, "mp3 wav flac aac m4a ogg wma opus aiff mid"),
            new Category("비디오", 1, "mp4 avi mkv mov wmv flv webm m4v ts mts m2ts mpg mpeg 3gp"),
            new Category("압축", 1, "zip 7z rar alz egg tar gz tgz bz2 xz iso cab zst"),
            new Category("실행 파일", 1, "exe msi bat cmd ps1 com lnk vbs appx msix"),
        };

        private readonly IndexManager mgr;
        private readonly SearchBox search;
        private readonly ComboBox category;
        private readonly Button reindex;
        private readonly ResultList list;
        private readonly Label statusLeft, statusRight;
        private readonly LinkLabel adminLink;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer debounce, progressTimer;

        private FileIndex resultsIndex;
        private int[] results = new int[0];
        private Dictionary<int, Meta> meta = new Dictionary<int, Meta>();
        private CancellationTokenSource searchCts;
        private int searchSeq;
        private int sortCol;
        private bool sortAsc = true;
        private bool pendingRefresh;

        public SearchPage(IndexManager mgr)
        {
            this.mgr = mgr;
            BackColor = Theme.Bg;
            Font = Theme.Regular;

            // ── 상단: 검색창 + 분류 + 재색인 ──
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(62), BackColor = Theme.Bg, Padding = new Padding(Theme.S(16), Theme.S(12), Theme.S(16), Theme.S(12)) };
            search = new SearchBox("파일 또는 폴더 이름 검색   (예: 보고서 ext:pdf,  *.hwp,  D:\\자료\\ 계약)");
            category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Regular, FlatStyle = FlatStyle.Flat, Width = Theme.S(110) };
            category.Items.AddRange(Categories);
            category.SelectedIndex = 0;
            reindex = Theme.FlatButton("다시 색인");
            reindex.Font = Theme.Regular;
            var tip = new ToolTip();
            tip.SetToolTip(reindex, "전체 드라이브를 다시 색인합니다 (F5)");
            top.Controls.Add(search);
            top.Controls.Add(category);
            top.Controls.Add(reindex);
            top.Layout += (s, e) =>
            {
                int h = Theme.S(38);
                int y = top.Padding.Top;
                int right = top.ClientSize.Width - top.Padding.Right;
                reindex.SetBounds(right - reindex.Width, y + (h - reindex.Height) / 2, reindex.Width, reindex.Height);
                right = reindex.Left - Theme.S(8);
                category.SetBounds(right - category.Width, y + (h - category.Height) / 2, category.Width, category.Height);
                right = category.Left - Theme.S(8);
                search.SetBounds(top.Padding.Left, y, Math.Max(Theme.S(100), right - top.Padding.Left), h);
            };

            // ── 결과 목록 ──
            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
            var listOuter = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(16), 0, Theme.S(16), 0), BackColor = Theme.Bg };
            list = new ResultList { Dock = DockStyle.Fill, Font = Theme.Regular };
            list.Columns.Add("이름", Theme.S(300));
            list.Columns.Add("경로", Theme.S(420));
            list.Columns.Add("크기", Theme.S(90), HorizontalAlignment.Right);
            list.Columns.Add("수정한 날짜", Theme.S(130));
            listHost.Controls.Add(list);
            listOuter.Controls.Add(listHost);

            // ── 상태 표시줄 ──
            var status = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(32), BackColor = Theme.Bg, Padding = new Padding(Theme.S(18), 0, Theme.S(18), 0) };
            statusLeft = new Label { Dock = DockStyle.Left, AutoSize = false, AutoEllipsis = true, Width = Theme.S(520), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text };
            statusRight = new Label { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.SubText };
            adminLink = new LinkLabel
            {
                Dock = DockStyle.Right, AutoSize = true, Text = "  ⚡ 관리자 권한으로 고속 색인", TextAlign = ContentAlignment.MiddleRight,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline,
                Padding = new Padding(0, Theme.S(8), 0, 0), Visible = !Program.IsAdmin
            };
            tip.SetToolTip(adminLink, "관리자 권한이면 NTFS 마스터 파일 테이블(MFT)을 직접 읽어 수 초 안에 색인합니다.");
            status.Controls.Add(statusRight);
            status.Controls.Add(adminLink);
            status.Controls.Add(statusLeft);

            Controls.Add(listOuter);
            Controls.Add(status);
            Controls.Add(top);

            // ── 우클릭 메뉴 ──
            menu = new ContextMenuStrip { Font = Theme.Regular };
            menu.Items.Add("열기", null, (s, e) => OpenSelected());
            menu.Items.Add("폴더 열기 (Ctrl+Enter)", null, (s, e) => OpenFolder());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("전체 경로 복사 (Ctrl+C)", null, (s, e) => CopySelected(true));
            menu.Items.Add("이름 복사", null, (s, e) => CopySelected(false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("이름 바꾸기 (F2)", null, (s, e) => RenameSelected());
            menu.Items.Add("삭제 (Delete)", null, (s, e) => DeleteSelected(false));
            list.ContextMenuRequested += ShowContextMenu;

            // ── 이벤트 ──
            debounce = new System.Windows.Forms.Timer { Interval = 90 };
            debounce.Tick += (s, e) => { debounce.Stop(); RunSearch(false); };
            progressTimer = new System.Windows.Forms.Timer { Interval = 400 };
            progressTimer.Tick += (s, e) => UpdateIndexStatus();

            search.Box.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            search.Box.KeyDown += SearchBox_KeyDown;
            category.SelectedIndexChanged += (s, e) => RunSearch(false);
            reindex.Click += (s, e) => mgr.Rebuild();
            adminLink.LinkClicked += (s, e) => Program.RestartAsAdmin("search");

            list.RetrieveVirtualItem += List_RetrieveVirtualItem;
            list.DoubleClick += (s, e) => OpenSelected();
            list.KeyDown += List_KeyDown;
            list.ColumnClick += List_ColumnClick;
            list.ItemDrag += List_ItemDrag;
            list.Resize += (s, e) => FitPathColumn();

            mgr.IndexReplaced += () => SafeInvoke(() => { RunSearch(true); UpdateIndexStatus(); });
            mgr.IndexChanged += () => SafeInvoke(OnLiveChange);
            mgr.BuildStateChanged += () => SafeInvoke(() =>
            {
                if (mgr.Building) progressTimer.Start(); else progressTimer.Stop();
                UpdateIndexStatus();
            });

            statusLeft.Text = "";
            UpdateIndexStatus();
        }

        public void FocusSearch()
        {
            search.Box.Focus();
            search.Box.SelectAll();
        }

        /// <summary>창이 다시 활성화될 때 밀린 실시간 변경 반영</summary>
        public void OnActivated()
        {
            if (pendingRefresh) { pendingRefresh = false; RunSearch(true); }
        }

        public void Rebuild() => mgr.Rebuild();

        private void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        private void OnLiveChange()
        {
            var form = FindForm();
            if (Visible && form != null && Form.ActiveForm == form && form.WindowState != FormWindowState.Minimized)
                RunSearch(true);
            else
                pendingRefresh = true;
        }

        // ── 검색 실행 ───────────────────────────────────────────
        private void RunSearch(bool keepView)
        {
            var idx = mgr.Current;
            if (idx == null) { UpdateIndexStatus(); return; }

            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var token = searchCts.Token;
            int seq = ++searchSeq;
            var cat = (Category)category.SelectedItem;
            var q = SearchQuery.Parse(search.Box.Text, cat.Type, cat.Exts);
            int col = sortCol;
            bool asc = sortAsc;
            var sw = Stopwatch.StartNew();

            Task.Run(() =>
            {
                try
                {
                    var r = SearchEngine.Run(idx, q, token);
                    var m = SortResults(idx, ref r, ref col, asc, token, out string msg);
                    long ms = sw.ElapsedMilliseconds;
                    SafeInvoke(() =>
                    {
                        if (seq != searchSeq) return;
                        if (col != sortCol) { sortCol = col; list.SetSortArrow(sortCol, sortAsc); }
                        ShowResults(idx, r, m, ms, keepView, msg);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    SafeInvoke(() => statusLeft.Text = "검색 오류: " + ex.Message);
                }
            }, token);
        }

        /// <summary>결과 정렬. 이름순은 색인 순서 그대로라 비용 없음. 크기/날짜는 결과가 많으면 이름순으로 대체.</summary>
        private static Dictionary<int, Meta> SortResults(FileIndex idx, ref int[] r, ref int col, bool asc,
            CancellationToken ct, out string message)
        {
            Dictionary<int, Meta> m = null;
            message = null;
            if (col == 1)
            {
                if (r.Length <= 500_000)
                {
                    var keys = new string[r.Length];
                    for (int k = 0; k < r.Length; k++) keys[k] = idx.FolderOf(r[k]) + "\\" + idx.Names[r[k]];
                    ct.ThrowIfCancellationRequested();
                    Array.Sort(keys, r, StringComparer.OrdinalIgnoreCase);
                }
                else { col = 0; message = "결과가 너무 많아 이름순으로 표시합니다"; }
            }
            else if (col == 2 || col == 3)
            {
                if (r.Length <= 100_000)
                {
                    m = new Dictionary<int, Meta>(r.Length);
                    var keys = new long[r.Length];
                    for (int k = 0; k < r.Length; k++)
                    {
                        if ((k & 1023) == 0) ct.ThrowIfCancellationRequested();
                        int i = r[k];
                        var mm = ReadMeta(idx, i);
                        m[i] = mm;
                        keys[k] = col == 2 ? (idx.IsDir(i) ? -1 : mm.Size) : (mm.Ok ? mm.Modified.Ticks : 0);
                    }
                    Array.Sort(keys, r);
                }
                else { col = 0; message = "결과가 10만 개를 넘어 크기/날짜 정렬 대신 이름순으로 표시합니다"; }
            }
            if (!asc) Array.Reverse(r);
            return m;
        }

        private void ShowResults(FileIndex idx, int[] r, Dictionary<int, Meta> m, long ms, bool keepView, string message)
        {
            int selectedId = -1, top = 0;
            if (keepView && resultsIndex == idx && list.VirtualListSize > 0)
            {
                if (list.SelectedIndices.Count > 0 && list.SelectedIndices[0] < results.Length)
                    selectedId = results[list.SelectedIndices[0]];
                try { top = list.TopItem?.Index ?? 0; } catch { top = 0; }
            }

            bool indexSwapped = resultsIndex != idx;
            list.BeginUpdate();
            try
            {
                list.SelectedIndices.Clear();
                resultsIndex = idx;
                results = r;
                meta = m ?? new Dictionary<int, Meta>();
                list.VirtualListSize = r.Length;
                if (r.Length > 0)
                {
                    if (selectedId >= 0)
                    {
                        int pos = Array.IndexOf(r, selectedId);
                        if (pos >= 0) { list.SelectedIndices.Add(pos); list.FocusedItem = list.Items[pos]; }
                    }
                    int t = Math.Min(top, r.Length - 1);
                    list.EnsureVisible(Math.Min(r.Length - 1, t + Math.Max(0, VisibleRows() - 1)));
                    list.EnsureVisible(t);
                }
            }
            finally
            {
                list.EndUpdate();
            }
            list.Invalidate();

            statusLeft.Text = message ?? $"{r.Length:N0}개 항목   ·   {ms:N0} ms";
            UpdateIndexStatus();
            // 새 색인으로 교체된 직후: 이전 색인(수십~수백 MB)을 바로 반환
            if (indexSwapped) Task.Run(IndexManager.ReleaseMemory);
        }

        private int VisibleRows()
        {
            int rowH = Math.Max(1, list.Font.Height + Theme.S(4));
            return Math.Max(1, list.ClientSize.Height / rowH - 1);
        }

        private void UpdateIndexStatus()
        {
            var idx = mgr.Current;
            if (mgr.Building)
            {
                long n = IndexBuilder.Scanned;
                string how = Program.IsAdmin ? "MFT 읽는 중" : "폴더 탐색 중";
                statusRight.Text = idx == null
                    ? $"색인 생성 중 ({how})… {n:N0}개"
                    : $"색인 {idx.Count:N0}개 · 백그라운드 갱신 중 ({how}) {n:N0}개";
                if (idx == null) statusLeft.Text = "처음 실행 시 전체 드라이브를 색인합니다. 잠시만 기다려 주세요…";
            }
            else if (idx != null)
            {
                statusRight.Text = $"색인 {idx.Count:N0}개 · {idx.Source} · {idx.BuiltAt:HH:mm} 기준, 실시간 반영";
            }
            else
            {
                statusRight.Text = "색인 준비 중…";
            }
            reindex.Enabled = !mgr.Building;
        }

        // ── 가상 목록 ───────────────────────────────────────────
        private void List_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var r = results;
            var idx = resultsIndex;
            if (idx == null || e.ItemIndex < 0 || e.ItemIndex >= r.Length)
            {
                e.Item = new ListViewItem(new[] { "", "", "", "" });
                return;
            }
            int i = r[e.ItemIndex];
            bool dir = idx.IsDir(i);
            string name = idx.Names[i];
            if (!meta.TryGetValue(i, out var m))
            {
                m = ReadMeta(idx, i);
                if (meta.Count > 20000) meta.Clear();
                meta[i] = m;
            }
            var item = new ListViewItem(new[]
            {
                name,
                idx.FolderOf(i),
                dir || !m.Ok ? "" : FormatSize(m.Size),
                m.Ok ? m.Modified.ToString("yyyy-MM-dd HH:mm") : ""
            })
            {
                ImageIndex = IconCache.Get(name, dir)
            };
            if (!m.Ok) item.ForeColor = Theme.SubText;
            e.Item = item;
        }

        private static Meta ReadMeta(FileIndex idx, int i)
        {
            string path = idx.FullPath(i);
            if (Native.GetFileAttributesEx(@"\\?\" + path, 0, out var d))
            {
                return new Meta
                {
                    Ok = true,
                    Size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
                    Modified = DateTime.FromFileTime(d.ftLastWriteTime)
                };
            }
            return new Meta();
        }

        private static string FormatSize(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            return ((b + 1023) / 1024).ToString("N0") + " KB";
        }

        private void FitPathColumn()
        {
            if (list.Columns.Count < 4) return;
            int others = list.Columns[0].Width + list.Columns[2].Width + list.Columns[3].Width;
            int w = list.ClientSize.Width - others - 2;
            if (w > Theme.S(150)) list.Columns[1].Width = w;
        }

        private void List_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == sortCol) sortAsc = !sortAsc;
            else { sortCol = e.Column; sortAsc = e.Column < 2; } // 크기·날짜는 큰/최근 순이 기본
            list.SetSortArrow(sortCol, sortAsc);
            statusLeft.Text = "정렬 중…";
            RunSearch(false);
        }

        // ── 열기/복사/끌어놓기 ───────────────────────────────────────
        private List<string> SelectedPaths(int max = 5000)
        {
            var paths = new List<string>();
            var idx = resultsIndex;
            if (idx == null) return paths;
            foreach (int pos in list.SelectedIndices)
            {
                if (pos >= results.Length) continue;
                paths.Add(idx.FullPath(results[pos]));
                if (paths.Count >= max) break;
            }
            return paths;
        }

        private void OpenSelected()
        {
            foreach (var p in SelectedPaths(10))
            {
                try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                catch (Exception ex) { statusLeft.Text = "열 수 없음: " + ex.Message; }
            }
        }

        private void OpenFolder()
        {
            foreach (var p in SelectedPaths(10))
            {
                try { Process.Start("explorer.exe", "/select,\"" + p + "\""); }
                catch (Exception ex) { statusLeft.Text = "폴더를 열 수 없음: " + ex.Message; }
            }
        }

        private void CopySelected(bool fullPath)
        {
            var paths = SelectedPaths();
            if (paths.Count == 0) return;
            var text = string.Join("\r\n", fullPath ? paths : paths.Select(Path.GetFileName));
            try
            {
                Clipboard.SetText(text);
                statusLeft.Text = $"{paths.Count:N0}개 {(fullPath ? "경로" : "이름")}를 복사했습니다";
            }
            catch { }
        }

        // ── 우클릭 메뉴 · 삭제 · 이름 바꾸기 ─────────────────────────────
        private void ShowContextMenu(Point screen, bool keyboard)
        {
            if (list.SelectedIndices.Count == 0) return;
            if (keyboard)
            {
                var item = list.FocusedItem ?? list.Items[list.SelectedIndices[0]];
                var r = item.Bounds;
                screen = list.PointToScreen(new Point(r.Left + Theme.S(24), r.Bottom));
            }
            var paths = SelectedPaths(1001);
            var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var extras = new[]
            {
                new ShellMenu.Extra("파일 위치 열기\tCtrl+Enter", OpenFolder),
                new ShellMenu.Extra("전체 경로 복사\tCtrl+C", () => CopySelected(true)),
                new ShellMenu.Extra("이름 복사", () => CopySelected(false)),
                new ShellMenu.Extra("이름 바꾸기\tF2", RenameSelected, paths.Count == 1),
            };
            if (paths.Count > 1000 || existing.Count == 0 || !ShellMenu.Show(list, existing, screen, extras))
            {
                if (existing.Count == 0) statusLeft.Text = "선택한 항목을 찾을 수 없습니다 (이미 삭제되었거나 이동됨)";
                menu.Show(screen);
            }
        }

        private void DeleteSelected(bool permanent)
        {
            var paths = SelectedPaths(5000).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (paths.Count == 0) return;
            if (!ShellMenu.InvokeVerb(list, paths, "delete", permanent))
                statusLeft.Text = "삭제할 수 없습니다";
        }

        private void ShowProperties()
        {
            var paths = SelectedPaths(100).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (paths.Count > 0) ShellMenu.InvokeVerb(list, paths, "properties");
        }

        private void RenameSelected()
        {
            var paths = SelectedPaths(2);
            if (paths.Count != 1) return;
            string path = paths[0];
            bool dir = Directory.Exists(path);
            if (!dir && !File.Exists(path))
            {
                statusLeft.Text = "파일을 찾을 수 없습니다 (이미 삭제되었거나 이동됨)";
                return;
            }
            string oldName = Path.GetFileName(path);
            string newName = AskNewName(oldName, dir);
            if (newName == null || newName == oldName) return;

            var owner = FindForm();
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Trim('.', ' ').Length == 0)
            {
                MessageBox.Show(owner, "파일 이름에는 다음 문자를 사용할 수 없습니다.\n\\ / : * ? \" < > |", "이름 바꾸기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!dir && !string.Equals(Path.GetExtension(oldName), Path.GetExtension(newName), StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(owner, "확장명을 변경하면 파일을 사용할 수 없게 될 수도 있습니다.\n변경하시겠습니까?", "이름 바꾸기",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string dest = Path.Combine(Path.GetDirectoryName(path), newName);
            try
            {
                if (dir)
                {
                    if (string.Equals(path, dest, StringComparison.OrdinalIgnoreCase))
                    {
                        // 대소문자만 바꾸는 경우 임시 이름을 거쳐 이동
                        string tmp = dest + "~" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        Directory.Move(path, tmp);
                        Directory.Move(tmp, dest);
                    }
                    else Directory.Move(path, dest);
                }
                else File.Move(path, dest);
                statusLeft.Text = $"이름을 '{newName}'(으)로 바꿨습니다";
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "이름을 바꿀 수 없습니다.\n" + ex.Message, "이름 바꾸기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string AskNewName(string name, bool dir)
        {
            using (var dlg = new Form
            {
                Text = "이름 바꾸기",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Surface,
                Font = Theme.Regular,
                ClientSize = new Size(Theme.S(440), Theme.S(132))
            })
            {
                int x = Theme.S(20), w = Theme.S(400);
                var lbl = new Label { Text = "새 이름", Location = new Point(x, Theme.S(16)), AutoSize = true, Font = Theme.Bold };
                var box = new TextBox { Text = name, Location = new Point(x, Theme.S(42)), Width = w };
                var ok = Theme.FlatButton("확인", true);
                var cancel = Theme.FlatButton("취소");
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.Shown += (s, e) =>
                {
                    int by = dlg.ClientSize.Height - Theme.S(16) - ok.Height;
                    cancel.Location = new Point(dlg.ClientSize.Width - Theme.S(20) - cancel.Width, by);
                    ok.Location = new Point(cancel.Left - Theme.S(8) - ok.Width, by);
                    // 탐색기처럼 확장명을 뺀 부분만 선택
                    int dot = dir ? -1 : name.LastIndexOf('.');
                    box.Focus();
                    box.Select(0, dot > 0 ? dot : name.Length);
                };
                return dlg.ShowDialog(FindForm()) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        private void List_ItemDrag(object sender, ItemDragEventArgs e)
        {
            var paths = SelectedPaths(2000).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (paths.Length == 0) return;
            list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }

        // ── 키보드 ──────────────────────────────────────────────
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.PageDown)
            {
                if (list.VirtualListSize > 0)
                {
                    list.Focus();
                    if (list.SelectedIndices.Count == 0)
                    {
                        list.SelectedIndices.Add(0);
                        list.FocusedItem = list.Items[0];
                    }
                }
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                debounce.Stop();
                if (list.VirtualListSize > 0)
                {
                    if (list.SelectedIndices.Count == 0) list.SelectedIndices.Add(0);
                    if (e.Control) OpenFolder(); else OpenSelected();
                }
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                search.Box.Clear();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Alt)
            {
                ShowProperties();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                DeleteSelected(e.Shift);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F2)
            {
                RenameSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (e.Control) OpenFolder(); else OpenSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelected(true);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up && list.SelectedIndices.Count > 0 && list.SelectedIndices[0] == 0 && !e.Shift)
            {
                FocusSearch();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.Back)
            {
                FocusSearch();
                e.Handled = e.SuppressKeyPress = true;
            }
        }
    }
}
