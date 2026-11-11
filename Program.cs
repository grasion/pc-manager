using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;

namespace PCManagerCS
{
    internal static class Program
    {
        public static readonly bool IsAdmin = CheckAdmin();

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportError(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportError(e.ExceptionObject as Exception, true);
            Application.AddMessageFilter(new WheelRedirect());
            string tab = args.FirstOrDefault(a => a.StartsWith("--tab="))?.Substring(6) ?? "search";
            Application.Run(new MainForm(tab));
        }

        /// <summary>예기치 않은 오류를 %LOCALAPPDATA%\PCManagerCS\error.log 에 기록</summary>
        private static void ReportError(Exception ex, bool fatal)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(IndexManager.CachePath);
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL " : "")}{ex}\r\n\r\n");
            }
            catch { }
            if (!fatal)
                MessageBox.Show("예기치 않은 오류가 발생했습니다.\n\n" + ex?.Message, "PC 관리 도구",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static bool CheckAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>관리자 권한으로 다시 시작 (UAC 취소 시 현재 창 유지)</summary>
        public static void RestartAsAdmin(string tab)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--tab=" + tab)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Application.Exit();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 사용자가 취소
            }
        }
    }

    /// <summary>
    /// 마우스 휠을 포커스가 아닌 '커서 아래' 컨트롤로 보냄 (탐색기와 같은 동작).
    /// 이 처리가 없으면 다른 탭의 검색창에 포커스가 남아 있을 때 PC 관리 탭이 휠로 스크롤되지 않음.
    /// </summary>
    internal sealed class WheelRedirect : IMessageFilter
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
            if (m.Msg != WM_MOUSEWHEEL && m.Msg != WM_MOUSEHWHEEL) return false;
            long lp = m.LParam.ToInt64();
            var pt = new System.Drawing.Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            IntPtr target = WindowFromPoint(pt);
            if (target == IntPtr.Zero || target == m.HWnd) return false;
            if (Control.FromChildHandle(target) == null) return false; // 이 프로그램의 창이 아니면 기본 동작
            SendMessage(target, m.Msg, m.WParam, m.LParam);
            return true;
        }
    }
}
