using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace PCManagerCS
{
    /// <summary>Microsoft Office(클릭투런) 설치 감지 — 수정 기능은 클릭투런 설치에서만 동작</summary>
    internal static class OfficeInfo
    {
        public static string ClickToRunExe { get; private set; }
        public static string Platform { get; private set; } = "x64";
        public static string Culture { get; private set; } = "ko-kr";
        public static string Product { get; private set; } = "Microsoft Office";

        public static bool Detect()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
                {
                    if (k == null) return false;
                    Platform = k.GetValue("Platform") as string ?? Platform;
                    Culture = k.GetValue("ClientCulture") as string ?? Culture;
                    var ids = k.GetValue("ProductReleaseIds") as string;
                    if (!string.IsNullOrEmpty(ids)) Product = FriendlyName(ids.Split(',')[0]);
                }

                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ClickToRun\OfficeClickToRun.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), @"microsoft shared\ClickToRun\OfficeClickToRun.exe"),
                    @"C:\Program Files\Common Files\microsoft shared\ClickToRun\OfficeClickToRun.exe",
                };
                ClickToRunExe = candidates.FirstOrDefault(File.Exists);
                return ClickToRunExe != null;
            }
            catch
            {
                return false;
            }
        }

        private static string FriendlyName(string id)
        {
            if (id.StartsWith("O365", StringComparison.OrdinalIgnoreCase)) return "Microsoft 365";
            foreach (var y in new[] { "2024", "2021", "2019", "2016" })
                if (id.Contains(y)) return "Office " + y;
            return "Microsoft Office";
        }
    }

    internal static class SystemTools
    {
        /// <summary>콘솔 프로그램 출력 인코딩 (한국어 Windows = CP949)</summary>
        public static Encoding OemEncoding
        {
            get
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { return Encoding.Default; }
            }
        }

        public static bool HasBattery()
        {
            if (!Native.GetSystemPowerStatus(out var s)) return false;
            return s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = 배터리 없음, 255 = 알 수 없음
        }

        public static string FormatBytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i == 0 ? "0" : "0.0") + " " + u[i];
        }

        // ── 시스템 정보 ──────────────────────────────────────────
        public static IEnumerable<string> GetSystemInfo()
        {
            yield return "──────── 시스템 ────────";
            yield return $"컴퓨터 이름 : {Environment.MachineName}";
            yield return $"사용자      : {Environment.UserDomainName}\\{Environment.UserName}";
            yield return $"운영 체제   : {GetOsName()}";
            yield return $"프로세서    : {Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim()} ({Environment.ProcessorCount}스레드)";
            var mem = new Native.MEMORYSTATUSEX { dwLength = 64 };
            if (Native.GlobalMemoryStatusEx(ref mem))
                yield return $"메모리      : {FormatBytes(mem.ullTotalPhys)} (사용 중 {mem.dwMemoryLoad}%, 여유 {FormatBytes(mem.ullAvailPhys)})";
            var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            yield return $"가동 시간   : {(int)up.TotalDays}일 {up.Hours}시간 {up.Minutes}분";

            yield return "";
            yield return "──────── 네트워크 ────────";
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (v4.Count == 0) continue;
                yield return $"[{ni.Name}] {ni.Description}";
                foreach (var a in v4) yield return $"  IP 주소     : {a.Address} / {a.IPv4Mask}";
                var gw = props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Select(g => g.Address.ToString()).ToList();
                if (gw.Count > 0) yield return $"  게이트웨이  : {string.Join(", ", gw)}";
                var dns = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
                if (dns.Count > 0) yield return $"  DNS         : {string.Join(", ", dns)}";
                bool dhcp = false;
                try { dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { }
                yield return $"  DHCP        : {(dhcp ? "사용" : "사용 안 함 (고정 IP)")}";
                var mac = ni.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length > 0) yield return $"  MAC 주소    : {string.Join("-", mac.Select(b => b.ToString("X2")))}";
            }

            yield return "";
            yield return "──────── 디스크 ────────";
            foreach (var d in DriveInfo.GetDrives())
            {
                string line;
                try
                {
                    if (!d.IsReady) continue;
                    double used = d.TotalSize - d.TotalFreeSpace;
                    line = $"{d.Name} {(string.IsNullOrEmpty(d.VolumeLabel) ? "" : "(" + d.VolumeLabel + ") ")}[{d.DriveFormat}]  " +
                           $"전체 {FormatBytes(d.TotalSize)} · 사용 {FormatBytes(used)} ({used * 100 / d.TotalSize:0}%) · 여유 {FormatBytes(d.AvailableFreeSpace)}";
                }
                catch { continue; }
                yield return line;
            }
        }

        private static string GetOsName()
        {
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string name = Reg(key, "ProductName") ?? "Windows";
            string ver = Reg(key, "DisplayVersion") ?? Reg(key, "ReleaseId") ?? "";
            string build = Reg(key, "CurrentBuild") ?? "";
            string ubr = Reg(key, "UBR") ?? "";
            // Windows 11도 레지스트리 ProductName이 "Windows 10"으로 남아 있음
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {ver} (빌드 {build}{(ubr.Length > 0 ? "." + ubr : "")})";
        }

        private static string Reg(string path, string value)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(path))
                    return k?.GetValue(value)?.ToString();
            }
            catch { return null; }
        }

        // ── 네트워크 · 시간 ──────────────────────────────────────
        /// <summary>고정 IP를 쓰는 활성 어댑터 설정 (초기화 전 기록용)</summary>
        public static List<string> GetStaticIpAdapters()
        {
            var result = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    bool dhcp = true;
                    try { dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? true; } catch { }
                    if (dhcp) continue;
                    var v4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
                    if (v4.Count == 0) continue;
                    var gw = props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Select(g => g.Address.ToString());
                    var dns = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString());
                    result.Add($"[{ni.Name}] IP {string.Join(", ", v4.Select(a => a.Address + " / " + a.IPv4Mask))}" +
                               $" · 게이트웨이 {string.Join(", ", gw)} · DNS {string.Join(", ", dns)}");
                }
            }
            catch { }
            return result;
        }

        /// <summary>Windows 시간 서비스 상태: 도메인 시간 사용 여부, 서비스 사용 안 함 여부</summary>
        public static (bool Domain, bool Disabled) GetTimeServiceState()
        {
            string type = Reg(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "Type") ?? "";
            string start = Reg(@"SYSTEM\CurrentControlSet\Services\W32Time", "Start") ?? "";
            bool domain = type.Equals("NT5DS", StringComparison.OrdinalIgnoreCase) || type.Equals("AllSync", StringComparison.OrdinalIgnoreCase);
            return (domain, start == "4");
        }

        /// <summary>SNTP로 인터넷 시간과의 차이(초) 측정. 실패하면 null</summary>
        public static double? MeasureClockOffset(string server)
        {
            try
            {
                var addr = System.Net.Dns.GetHostAddresses(server).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (addr == null) return null;
                var data = new byte[48];
                data[0] = 0x1B; // LI=0, VN=3, Mode=3(client)
                using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    sock.ReceiveTimeout = 3000;
                    sock.SendTimeout = 3000;
                    sock.Connect(new System.Net.IPEndPoint(addr, 123));
                    var t1 = DateTime.UtcNow;
                    sock.Send(data);
                    if (sock.Receive(data) < 48) return null;
                    var t4 = DateTime.UtcNow;
                    var t2 = NtpTime(data, 32);
                    var t3 = NtpTime(data, 40);
                    return ((t2 - t1) + (t3 - t4)).TotalSeconds / 2;
                }
            }
            catch { return null; }
        }

        private static DateTime NtpTime(byte[] d, int o)
        {
            ulong sec = (ulong)d[o] << 24 | (ulong)d[o + 1] << 16 | (ulong)d[o + 2] << 8 | d[o + 3];
            ulong frac = (ulong)d[o + 4] << 24 | (ulong)d[o + 5] << 16 | (ulong)d[o + 6] << 8 | d[o + 7];
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(sec * 1000.0 + frac * 1000.0 / 4294967296.0);
        }

        // ── 임시 파일 정리 ────────────────────────────────────────
        public sealed class CleanResult
        {
            public int Files, Skipped, Dirs;
            public long Bytes;
        }

        public static CleanResult CleanTemp()
        {
            var result = new CleanResult();
            var roots = new[]
                {
                    Path.GetTempPath(),
                    Environment.GetEnvironmentVariable("TEMP"),
                    Environment.GetEnvironmentVariable("TMP"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                }
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetFullPath(p).TrimEnd('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);

            foreach (var root in roots) CleanDir(root, result);
            return result;
        }

        private static void CleanDir(string dir, CleanResult r)
        {
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).ToList(); } catch { return; }
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    long len = fi.Length;
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    r.Files++;
                    r.Bytes += len;
                }
                catch { r.Skipped++; } // 사용 중인 파일은 건너뜀
            }
            try { dirs = Directory.EnumerateDirectories(dir).ToList(); } catch { return; }
            foreach (var d in dirs)
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // 링크는 따라가지 않음
                    CleanDir(d, r);
                    Directory.Delete(d, false); // 비었을 때만 삭제됨
                    r.Dirs++;
                }
                catch { }
            }
        }

        public static void CleanDirectory(string dir, CleanResult r)
        {
            if (Directory.Exists(dir)) CleanDir(dir, r);
        }

        // ── 프로세스 실행 ──────────────────────────────────────────
        /// <summary>
        /// cmd 명령을 관리자 권한으로 실행. 관리자면 바로(실시간 출력), 아니면 UAC 요청 후 숨김 창에서 실행하고
        /// 출력은 임시 파일로 받아 전달. 반환값 = 종료 코드, -1223 = 사용자가 권한 요청 취소
        /// </summary>
        public static int RunElevated(string command, Action<string> onLine)
        {
            if (Program.IsAdmin)
                return RunStreaming("cmd.exe", "/s /c \"" + command + "\"", OemEncoding, onLine);

            string tmp = Path.Combine(Path.GetTempPath(), "pcm_" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/s /c \"(" + command + ") > \"" + tmp + "\" 2>&1\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    try
                    {
                        if (File.Exists(tmp))
                            foreach (var line in File.ReadAllLines(tmp, OemEncoding))
                                if (!string.IsNullOrWhiteSpace(line)) onLine(line.TrimEnd());
                    }
                    catch { }
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return -1223;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }


        /// <summary>출력을 실시간으로 받아오며 실행. 반환값 = 종료 코드</summary>
        public static int RunStreaming(string file, string args, Encoding enc, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (e.Data == null) return;
                    // DISM 등 진행률 표시(\r 갱신)는 마지막 상태만 표시
                    string line = e.Data.Split('\r').LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Replace("\0", "").TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) onLine(line);
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
