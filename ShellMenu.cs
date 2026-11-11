using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>
    /// 탐색기와 같은 Windows 셸 우클릭 메뉴 (열기, 연결 프로그램, 보내기, 복사, 삭제, 속성 등).
    /// 프로그램 자체 항목(파일 위치 열기, 경로 복사 등)은 '열기' 묶음 바로 아래에 끼워 넣는다.
    /// </summary>
    internal static class ShellMenu
    {
        public sealed class Extra
        {
            public string Text;
            public Action Action;
            public bool Enabled = true;
            public Extra(string text, Action action, bool enabled = true) { Text = text; Action = action; Enabled = enabled; }
        }

        private const int ExtraFirst = 1, ShellFirst = 1000, ShellLast = 0x7FFF;

        // 하위 메뉴(연결 프로그램, 보내기 등)를 그리려면 메뉴가 떠 있는 동안 창 메시지를 넘겨줘야 함
        private static IContextMenu2 active2;
        private static IContextMenu3 active3;

        /// <summary>메뉴 소유 창의 WndProc에서 호출. 처리했으면 true</summary>
        public static bool HandleMessage(ref Message m)
        {
            if (active2 == null && active3 == null) return false;
            const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
            switch (m.Msg)
            {
                case WM_DRAWITEM:
                case WM_MEASUREITEM:
                    if (Marshal.ReadInt32(m.LParam) != 1) return false; // ODT_MENU만
                    break;
                case WM_INITMENUPOPUP:
                case WM_MENUCHAR:
                    break;
                default:
                    return false;
            }
            try
            {
                if (active3 != null)
                {
                    if (active3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out IntPtr result) == 0)
                    {
                        m.Result = m.Msg == WM_MENUCHAR ? result : (m.Msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1);
                        return true;
                    }
                }
                else if (active2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0)
                {
                    m.Result = m.Msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>셸 메뉴 표시. 셸 메뉴를 만들 수 없으면 false (호출 측에서 대체 메뉴 표시)</summary>
        public static bool Show(Control owner, IList<string> paths, Point screen, IList<Extra> extras)
        {
            using (var sel = Selection.Create(paths))
            {
                if (sel == null) return false;
                IntPtr hmenu = CreatePopupMenu();
                try
                {
                    uint flags = CMF_NORMAL | CMF_EXPLORE;
                    if ((Control.ModifierKeys & Keys.Shift) != 0) flags |= CMF_EXTENDEDVERBS;
                    if (sel.Menu.QueryContextMenu(hmenu, 0, ShellFirst, ShellLast, flags) < 0) return false;

                    // '열기' 묶음(첫 구분선) 다음에 자체 항목 삽입
                    int count = GetMenuItemCount(hmenu);
                    int pos = count;
                    for (int i = 0; i < count; i++)
                        if ((GetMenuState(hmenu, (uint)i, MF_BYPOSITION) & MF_SEPARATOR) != 0) { pos = i; break; }
                    // 결과: [열기 묶음] ─ [자체 항목] ─(기존 구분선) [나머지 셸 항목]
                    if (pos > 0) InsertMenu(hmenu, (uint)pos++, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
                    for (int i = 0; i < extras.Count; i++)
                    {
                        var ex = extras[i];
                        if (ex == null) InsertMenu(hmenu, (uint)pos++, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
                        else InsertMenu(hmenu, (uint)pos++, MF_BYPOSITION | MF_STRING | (ex.Enabled ? 0 : MF_GRAYED), (UIntPtr)(uint)(ExtraFirst + i), ex.Text);
                    }

                    active2 = sel.Menu as IContextMenu2;
                    active3 = sel.Menu as IContextMenu3;
                    int cmd;
                    try
                    {
                        cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, owner.Handle, IntPtr.Zero);
                    }
                    finally
                    {
                        active2 = null;
                        active3 = null;
                    }

                    if (cmd >= ExtraFirst && cmd < ExtraFirst + extras.Count)
                        extras[cmd - ExtraFirst]?.Action?.Invoke();
                    else if (cmd >= ShellFirst)
                        Invoke(sel.Menu, owner, (IntPtr)(cmd - ShellFirst), null, screen);
                    return true;
                }
                finally
                {
                    DestroyMenu(hmenu);
                }
            }
        }

        /// <summary>메뉴 없이 셸 동작 실행 (예: "delete", "properties"). 성공하면 true</summary>
        public static bool InvokeVerb(Control owner, IList<string> paths, string verb, bool shift = false)
        {
            using (var sel = Selection.Create(paths))
            {
                if (sel == null) return false;
                IntPtr hmenu = CreatePopupMenu();
                try
                {
                    sel.Menu.QueryContextMenu(hmenu, 0, ShellFirst, ShellLast, CMF_NORMAL | CMF_EXPLORE);
                    return Invoke(sel.Menu, owner, IntPtr.Zero, verb, Cursor.Position, shift);
                }
                finally { DestroyMenu(hmenu); }
            }
        }

        private static bool Invoke(IContextMenu menu, Control owner, IntPtr id, string verb, Point pt, bool shift = false)
        {
            IntPtr a = IntPtr.Zero, w = IntPtr.Zero;
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = owner.Handle,
                    nShow = 1, // SW_SHOWNORMAL
                    ptInvoke = new POINT { X = pt.X, Y = pt.Y }
                };
                if (verb != null)
                {
                    a = Marshal.StringToHGlobalAnsi(verb);
                    w = Marshal.StringToHGlobalUni(verb);
                    info.lpVerb = a;
                    info.lpVerbW = w;
                }
                else
                {
                    info.lpVerb = id;
                    info.lpVerbW = id;
                }
                var mods = Control.ModifierKeys;
                if (shift || (mods & Keys.Shift) != 0) info.fMask |= CMIC_MASK_SHIFT_DOWN;
                if ((mods & Keys.Control) != 0) info.fMask |= CMIC_MASK_CONTROL_DOWN;
                return menu.InvokeCommand(ref info) >= 0;
            }
            catch { return false; }
            finally
            {
                if (a != IntPtr.Zero) Marshal.FreeHGlobal(a);
                if (w != IntPtr.Zero) Marshal.FreeHGlobal(w);
            }
        }

        /// <summary>선택한 경로들의 PIDL과 IContextMenu</summary>
        private sealed class Selection : IDisposable
        {
            public IContextMenu Menu;
            private readonly List<IntPtr> pidls = new List<IntPtr>();
            private object folder;

            public static Selection Create(IList<string> paths)
            {
                var s = new Selection();
                try
                {
                    foreach (var p in paths)
                    {
                        if (SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                            s.pidls.Add(pidl);
                    }
                    if (s.pidls.Count == 0) { s.Dispose(); return null; }

                    var iidFolder = typeof(IShellFolder).GUID;
                    var iidMenu = typeof(IContextMenu).GUID;
                    IShellFolder sf;
                    IntPtr[] children;
                    var parents = paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    if (parents == 1 && SHBindToParent(s.pidls[0], ref iidFolder, out IntPtr pf, IntPtr.Zero) == 0)
                    {
                        sf = (IShellFolder)Marshal.GetObjectForIUnknown(pf);
                        Marshal.Release(pf);
                        children = s.pidls.Select(ILFindLastID).ToArray();
                    }
                    else
                    {
                        // 서로 다른 폴더의 항목: 바탕 화면 폴더 기준 절대 PIDL 사용
                        if (SHGetDesktopFolder(out IntPtr pd) != 0) { s.Dispose(); return null; }
                        sf = (IShellFolder)Marshal.GetObjectForIUnknown(pd);
                        Marshal.Release(pd);
                        children = s.pidls.ToArray();
                    }
                    s.folder = sf;
                    if (sf.GetUIObjectOf(IntPtr.Zero, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out IntPtr pm) != 0 || pm == IntPtr.Zero)
                    {
                        s.Dispose();
                        return null;
                    }
                    s.Menu = (IContextMenu)Marshal.GetObjectForIUnknown(pm);
                    Marshal.Release(pm);
                    return s;
                }
                catch
                {
                    s.Dispose();
                    return null;
                }
            }

            public void Dispose()
            {
                if (Menu != null) { Marshal.ReleaseComObject(Menu); Menu = null; }
                if (folder != null) { Marshal.ReleaseComObject(folder); folder = null; }
                foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
                pidls.Clear();
            }
        }

        // ── Win32 / COM ─────────────────────────────────────────────
        private const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_EXTENDEDVERBS = 0x100;
        private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
                           CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
        private const uint MF_BYPOSITION = 0x400, MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 0x1;
        private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName();
            void EnumObjects();
            void BindToObject();
            void BindToStorage();
            void CompareIDs();
            void CreateViewObject();
            void GetAttributesOf();
            [PreserveSig]
            int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
            void GetDisplayNameOf();
            void SetNameOf();
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, StringBuilder name, uint cch);
        }

        [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, StringBuilder name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, StringBuilder name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, IntPtr ppidlLast);

        [DllImport("shell32.dll")]
        private static extern int SHGetDesktopFolder(out IntPtr ppshf);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hmenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InsertMenuW")]
        private static extern bool InsertMenu(IntPtr hmenu, uint position, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr hmenu);

        [DllImport("user32.dll")]
        private static extern uint GetMenuState(IntPtr hmenu, uint id, uint flags);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    }
}
