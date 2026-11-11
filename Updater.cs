using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>
    /// GitHub Release 자동 업데이트. 시작할 때 최신 릴리스를 확인하고, 새 버전이면 헤더에 버튼을 표시.
    /// 누르면 exe를 내려받아(SHA-256 확인) 실행 중인 exe와 바꾼 뒤 다시 시작한다.
    /// 배포는 release.ps1 이 담당 (버전 올리기 → 빌드 → 커밋/태그/푸시 → Release에 exe 업로드).
    /// </summary>
    internal static class Updater
    {
        public static string Repo = "grasion/pc-manager"; // release.ps1 의 $Repo 와 같아야 함
        private const string AssetName = "PCManagerCS.exe";

        public sealed class Release
        {
            public Version Version;
            public string Tag, PageUrl, DownloadUrl, Sha256, Notes;
            public long Size;
        }

        public static Version Current
        {
            get
            {
                var v = typeof(Updater).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static string ExePath => Application.ExecutablePath;

        /// <summary>지난 업데이트에서 남은 이전 exe 정리</summary>
        public static void CleanupOld()
        {
            try { File.Delete(ExePath + ".old"); } catch { }
            try { File.Delete(ExePath + ".new"); } catch { }
        }

        /// <summary>최신 릴리스가 현재보다 새로우면 반환, 아니면(또는 확인 실패 시) null</summary>
        public static async Task<Release> CheckAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string json;
                using (var wc = NewClient(true))
                    json = await wc.DownloadStringTaskAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);

                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return null;
                string tag = root.TryGetValue("tag_name", out var t) ? t as string : null;
                if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
                ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
                if (ver <= Current) return null;

                var rel = new Release
                {
                    Version = ver,
                    Tag = tag,
                    PageUrl = root.TryGetValue("html_url", out var u) ? u as string : $"https://github.com/{Repo}/releases/latest",
                    Notes = root.TryGetValue("body", out var b) ? b as string : ""
                };
                if (root.TryGetValue("assets", out var a) && a is IEnumerable assets)
                {
                    foreach (var o in assets.OfType<Dictionary<string, object>>())
                    {
                        if (!string.Equals(o.TryGetValue("name", out var n) ? n as string : null, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                        rel.DownloadUrl = o.TryGetValue("browser_download_url", out var d) ? d as string : null;
                        rel.Size = o.TryGetValue("size", out var s) ? Convert.ToInt64(s) : 0;
                        var digest = o.TryGetValue("digest", out var g) ? g as string : null; // "sha256:..."
                        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            rel.Sha256 = digest.Substring(7).ToLowerInvariant();
                    }
                }
                return rel;
            }
            catch
            {
                return null; // 오프라인, 저장소 없음 등은 조용히 무시
            }
        }

        /// <summary>
        /// 새 exe를 내려받아 교체하고 다시 시작. 폴더에 쓸 수 없으면 Release 페이지를 연다.
        /// 반환값: true = 재시작 진행(프로그램 종료 예정)
        /// </summary>
        public static async Task<bool> InstallAsync(Release rel, IWin32Window owner, Action<int> progress)
        {
            if (string.IsNullOrEmpty(rel.DownloadUrl))
            {
                OpenPage(rel);
                return false;
            }

            string exe = ExePath, tmp = exe + ".new";
            try
            {
                using (var wc = NewClient())
                {
                    wc.DownloadProgressChanged += (s, e) => progress?.Invoke(e.ProgressPercentage);
                    await wc.DownloadFileTaskAsync(new Uri(rel.DownloadUrl), tmp);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex.InnerException is UnauthorizedAccessException)
            {
                // 설치 폴더에 쓸 권한이 없음 (예: Program Files) → 브라우저로 받도록 안내
                MessageBox.Show(owner, "이 폴더에는 프로그램을 바꿀 권한이 없어 자동 업데이트를 할 수 없습니다.\n" +
                                       "열리는 페이지에서 새 버전을 내려받아 주세요.", "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenPage(rel);
                return false;
            }

            string error = Verify(tmp, rel);
            if (error != null)
            {
                try { File.Delete(tmp); } catch { }
                MessageBox.Show(owner, "내려받은 파일을 확인하지 못해 업데이트를 취소했습니다.\n" + error, "업데이트",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                // 실행 중인 exe는 지울 수 없지만 이름은 바꿀 수 있음 → 새 exe를 원래 이름으로
                try { File.Delete(exe + ".old"); } catch { }
                File.Move(exe, exe + ".old");
                try { File.Move(tmp, exe); }
                catch { File.Move(exe + ".old", exe); throw; }
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                MessageBox.Show(owner, "프로그램 파일을 바꾸지 못했습니다.\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a);
            Process.Start(new ProcessStartInfo(exe, string.Join(" ", args)) { UseShellExecute = true });
            return true;
        }

        private static string Verify(string file, Release rel)
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length < 1024) return "파일이 비어 있습니다.";
            if (rel.Size > 0 && fi.Length != rel.Size) return "파일 크기가 다릅니다.";
            using (var fs = File.OpenRead(file))
            {
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return "실행 파일 형식이 아닙니다.";
                if (rel.Sha256 != null)
                {
                    fs.Position = 0;
                    using (var sha = SHA256.Create())
                    {
                        string hash = string.Concat(sha.ComputeHash(fs).Select(x => x.ToString("x2")));
                        if (hash != rel.Sha256) return "파일 해시(SHA-256)가 일치하지 않습니다.";
                    }
                }
            }
            return null;
        }

        public static void OpenPage(Release rel)
        {
            try { Process.Start(new ProcessStartInfo(rel.PageUrl) { UseShellExecute = true }); } catch { }
        }

        private static WebClient NewClient(bool api = false)
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "PCManagerCS-Updater";
            if (api) wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }
    }
}
