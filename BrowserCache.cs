using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PCManagerCS
{
    /// <summary>설치된 브라우저의 캐시(임시 인터넷 파일)만 정리. 쿠키·기록·비밀번호·즐겨찾기는 건드리지 않음</summary>
    internal static class BrowserCache
    {
        public sealed class Browser
        {
            public string Name, Process;
            public int Profiles;
            public List<string> Dirs = new List<string>();
            public bool Running;
        }

        // 크로미움 계열: (표시 이름, %LOCALAPPDATA% 기준 경로, 프로세스 이름, 프로필 폴더 구조 여부)
        private static readonly (string Name, string Path, string Proc, bool UserData)[] Chromium =
        {
            ("Chrome", @"Google\Chrome\User Data", "chrome", true),
            ("Edge", @"Microsoft\Edge\User Data", "msedge", true),
            ("웨일", @"Naver\Naver Whale\User Data", "whale", true),
            ("Brave", @"BraveSoftware\Brave-Browser\User Data", "brave", true),
            ("Vivaldi", @"Vivaldi\User Data", "vivaldi", true),
            ("Chromium", @"Chromium\User Data", "chrome", true),
            ("Opera", @"Opera Software\Opera Stable", "opera", false),
            ("Opera GX", @"Opera Software\Opera GX Stable", "opera", false),
        };

        private static readonly string[] ProfileCaches = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };
        private static readonly string[] RootCaches = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };

        public static List<Browser> Detect()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var list = new List<Browser>();

            foreach (var c in Chromium)
            {
                string root = Path.Combine(local, c.Path);
                if (!Directory.Exists(root)) continue;
                var b = new Browser { Name = c.Name, Process = c.Proc };
                IEnumerable<string> profiles = c.UserData ? SafeDirs(root) : new[] { root };
                foreach (var prof in profiles)
                {
                    bool any = false;
                    foreach (var name in ProfileCaches)
                    {
                        string d = Path.Combine(prof, name);
                        if (Directory.Exists(d)) { b.Dirs.Add(d); any = true; }
                    }
                    if (any) b.Profiles++;
                }
                foreach (var name in RootCaches)
                {
                    string d = Path.Combine(root, name);
                    if (Directory.Exists(d)) b.Dirs.Add(d);
                }
                if (b.Dirs.Count > 0) list.Add(b);
            }

            string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ff))
            {
                var b = new Browser { Name = "Firefox", Process = "firefox" };
                foreach (var prof in SafeDirs(ff))
                {
                    bool any = false;
                    foreach (var name in new[] { "cache2", "startupCache", "thumbnails" })
                    {
                        string d = Path.Combine(prof, name);
                        if (Directory.Exists(d)) { b.Dirs.Add(d); any = true; }
                    }
                    if (any) b.Profiles++;
                }
                if (b.Dirs.Count > 0) list.Add(b);
            }

            foreach (var b in list)
            {
                try
                {
                    var ps = System.Diagnostics.Process.GetProcessesByName(b.Process);
                    b.Running = ps.Length > 0;
                    foreach (var p in ps) p.Dispose();
                }
                catch { }
            }
            return list;
        }

        public static SystemTools.CleanResult Clean(Browser b)
        {
            var r = new SystemTools.CleanResult();
            foreach (var d in b.Dirs) SystemTools.CleanDirectory(d, r);
            return r;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return Enumerable.Empty<string>(); }
        }
    }
}
