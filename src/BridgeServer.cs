// 浏览器桥接服务：本机 127.0.0.1 上的极简 HTTP 服务，供浏览器扩展长轮询取指令。
// 有了它，抖音的「上下切换视频」「长按快进」就不用抢焦点注入按键了 —— 由网页里的脚本直接操作播放器。
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WebVideoRemote
{
    class BridgeServer
    {
        public const int Port = 17777;

        static BridgeServer _instance;
        public static BridgeServer Instance
        {
            get
            {
                if (_instance == null) _instance = new BridgeServer();
                return _instance;
            }
        }

        TcpListener _listener;
        readonly object _lock = new object();
        // 按网站分队列：同时开着 B站 和抖音 时，指令必须发给对应的那个标签页
        readonly Dictionary<string, Queue<string>> _queues = new Dictionary<string, Queue<string>>();
        readonly Dictionary<string, int> _inFlight = new Dictionary<string, int>();
        readonly Dictionary<string, int> _lastPoll = new Dictionary<string, int>();
        volatile bool _started;

        public const string AnySite = "any";

        // exe 期望的桥接脚本版本（和 extension/content.js 里的 BRIDGE_VER 对应）
        public const string ExpectedVer = "1.11";

        public string LastState = "";
        public string LastSite = "";
        public string LastVersion = "";

        // 扩展是「长轮询」：一个请求挂 25 秒。只要有请求挂着就算已连接；
        // 有请求挂着就算已连接；没有挂起请求时，最近 15 秒内有过轮询也算
        // （旧版脚本每 5 秒才上报一次状态，窗口太小会误判成「没连接」）
        public bool ConnectedFor(string site)
        {
            if (!_started) return false;
            lock (_lock)
            {
                int n;
                if (_inFlight.TryGetValue(site, out n) && n > 0) return true;
                int t;
                if (_lastPoll.TryGetValue(site, out t) && unchecked(Environment.TickCount - t) < 15000) return true;
                return false;
            }
        }

        public bool Connected
        {
            get { return ConnectedFor("douyin") || ConnectedFor("bilibili"); }
        }

        public string ConnectedSites
        {
            get
            {
                var list = new List<string>();
                if (ConnectedFor("douyin")) list.Add("抖音");
                if (ConnectedFor("bilibili")) list.Add("B站");
                return list.Count == 0 ? "" : string.Join("/", list.ToArray());
            }
        }

        public void Start()
        {
            if (_started) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _started = true;
                var t = new Thread(AcceptLoop) { IsBackground = true, Name = "WVRemoteBridge" };
                t.Start();
                Log.Write("浏览器桥接服务已监听 127.0.0.1:" + Port);
            }
            catch (Exception e)
            {
                Log.Write("浏览器桥接服务启动失败: " + e.Message + "（端口可能被占用）");
            }
        }

        void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    var c = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(Handle, c);
                }
                catch { Thread.Sleep(500); }
            }
        }

        public void Send(string cmd) { Send(cmd, AnySite); }

        public void Send(string cmd, string site)
        {
            if (string.IsNullOrEmpty(site)) site = AnySite;
            lock (_lock)
            {
                Queue<string> q;
                if (!_queues.TryGetValue(site, out q)) { q = new Queue<string>(); _queues[site] = q; }
                q.Enqueue(cmd);
            }
        }

        string Take(string site, int timeoutMs)
        {
            int deadline = unchecked(Environment.TickCount + timeoutMs);
            while (true)
            {
                lock (_lock)
                {
                    Queue<string> q;
                    if (_queues.TryGetValue(site, out q) && q.Count > 0) return q.Dequeue();
                    // 广播指令（不指定网站）谁先取到就谁执行
                    if (site != AnySite && _queues.TryGetValue(AnySite, out q) && q.Count > 0) return q.Dequeue();
                }
                if (unchecked(Environment.TickCount - deadline) >= 0) return null;
                Thread.Sleep(25);
            }
        }

        string Take(int timeoutMs) { return Take(AnySite, timeoutMs); }

        void MarkPoll(string site, bool start)
        {
            lock (_lock)
            {
                int n;
                _inFlight.TryGetValue(site, out n);
                _inFlight[site] = Math.Max(0, n + (start ? 1 : -1));
                _lastPoll[site] = Environment.TickCount;
                if (site != AnySite) LastSite = site;
            }
        }

        // 只记录「这个网站刚刚上报过」，不动长轮询计数。
        // /state 是脚本每 5 秒一次的状态上报，不是挂起中的轮询请求，
        // 之前误用 MarkPoll(site,false) 会把正在挂起的轮询计数扣掉，导致连接状态抖动。
        void NoteSite(string site)
        {
            lock (_lock)
            {
                _lastPoll[site] = Environment.TickCount;
                if (site != AnySite) LastSite = site;
            }
        }

        // 读完整一个 HTTP 请求（含 POST body）。
        // 之前只 Read 一次，body 被 TCP 拆包时 /state 会截断，版本与回执就解析不出来。
        static string ReadRequest(NetworkStream ns)
        {
            var ms = new System.IO.MemoryStream();
            var buf = new byte[4096];
            int headerEnd = -1, contentLength = 0;
            try { ns.ReadTimeout = 3000; } catch { }
            while (true)
            {
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);

                if (headerEnd < 0)
                {
                    string hay = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    headerEnd = hay.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        foreach (var line in hay.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(15).Trim(), out contentLength);
                    }
                }
                if (headerEnd >= 0 && (int)ms.Length - (headerEnd + 4) >= contentLength) break;
                if (ms.Length > 65536) break;
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        void Handle(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                using (var ns = client.GetStream())
                {
                    string req = ReadRequest(ns);
                    if (req.Length == 0) return;

                    int sp1 = req.IndexOf(' ');
                    int sp2 = sp1 > 0 ? req.IndexOf(' ', sp1 + 1) : -1;
                    string method = sp1 > 0 ? req.Substring(0, sp1) : "GET";
                    string path = (sp1 > 0 && sp2 > sp1) ? req.Substring(sp1 + 1, sp2 - sp1 - 1) : "/";

                    if (method == "OPTIONS") { Write(ns, 204, ""); return; }  // 预检（含私有网络访问预检）

                    if (path.StartsWith("/ping"))
                    {
                        Write(ns, 200, "{\"ok\":true,\"v\":1,\"app\":\"WebVideoRemote\"}");
                        return;
                    }

                    if (path.StartsWith("/next"))
                    {
                        // 扩展会带上自己是哪个网站：/next?site=douyin
                        string site = AnySite;
                        int qi = path.IndexOf("site=", StringComparison.OrdinalIgnoreCase);
                        if (qi >= 0)
                        {
                            string s = path.Substring(qi + 5);
                            int amp = s.IndexOf('&');
                            if (amp >= 0) s = s.Substring(0, amp);
                            s = s.Trim().ToLowerInvariant();
                            if (s == "douyin" || s == "bilibili") site = s;
                        }
                        // 兼容旧版脚本（轮询不带网站标识）：按它最近上报过的网站归类，
                        // 否则会被算成「不属于任何网站」→ 抖音的指令一律发不出去。
                        if (site == AnySite && (LastSite == "douyin" || LastSite == "bilibili")) site = LastSite;

                        MarkPoll(site, true);
                        string cmd;
                        try { cmd = Take(site, 25000); }
                        finally { MarkPoll(site, false); }
                        if (cmd == null) Write(ns, 204, "");
                        else Write(ns, 200, "{\"cmd\":\"" + cmd + "\"}");
                        return;
                    }

                    if (path.StartsWith("/state"))
                    {
                        int idx = req.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (idx >= 0 && req.Length > idx + 4)
                        {
                            string body = req.Substring(idx + 4).Trim();
                            if (body.Length > 0 && body.Length < 800)
                            {
                                LastState = body;
                                // 指令回执写进日志，方便核对「脚本到底做了什么」
                                if (body.IndexOf("\"ack\"", StringComparison.Ordinal) >= 0) Log.Write("脚本回执: " + body);

                                string v = Field(body, "ver");
                                if (v.Length > 0 && v != LastVersion)
                                {
                                    LastVersion = v;
                                    Log.Write("浏览器脚本版本: " + v
                                        + (v == ExpectedVer ? "（与程序一致）" : "  ← 与程序期望的 " + ExpectedVer + " 不一致，请在 edge://extensions 里点「重新加载」并刷新页面"));
                                }

                                string site = body.IndexOf("\"site\":\"douyin\"", StringComparison.Ordinal) >= 0 ? "douyin"
                                            : body.IndexOf("bilibili", StringComparison.Ordinal) >= 0 ? "bilibili" : AnySite;
                                NoteSite(site);
                            }
                        }
                        Write(ns, 200, "{\"ok\":true}");
                        return;
                    }

                    Write(ns, 404, "{\"err\":\"notfound\"}");
                }
            }
            catch { }
            finally { try { client.Close(); } catch { } }
        }

        // 从简单的 JSON 里取 "key":"value"（不引第三方库，够用就行）
        static string Field(string json, string key)
        {
            try
            {
                string pat = "\"" + key + "\"";
                int i = json.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return "";
                i = json.IndexOf(':', i + pat.Length);
                if (i < 0) return "";
                int q1 = json.IndexOf('"', i + 1);
                if (q1 < 0) return "";
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) return "";
                return json.Substring(q1 + 1, q2 - q1 - 1);
            }
            catch { return ""; }
        }

        static void Write(NetworkStream ns, int code, string body)        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : code == 204 ? " No Content" : " Not Found").Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            if (bytes.Length > 0) ns.Write(bytes, 0, bytes.Length);
            ns.Flush();
        }
    }
}
