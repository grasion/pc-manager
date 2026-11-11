using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCManagerCS
{
    /// <summary>
    /// 이름/부모/플래그를 병렬 배열로 저장하는 가벼운 항목 저장소.
    /// 전체 경로는 저장하지 않고 부모 체인으로 필요할 때만 조립하여 메모리를 아낀다.
    /// </summary>
    internal class EntryStore
    {
        public const byte FDir = 1, FDeleted = 2;

        public string[] Names;
        public int[] Parents;   // -1 = 최상위(드라이브 루트)
        public byte[] Flags;
        public int Count;

        public EntryStore(int capacity = 1024)
        {
            Names = new string[capacity];
            Parents = new int[capacity];
            Flags = new byte[capacity];
        }

        protected virtual void EnsureCapacity(int min)
        {
            if (min <= Names.Length) return;
            int cap = Math.Max(min, Names.Length * 2);
            // 새 배열을 만든 뒤 참조만 교체 → 읽는 쪽은 이전 배열을 계속 안전하게 사용 가능
            var n = new string[cap]; Array.Copy(Names, n, Count);
            var p = new int[cap]; Array.Copy(Parents, p, Count);
            var f = new byte[cap]; Array.Copy(Flags, f, Count);
            Names = n; Parents = p; Flags = f;
        }

        public int Add(string name, int parent, byte flags)
        {
            if (Count == Names.Length) EnsureCapacity(Count + 1);
            Names[Count] = name;
            Parents[Count] = parent;
            Flags[Count] = flags;
            return Count++;
        }

        /// <summary>다른 저장소를 덧붙인다. chunk 안에서 부모가 -1인 항목은 attachTo 아래로 연결.</summary>
        public void Append(EntryStore chunk, int attachTo)
        {
            EnsureCapacity(Count + chunk.Count);
            int off = Count;
            Array.Copy(chunk.Names, 0, Names, off, chunk.Count);
            Array.Copy(chunk.Flags, 0, Flags, off, chunk.Count);
            for (int j = 0; j < chunk.Count; j++)
            {
                int p = chunk.Parents[j];
                Parents[off + j] = p < 0 ? attachTo : p + off;
            }
            Count += chunk.Count;
        }
    }

    /// <summary>검색 가능한 전체 파일 색인</summary>
    internal sealed class FileIndex : EntryStore
    {
        public readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim();
        public readonly List<int> Roots = new List<int>();
        public int[] NameOrder = new int[0];   // 이름순 정렬 순서 (검색 결과가 자동으로 이름순)
        public int SortedCount;
        public string Source = "";              // "MFT" / "폴더 탐색" 등
        public bool AnyDeleted;
        public DateTime BuiltAt = DateTime.Now;

        private string[] dirCache;              // 폴더 경로 캐시 (폴더 항목만 채워짐)
        private int[] buckets, next;            // 이름 해시 (실시간 갱신용, 필요할 때만 생성)
        private int mask;

        public FileIndex(int capacity = 1024) : base(capacity) { }

        protected override void EnsureCapacity(int min)
        {
            base.EnsureCapacity(min);
            if (next != null && next.Length < Names.Length)
            {
                var nn = new int[Names.Length];
                Array.Copy(next, nn, next.Length);
                next = nn;
            }
        }

        public bool IsDir(int i) => (Flags[i] & FDir) != 0;

        // ── 경로 ──────────────────────────────────────────────
        public string DirPath(int i)
        {
            var cache = dirCache;
            if (cache != null && i < cache.Length)
            {
                var s = cache[i];
                if (s != null) return s;
            }
            int p = Parents[i];
            string r = p < 0 ? Names[i] : DirPath(p) + "\\" + Names[i];
            if (cache != null && i < cache.Length) cache[i] = r;
            return r;
        }

        public string FullPath(int i)
        {
            int p = Parents[i];
            return p < 0 ? Names[i] + "\\" : DirPath(p) + "\\" + Names[i];
        }

        /// <summary>목록의 "경로" 열에 표시할 상위 폴더 경로</summary>
        public string FolderOf(int i)
        {
            int p = Parents[i];
            if (p < 0) return "";
            return Parents[p] < 0 ? Names[p] + "\\" : DirPath(p);
        }

        public bool AncestorsAlive(int i)
        {
            for (int p = Parents[i]; p >= 0; p = Parents[p])
                if ((Flags[p] & FDeleted) != 0) return false;
            return true;
        }

        public void Finish()
        {
            dirCache = new string[Names.Length];
            if (SortedCount == 0) BuildNameOrder();
        }

        public void BuildNameOrder()
        {
            int n = Count;
            var keys = new string[n];
            Array.Copy(Names, keys, n);
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(keys, order, StringComparer.OrdinalIgnoreCase);
            NameOrder = order;
            SortedCount = n;
        }

        // ── 실시간 갱신용 조회/수정 (쓰기 잠금 상태에서 호출) ─────────────
        private static int Hash(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(s) & 0x7FFFFFFF;

        private void EnsureHash()
        {
            if (buckets != null) return;
            int size = 1024;
            while (size < Count) size <<= 1;
            buckets = new int[size];
            for (int b = 0; b < size; b++) buckets[b] = -1;
            mask = size - 1;
            next = new int[Names.Length];
            HashRange(0, Count);
        }

        private void HashRange(int from, int to)
        {
            if (buckets == null) return;
            for (int i = from; i < to; i++)
            {
                int b = Hash(Names[i]) & mask;
                next[i] = buckets[b];
                buckets[b] = i;
            }
        }

        private void Unhash(int i)
        {
            if (buckets == null) return;
            int b = Hash(Names[i]) & mask;
            if (buckets[b] == i) { buckets[b] = next[i]; return; }
            for (int c = buckets[b]; c >= 0; c = next[c])
                if (next[c] == i) { next[c] = next[i]; return; }
        }

        /// <summary>전체 경로로 항목 찾기 (없으면 -1)</summary>
        public int Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            path = path.TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':')
            {
                foreach (int r in Roots)
                    if (string.Equals(Names[r], path, StringComparison.OrdinalIgnoreCase)) return r;
                return -1;
            }
            int slash = path.LastIndexOf('\\');
            if (slash < 0) return -1;
            string name = path.Substring(slash + 1);
            string dir = path.Substring(0, slash);
            EnsureHash();
            for (int c = buckets[Hash(name) & mask]; c >= 0; c = next[c])
            {
                if ((Flags[c] & FDeleted) != 0) continue;
                if (!string.Equals(Names[c], name, StringComparison.OrdinalIgnoreCase)) continue;
                int p = Parents[c];
                if (p >= 0 && string.Equals(DirPath(p), dir, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return -1;
        }

        public int AddLive(string name, int parent, bool dir)
        {
            EnsureHash();
            int i = Add(name, parent, dir ? FDir : (byte)0);
            HashRange(i, i + 1);
            return i;
        }

        public void AppendLive(EntryStore chunk, int attachTo)
        {
            EnsureHash();
            int from = Count;
            Append(chunk, attachTo);
            HashRange(from, Count);
        }

        public void Rename(int i, string newName)
        {
            EnsureHash();
            Unhash(i);
            Names[i] = newName;
            HashRange(i, i + 1);
            if (IsDir(i)) dirCache = new string[Names.Length]; // 하위 경로가 바뀌므로 캐시 초기화
        }

        public void MarkDeleted(int i)
        {
            Flags[i] |= FDeleted;
            AnyDeleted = true;
        }

        // ── 캐시 파일 저장/불러오기 ───────────────────────────────
        private const string Magic = "PCMIDX3";

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Source ?? "");
                w.Write(BuiltAt.ToBinary());
                w.Write(Count);
                w.Write(Roots.Count);
                foreach (int r in Roots) w.Write(r);
                for (int i = 0; i < Count; i++)
                {
                    w.Write(Names[i]);
                    w.Write(Parents[i]);
                    w.Write(Flags[i]);
                }
                w.Write(SortedCount);
                for (int i = 0; i < SortedCount; i++) w.Write(NameOrder[i]);
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        public static FileIndex Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadString() != Magic) return null;
                    string source = r.ReadString();
                    var built = DateTime.FromBinary(r.ReadInt64());
                    int count = r.ReadInt32();
                    if (count < 0 || count > 100_000_000) return null;
                    var idx = new FileIndex(Math.Max(1024, count)) { Source = source, BuiltAt = built };
                    int roots = r.ReadInt32();
                    for (int k = 0; k < roots; k++) idx.Roots.Add(r.ReadInt32());
                    var pool = new NamePool();
                    for (int i = 0; i < count; i++)
                    {
                        idx.Names[i] = pool.Get(r.ReadString());
                        idx.Parents[i] = r.ReadInt32();
                        idx.Flags[i] = r.ReadByte();
                    }
                    idx.Count = count;
                    int sorted = r.ReadInt32();
                    if (sorted != count) return null;
                    var order = new int[sorted];
                    for (int i = 0; i < sorted; i++) order[i] = r.ReadInt32();
                    idx.NameOrder = order;
                    idx.SortedCount = sorted;
                    idx.Finish();
                    return idx;
                }
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>색인 생성기: 관리자면 NTFS MFT 직접 읽기(수 초), 아니면 폴더 탐색</summary>
    internal static class IndexBuilder
    {
        public static long Scanned; // 진행 상황 표시용

        public static FileIndex Build(bool tryMft, CancellationToken ct)
        {
            Interlocked.Exchange(ref Scanned, 0);
            var drives = DriveInfo.GetDrives().Where(d =>
            {
                try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; }
            }).ToArray();

            var chunks = new EntryStore[drives.Length];
            var usedMft = new bool[drives.Length];

            Parallel.For(0, drives.Length, new ParallelOptions { CancellationToken = ct }, k =>
            {
                string letter = drives[k].Name.Substring(0, 2);
                EntryStore c = null;
                if (tryMft)
                {
                    string fmt = "";
                    try { fmt = drives[k].DriveFormat; } catch { }
                    if (fmt == "NTFS")
                    {
                        try { c = MftReader.Read(letter, ct); } catch (OperationCanceledException) { throw; } catch { c = null; }
                        usedMft[k] = c != null;
                    }
                }
                if (c == null) c = Walker.WalkDrive(letter, ct);
                chunks[k] = c;
            });

            int total = chunks.Sum(c => c.Count) + drives.Length;
            var idx = new FileIndex(Math.Max(1024, total));
            for (int k = 0; k < drives.Length; k++)
            {
                int root = idx.Add(drives[k].Name.Substring(0, 2), -1, EntryStore.FDir);
                idx.Roots.Add(root);
                idx.Append(chunks[k], root);
                chunks[k] = null;
            }
            int mft = usedMft.Count(b => b);
            idx.Source = mft == drives.Length && mft > 0 ? "MFT 고속 색인" : mft > 0 ? "MFT + 폴더 탐색" : "폴더 탐색";
            idx.BuiltAt = DateTime.Now;
            ct.ThrowIfCancellationRequested();
            idx.Finish();
            return idx;
        }
    }

    /// <summary>NTFS 마스터 파일 테이블을 FSCTL_ENUM_USN_DATA로 직접 열거 (관리자 권한 필요)</summary>
    internal static class MftReader
    {
        private const ulong FrnMask = 0x0000FFFFFFFFFFFF;

        public static unsafe EntryStore Read(string letter, CancellationToken ct)
        {
            var frns = new List<ulong>(1 << 16);
            var pfrns = new List<ulong>(1 << 16);
            var names = new List<string>(1 << 16);
            var dirs = new List<bool>(1 << 16);
            var pool = new NamePool();

            using (var h = Native.CreateFile(@"\\.\" + letter, Native.GENERIC_READ,
                       Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var med = new Native.MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
                const int BufSize = 1 << 20;
                IntPtr buf = Marshal.AllocHGlobal(BufSize);
                int reported = 0;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Native.DeviceIoControl(h, Native.FSCTL_ENUM_USN_DATA, ref med, sizeof(Native.MFT_ENUM_DATA_V0),
                                buf, BufSize, out int ret, IntPtr.Zero))
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (err == Native.ERROR_HANDLE_EOF) break;
                            return null; // 중간 실패 시 불완전한 색인 대신 폴더 탐색으로 대체
                        }
                        if (ret <= 8) break;
                        byte* p = (byte*)buf;
                        int off = 8;
                        while (off < ret)
                        {
                            byte* rec = p + off;
                            int len = *(int*)rec;
                            if (len <= 0) break;
                            if (*(ushort*)(rec + 4) == 2)
                            {
                                ulong frn = *(ulong*)(rec + 8);
                                if ((frn & FrnMask) >= 16) // 0~15번은 $MFT 등 NTFS 메타데이터
                                {
                                    uint attr = *(uint*)(rec + 52);
                                    int nlen = *(ushort*)(rec + 56);
                                    int noff = *(ushort*)(rec + 58);
                                    frns.Add(frn);
                                    pfrns.Add(*(ulong*)(rec + 16));
                                    names.Add(pool.Get(new string((char*)(rec + noff), 0, nlen / 2)));
                                    dirs.Add((attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0);
                                }
                            }
                            off += len;
                        }
                        med.StartFileReferenceNumber = *(ulong*)p;
                        Interlocked.Add(ref IndexBuilder.Scanned, frns.Count - reported);
                        reported = frns.Count;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            int n = frns.Count;

            // 부모 연결: FRN → 인덱스 (폴더만)
            var map = new Dictionary<ulong, int>(n / 6 + 16);
            for (int i = 0; i < n; i++) if (dirs[i]) map[frns[i]] = i;

            var par = new int[n]; // -1 = 루트, -2 = 연결 불가
            for (int i = 0; i < n; i++)
            {
                if (map.TryGetValue(pfrns[i], out int j)) par[i] = j;
                else par[i] = (pfrns[i] & FrnMask) == 5 ? -1 : -2;
            }
            map = null;

            // 루트까지 연결되는 항목만 유지 (고아/순환 제거)
            var state = new sbyte[n]; // 0 미확인, 1 정상, -1 제외, 2 확인 중
            var stack = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (state[i] != 0) continue;
                stack.Clear();
                int x = i;
                sbyte res;
                while (true)
                {
                    if (state[x] != 0) { res = state[x] == 2 ? (sbyte)-1 : state[x]; break; }
                    state[x] = 2;
                    stack.Add(x);
                    int q = par[x];
                    if (q == -1) { res = 1; break; }
                    if (q == -2) { res = -1; break; }
                    x = q;
                }
                foreach (int s in stack) state[s] = res;
            }

            var newIdx = new int[n];
            int cnt = 0;
            for (int i = 0; i < n; i++) newIdx[i] = state[i] == 1 ? cnt++ : -1;

            var store = new EntryStore(Math.Max(16, cnt));
            for (int i = 0; i < n; i++)
            {
                if (state[i] != 1) continue;
                int p = par[i];
                store.Add(names[i], p < 0 ? -1 : newIdx[p], dirs[i] ? EntryStore.FDir : (byte)0);
            }
            return store;
        }
    }

    /// <summary>같은 이름(obj, Release, index.js …)은 문자열 하나를 공유 → 메모리 절약</summary>
    internal sealed class NamePool
    {
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string s)
        {
            if (map.TryGetValue(s, out var e)) return e;
            map[s] = s;
            return s;
        }
    }

    /// <summary>FindFirstFileEx 기반 폴더 탐색 (일반 사용자 권한). 여러 스레드가 폴더 큐를 나눠 처리.</summary>
    internal static class Walker
    {
        private const int WorkerShift = 27, LocalMask = (1 << WorkerShift) - 1;

        public static EntryStore WalkDrive(string letter, CancellationToken ct)
        {
            int workers = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            var stores = new EntryStore[workers];
            var queue = new System.Collections.Concurrent.ConcurrentStack<(string path, int enc)>();
            queue.Push((letter, -1));
            int pending = 1;     // 큐에 있거나 처리 중인 폴더 수
            Exception error = null;

            var threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int id = w;
                threads[w] = new Thread(() =>
                {
                    var store = stores[id] = new EntryStore(4096);
                    var pool = new NamePool();
                    var subdirs = new List<(string path, int idx)>();
                    var spin = new SpinWait();
                    try
                    {
                        while (true)
                        {
                            if (ct.IsCancellationRequested) return;
                            if (queue.TryPop(out var item))
                            {
                                spin.Reset();
                                subdirs.Clear();
                                ListDir(item.path, item.enc, store, subdirs, pool);
                                foreach (var sd in subdirs) queue.Push((sd.path, (id << WorkerShift) | sd.idx));
                                Interlocked.Add(ref pending, subdirs.Count - 1);
                            }
                            else if (Volatile.Read(ref pending) == 0) return;
                            else spin.SpinOnce();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        Interlocked.Exchange(ref pending, 0);
                    }
                }) { IsBackground = true, Priority = Thread.CurrentThread.Priority };
                threads[w].Start();
            }
            foreach (var t in threads) t.Join();
            ct.ThrowIfCancellationRequested();
            if (error != null) throw error;

            // 스레드별 저장소 병합: 인코딩된 부모(스레드 번호 + 로컬 인덱스)를 전역 인덱스로 변환
            var off = new int[workers];
            int total = 0;
            for (int w = 0; w < workers; w++) { off[w] = total; total += stores[w].Count; }
            var main = new EntryStore(Math.Max(16, total));
            for (int w = 0; w < workers; w++)
            {
                var st = stores[w];
                for (int j = 0; j < st.Count; j++)
                {
                    int enc = st.Parents[j];
                    main.Add(st.Names[j], enc < 0 ? -1 : off[enc >> WorkerShift] + (enc & LocalMask), st.Flags[j]);
                }
                stores[w] = null;
            }
            return main;
        }

        /// <summary>dir 아래 전체를 c에 추가 (dir 자신은 추가하지 않음, 최상위 항목의 부모 = -1)</summary>
        public static void WalkTree(string dir, EntryStore c, CancellationToken ct)
        {
            var stack = new Stack<(string, int)>();
            var subdirs = new List<(string path, int idx)>();
            stack.Push((dir, -1));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (path, idx) = stack.Pop();
                subdirs.Clear();
                ListDir(path, idx, c, subdirs, null);
                foreach (var s in subdirs) stack.Push(s);
            }
        }

        private static void ListDir(string path, int parentIdx, EntryStore c, List<(string path, int idx)> subdirs, NamePool pool)
        {
            IntPtr h = Native.FindFirstFileEx(@"\\?\" + path + @"\*", Native.FindExInfoBasic, out var data, 0,
                IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE) return;
            int added = 0;
            try
            {
                do
                {
                    string name = data.cFileName;
                    if (name == "." || name == "..") continue;
                    bool dir = (data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    int i = c.Add(pool != null ? pool.Get(name) : name, parentIdx, dir ? EntryStore.FDir : (byte)0);
                    added++;
                    // 정션/심볼릭 링크는 따라가지 않음 (중복·무한 루프 방지)
                    if (dir && (data.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                        subdirs.Add((path + "\\" + name, i));
                } while (Native.FindNextFile(h, out data));
            }
            finally
            {
                Native.FindClose(h);
            }
            Interlocked.Add(ref IndexBuilder.Scanned, added);
        }
    }

    // ── 검색 ────────────────────────────────────────────────────
    internal sealed class SearchTerm
    {
        public string Text;
        public bool Not, Wild, Path;
        public string Head, Tail;   // 경로 검색어의 마지막 '\' 앞/뒤
        public string Suffix;       // "*xxx" 형태면 xxx
        public sbyte[] DirMemo;     // 폴더별 "경로에 검색어 포함" 결과 캐시 (0 미확인, 1 예, -1 아니오)
    }

    /// <summary>
    /// 검색어 문법 (Everything 유사)
    ///   공백 = AND, "따옴표" = 공백 포함 구문, !단어 = 제외, * ? = 와일드카드(이름 전체 일치)
    ///   \ 포함 = 전체 경로에서 검색, ext:pdf;docx = 확장자, file: / folder: = 종류
    /// </summary>
    internal sealed class SearchQuery
    {
        public readonly List<SearchTerm> Terms = new List<SearchTerm>();
        public readonly List<string[]> ExtSets = new List<string[]>();
        public int Type; // 0 전체, 1 파일만, 2 폴더만

        public static SearchQuery Parse(string text, int type, string[] categoryExts)
        {
            var q = new SearchQuery { Type = type };
            if (categoryExts != null) q.ExtSets.Add(categoryExts);
            foreach (string raw in Tokenize(text ?? ""))
            {
                string tok = raw;
                bool not = false;
                if (tok.Length > 1 && tok[0] == '!') { not = true; tok = tok.Substring(1); }
                string lower = tok.ToLowerInvariant();
                if (lower.StartsWith("ext:"))
                {
                    var exts = tok.Substring(4).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.TrimStart('.')).Where(e => e.Length > 0).ToArray();
                    if (exts.Length > 0) q.ExtSets.Add(exts);
                    continue;
                }
                if (lower == "file:" || lower == "files:") { q.Type = 1; continue; }
                if (lower == "folder:" || lower == "folders:") { q.Type = 2; continue; }
                tok = tok.Replace('/', '\\');
                if (tok.Length == 0) continue;
                int last = tok.LastIndexOf('\\');
                q.Terms.Add(new SearchTerm
                {
                    Text = tok,
                    Not = not,
                    Wild = tok.IndexOfAny(new[] { '*', '?' }) >= 0,
                    Path = last >= 0,
                    Head = last >= 0 ? tok.Substring(0, last) : null,
                    Tail = last >= 0 ? tok.Substring(last + 1) : null,
                    // "*.hwp" 같은 흔한 패턴은 EndsWith로 빠르게 처리
                    Suffix = last < 0 && tok.Length > 1 && tok[0] == '*' && tok.IndexOfAny(new[] { '*', '?' }, 1) < 0
                        ? tok.Substring(1) : null
                });
            }
            return q;
        }

        private static IEnumerable<string> Tokenize(string s)
        {
            var sb = new StringBuilder();
            bool quote = false;
            foreach (char ch in s)
            {
                if (ch == '"') { quote = !quote; continue; }
                if (!quote && char.IsWhiteSpace(ch))
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    internal static class SearchEngine
    {
        public static int[] Run(FileIndex x, SearchQuery q, CancellationToken ct)
        {
            x.Lock.EnterReadLock();
            try
            {
                int n = x.Count, sorted = x.SortedCount;
                int[] order = x.NameOrder;
                foreach (var t in q.Terms)
                    t.DirMemo = t.Path && !t.Wild ? new sbyte[n] : null;
                int parts = Math.Max(1, Math.Min(Environment.ProcessorCount, n / 50000 + 1));
                int chunk = (n + parts - 1) / Math.Max(1, parts);
                var lists = new List<int>[parts];

                Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, p =>
                {
                    var l = new List<int>();
                    int start = p * chunk, end = Math.Min(n, start + chunk);
                    for (int pos = start; pos < end; pos++)
                    {
                        if ((pos & 0x3FFF) == 0 && ct.IsCancellationRequested) break;
                        int i = pos < sorted ? order[pos] : pos;
                        if (Match(x, i, q)) l.Add(i);
                    }
                    lists[p] = l;
                });
                ct.ThrowIfCancellationRequested();

                int total = 0;
                foreach (var l in lists) total += l?.Count ?? 0;
                var result = new int[total];
                int o = 0;
                foreach (var l in lists)
                {
                    if (l == null) continue;
                    l.CopyTo(result, o);
                    o += l.Count;
                }
                return result;
            }
            finally
            {
                x.Lock.ExitReadLock();
            }
        }

        private static bool Match(FileIndex x, int i, SearchQuery q)
        {
            byte f = x.Flags[i];
            if ((f & EntryStore.FDeleted) != 0) return false;
            bool dir = (f & EntryStore.FDir) != 0;
            if (q.Type == 1 && dir) return false;
            if (q.Type == 2 && !dir) return false;
            if (x.Parents[i] < 0 && q.Terms.Count == 0) return false; // 빈 검색에서는 드라이브 루트 숨김

            string name = x.Names[i];
            if (q.ExtSets.Count > 0)
            {
                if (dir) return false;
                foreach (var set in q.ExtSets)
                    if (!ExtIn(name, set)) return false;
            }

            foreach (var t in q.Terms)
            {
                bool m;
                if (t.DirMemo != null) m = PathContains(x, i, name, t);
                else if (t.Suffix != null) m = name.EndsWith(t.Suffix, StringComparison.OrdinalIgnoreCase);
                else
                {
                    string subject = t.Path ? x.FullPath(i) : name;
                    m = t.Wild
                        ? Wildcard(subject, t.Text)
                        : subject.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (m == t.Not) return false;
            }

            if (x.AnyDeleted && !x.AncestorsAlive(i)) return false;
            return true;
        }

        /// <summary>
        /// 전체 경로(상위폴더\이름)에 검색어가 있는지 문자열 생성 없이 확인.
        /// 이름에는 '\'가 없으므로 경계를 넘는 일치는 "상위폴더가 Head로 끝나고 이름이 Tail로 시작"하는 경우뿐.
        /// </summary>
        private static bool PathContains(FileIndex x, int i, string name, SearchTerm t)
        {
            int p = x.Parents[i];
            if (p < 0) return (name + "\\").IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) >= 0;
            string dir = null;
            var memo = t.DirMemo;
            sbyte v = p < memo.Length ? memo[p] : (sbyte)0;
            if (v == 0)
            {
                dir = x.DirPath(p);
                v = dir.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) >= 0 ? (sbyte)1 : (sbyte)-1;
                if (p < memo.Length) memo[p] = v;
            }
            if (v == 1) return true;
            if (!name.StartsWith(t.Tail, StringComparison.OrdinalIgnoreCase)) return false;
            dir = dir ?? x.DirPath(p);
            return dir.EndsWith(t.Head, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ExtIn(string name, string[] set)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0) return false;
            int len = name.Length - dot - 1;
            foreach (string e in set)
                if (e.Length == len && string.Compare(name, dot + 1, e, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            return false;
        }

        /// <summary>대소문자 무시 와일드카드 일치 (* = 0개 이상, ? = 1글자)</summary>
        public static bool Wildcard(string s, string p)
        {
            int si = 0, pi = 0, star = -1, mark = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
                else if (pi < p.Length && (p[pi] == '?' || char.ToUpperInvariant(p[pi]) == char.ToUpperInvariant(s[si]))) { si++; pi++; }
                else if (star >= 0) { pi = star + 1; si = ++mark; }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }
    }

    // ── 실시간 반영 ────────────────────────────────────────────────
    /// <summary>FileSystemWatcher 이벤트를 모아 1초마다 색인에 반영</summary>
    internal sealed class IndexUpdater : IDisposable
    {
        private enum Kind { Created, Deleted, Renamed }

        private struct Change
        {
            public Kind Kind;
            public string Path, OldPath;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<Change> queue =
            new System.Collections.Concurrent.ConcurrentQueue<Change>();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Func<FileIndex> getIndex;
        private readonly System.Threading.Timer timer;
        public volatile bool Paused;
        public event Action Changed;

        public IndexUpdater(Func<FileIndex> getIndex)
        {
            this.getIndex = getIndex;
            timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var w = new FileSystemWatcher(d.Name)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024
                    };
                    w.Created += (s, e) => queue.Enqueue(new Change { Kind = Kind.Created, Path = e.FullPath });
                    w.Deleted += (s, e) => queue.Enqueue(new Change { Kind = Kind.Deleted, Path = e.FullPath });
                    w.Renamed += (s, e) => queue.Enqueue(new Change { Kind = Kind.Renamed, Path = e.FullPath, OldPath = e.OldFullPath });
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch { }
            }
            timer.Change(1000, Timeout.Infinite);
        }

        private void Tick()
        {
            try
            {
                var idx = getIndex();
                if (!Paused && idx != null && !queue.IsEmpty)
                {
                    bool changed = false;
                    idx.Lock.EnterWriteLock();
                    try
                    {
                        int budget = 20000;
                        while (budget-- > 0 && queue.TryDequeue(out var c))
                        {
                            try { changed |= Apply(idx, c); } catch { }
                        }
                    }
                    finally
                    {
                        idx.Lock.ExitWriteLock();
                    }
                    if (changed) Changed?.Invoke();
                }
            }
            catch { }
            finally
            {
                try { timer.Change(1000, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        private static bool Apply(FileIndex idx, Change c)
        {
            switch (c.Kind)
            {
                case Kind.Created:
                    return Create(idx, c.Path);
                case Kind.Deleted:
                {
                    int i = idx.Find(c.Path);
                    if (i < 0) return false;
                    idx.MarkDeleted(i);
                    return true;
                }
                default:
                {
                    int i = idx.Find(c.OldPath);
                    if (i < 0) return Create(idx, c.Path);
                    if (string.Equals(Path.GetDirectoryName(c.OldPath), Path.GetDirectoryName(c.Path), StringComparison.OrdinalIgnoreCase))
                    {
                        idx.Rename(i, Path.GetFileName(c.Path));
                        return true;
                    }
                    idx.MarkDeleted(i);
                    return Create(idx, c.Path);
                }
            }
        }

        private static bool Create(FileIndex idx, string path)
        {
            if (idx.Find(path) >= 0) return false;
            int parent = idx.Find(Path.GetDirectoryName(path));
            if (parent < 0) return false;
            bool dir = Directory.Exists(path);
            if (!dir && !File.Exists(path)) return false; // 이미 사라진 임시 파일
            int i = idx.AddLive(Path.GetFileName(path), parent, dir);
            if (dir)
            {
                // 다른 곳에서 이동해 온 폴더는 하위 항목 이벤트가 없으므로 직접 탐색
                var chunk = new EntryStore(64);
                Walker.WalkTree(path, chunk, CancellationToken.None);
                if (chunk.Count > 0) idx.AppendLive(chunk, i);
            }
            return true;
        }

        public void Dispose()
        {
            timer.Dispose();
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            watchers.Clear();
        }
    }

    /// <summary>색인 수명 관리: 캐시 로드 → 실시간 감시 → 백그라운드 재색인</summary>
    internal sealed class IndexManager : IDisposable
    {
        public static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCManagerCS", "index.dat");

        private volatile FileIndex current;
        private IndexUpdater updater;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int building;

        public FileIndex Current => current;
        public bool Building => building != 0;

        /// <summary>색인 전체가 교체됨 (UI 스레드 아님)</summary>
        public event Action IndexReplaced;
        /// <summary>실시간 변경이 반영됨 (UI 스레드 아님)</summary>
        public event Action IndexChanged;
        public event Action BuildStateChanged;

        public void Start()
        {
            var t = new Thread(() =>
            {
                var cached = FileIndex.Load(CachePath);
                if (cached != null && current == null)
                {
                    cached.Source += " · 캐시";
                    current = cached;
                    IndexReplaced?.Invoke();
                }
                updater = new IndexUpdater(() => current);
                updater.Changed += () => IndexChanged?.Invoke();
                updater.Start();
                // 방금 만든 캐시라면(10분 이내) 재탐색 생략 — 실행 중 변경은 실시간 감시로 반영됨
                if (cached == null || (DateTime.Now - cached.BuiltAt).TotalMinutes > 10) Rebuild();
            }) { IsBackground = true, Name = "IndexLoad" };
            t.Start();
        }

        public void Rebuild()
        {
            if (Interlocked.CompareExchange(ref building, 1, 0) != 0) return;
            BuildStateChanged?.Invoke();
            var t = new Thread(() =>
            {
                try
                {
                    if (updater != null) updater.Paused = true;
                    var idx = IndexBuilder.Build(Program.IsAdmin, cts.Token);
                    try { idx.Save(CachePath); } catch { }
                    current = idx;
                    IndexReplaced?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (updater != null) updater.Paused = false;
                    Interlocked.Exchange(ref building, 0);
                    BuildStateChanged?.Invoke();
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "IndexBuild" };
            t.Start();
        }

        /// <summary>이전 색인을 더 이상 참조하지 않게 된 뒤 호출 → 메모리 즉시 반환</summary>
        public static void ReleaseMemory()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void Dispose()
        {
            cts.Cancel();
            updater?.Dispose();
        }
    }
}
