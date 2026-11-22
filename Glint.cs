// Glint — hotkey screen assistant. Ctrl+Space: capture active window, ask Claude about it.
// Build: build.ps1 (built-in csc.exe, .NET Framework 4).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class App
{
    public static Settings S;
    public static JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public static readonly string[] Models = { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" };
    public static readonly string[] Names = { "Haiku", "Sonnet", "Opus" };
    // $ per 1M tokens: input, output
    static readonly double[,] Price = { { 0.10, 0.50 }, { 2, 10 }, { 4, 20 } };

    public static bool TR = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string T(string en, string tr) { return TR ? tr : en; }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static string[] ApplyFlags(string[] a)
    {
        var list = new List<string>();
        foreach (var x in a)
        {
            if (x == "--en") TR = false;
            else if (x == "--tr") TR = true;
            else if (x == "--light") ChatForm.ThemeOverride = 0;
            else if (x == "--dark") ChatForm.ThemeOverride = 1;
            else list.Add(x);
        }
        return list.ToArray();
    }

    [STAThread]
    static void Main(string[] args)
    {
        args = ApplyFlags(args);
        if (args.Length >= 2 && args[0] == "--focustest") { FocusTest(args[1]); return; }
        if (args.Length >= 2 && (args[0] == "--demo" || args[0] == "--test"))
        {
            Application.ThreadException += (s, e) => { File.WriteAllText(args[args.Length - 1] + ".err.txt", e.Exception.ToString()); Environment.Exit(1); };
            try { OffScreenTest(args); } catch (Exception ex) { File.WriteAllText(args[args.Length - 1] + ".err.txt", ex.ToString()); }
            return;
        }
        bool created;
        var mx = new Mutex(true, "Glint_single_instance", out created);
        if (!created) return;
        SetProcessDPIAware();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        S = Settings.Load();
        var tray = new Tray();
        Application.Run();
        GC.KeepAlive(tray); GC.KeepAlive(mx);
    }

    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    // Run on a hidden Win32 desktop: opens the chat like the hotkey does, "types" abc into the focused
    // window with WM_CHAR (no real keyboard input) and reports where the text landed.
    static void FocusTest(string outTxt)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        S = Settings.Load();
        var f = new ChatForm();
        int step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 250 };
        t.Tick += delegate
        {
            step++;
            if (step == 1) f.NewConversation(new Bitmap(800, 500), "Test", "");
            else if (step == 10)
            {
                IntPtr h = GetFocus();
                foreach (char c in "abc") PostMessage(h, 0x0102 /*WM_CHAR*/, (IntPtr)c, (IntPtr)1);
            }
            else if (step == 14)
            {
                t.Stop();
                var cls = new StringBuilder(64); GetClassName(GetFocus(), cls, 64);
                File.WriteAllText(outTxt, "focus=" + cls + " value=" + f.InputValue());
                f.Shutdown(); Application.ExitThread();
            }
        };
        t.Start();
        Application.Run();
    }

    // Invisible self-test: window lives off-screen, never activates, result saved as PNG.
    //   --demo out.png                      canned conversation, no API call
    //   --test image.png "question" out.png real question to the configured backend
    static void OffScreenTest(string[] a)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        S = Settings.Load();
        var f = new ChatForm { OffScreen = true };
        string outPng = a[0] == "--demo" ? a[1] : a[3];
        Action snap = delegate
        {
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += delegate { t.Stop(); using (var b = f.Snapshot()) b.Save(outPng, ImageFormat.Png); f.Shutdown(); Application.ExitThread(); };
            t.Start();
        };
        var start = new System.Windows.Forms.Timer { Interval = 300 };
        start.Tick += delegate
        {
            start.Stop();
            if (a[0] == "--demo")
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(SystemInformation.VirtualScreen.Right + 200, 0);
                f.Show(); f.Demo(); snap();
            }
            else
            {
                f.NewConversation(new Bitmap(a[1]), "Test", "");
                f.AnswerShown += snap;
                var q = new System.Windows.Forms.Timer { Interval = 1500 };
                q.Tick += delegate { q.Stop(); f.SendText(a[2]); };
                q.Start();
            }
        };
        start.Start();
        Application.Run();
    }

    public static string SystemPrompt(bool esc, int mi)
    {
        string s = "You are Glint, a desktop helper. The user shares a screenshot of their screen (plus the active window title) and asks about it. " +
                   "Answer in the user's language. Be concise and practical: lead with the answer, no preamble. Use Markdown lists or code blocks only when they help.";
        if (esc && mi == 0) s += " If a good answer needs deep multi-step reasoning, careful debugging or long code, reply with exactly ESCALATE and nothing else.";
        if (esc && mi == 1) s += " Only if the task truly needs the strongest model (very hard reasoning or large complex code), reply with exactly ESCALATE and nothing else.";
        return s;
    }

    public static double Cost(int mi, long inp, long outp, long cr, long cw)
    {
        double pin = Price[mi, 0], pout = Price[mi, 1];
        return (inp * pin + cw * pin * 1.25 + cr * pin * 0.1 + outp * pout) / 1e6;
    }

    public static Dictionary<string, object> Obj(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
    public static Dictionary<string, object> D(object d, string k)
    {
        var x = d as Dictionary<string, object>; object o;
        return x != null && x.TryGetValue(k, out o) ? o as Dictionary<string, object> : null;
    }
    public static string Str(object d, string k)
    {
        var x = d as Dictionary<string, object>; object o;
        return x != null && x.TryGetValue(k, out o) && o != null ? o.ToString() : null;
    }
    public static double Num(object d, string k)
    {
        var x = d as Dictionary<string, object>; object o;
        if (x != null && x.TryGetValue(k, out o) && o != null) try { return Convert.ToDouble(o); } catch { }
        return 0;
    }
    public static void Usage(Dictionary<string, object> u, Reply r)
    {
        r.In = (long)Num(u, "input_tokens"); r.Out = (long)Num(u, "output_tokens");
        r.CacheR = (long)Num(u, "cache_read_input_tokens"); r.CacheW = (long)Num(u, "cache_creation_input_tokens");
    }
    public static bool IsEscalate(string t)
    {
        return t != null && t.Trim().Length < 40 && Regex.IsMatch(t, @"^\s*\**ESCALATE\b");
    }
}

class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glint");
    public static readonly string FilePath = Path.Combine(Dir, "settings.ini");
    readonly Dictionary<string, string> V = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly List<string> order = new List<string>();
    static readonly string[] Defaults = {
        "hotkey", "Ctrl+Space",
        "hotkey2", "Ctrl+Alt+Space",
        "passthrough", "Code.exe,Cursor.exe,devenv.exe,idea64.exe,pycharm64.exe,webstorm64.exe,rider64.exe,studio64.exe,notepad++.exe,sublime_text.exe",
        "backend", "cli",
        "maxEdge", "1280",
        "jpegQuality", "80",
        "maxTokens", "1024",
        "historyTurns", "6",
        "apikey", ""
    };

    public string this[string k]
    {
        get { string v; return V.TryGetValue(k, out v) ? v : ""; }
        set { if (!V.ContainsKey(k)) order.Add(k); V[k] = value; }
    }
    public int Int(string k, int def) { int v; return int.TryParse(this[k], out v) ? v : def; }

    public static Settings Load()
    {
        var s = new Settings();
        for (int i = 0; i < Defaults.Length; i += 2) s[Defaults[i]] = Defaults[i + 1];
        if (File.Exists(FilePath))
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (line.TrimStart().StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) s[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var sb = new StringBuilder();
        sb.Append("# Glint settings. After changing them: tray menu > 'Reload settings'.\r\n");
        sb.Append("# hotkey: main shortcut (in programs listed in passthrough the key goes to that program), hotkey2: backup that works everywhere.\r\n");
        sb.Append("# backend: cli = Claude Code subscription (no extra charge), api = Anthropic API key (billed separately).\r\n");
        sb.Append("# maxEdge: long edge of the image (px). Smaller = fewer tokens. maxTokens: answer cap (x4 for Sonnet/Opus).\r\n");
        foreach (var k in order) sb.Append(k).Append('=').Append(V[k]).Append("\r\n");
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
    }

    public string ApiKey
    {
        get
        {
            try
            {
                if (this["apikey"].Length > 0)
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(this["apikey"]), null, DataProtectionScope.CurrentUser));
            }
            catch { }
            return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        }
        set
        {
            this["apikey"] = string.IsNullOrEmpty(value) ? "" :
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
            Save();
        }
    }
}

class Tray : NativeWindow
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly NotifyIcon ni;
    ChatForm chat;
    MenuItem miCli, miApi, miAuto;

    public Tray()
    {
        CreateHandle(new CreateParams());
        ni = new NotifyIcon { Icon = MakeIcon(), Visible = true, Text = "Glint" };
        var m = new ContextMenu();
        m.MenuItems.Add(App.T("Ask about the screen", "Ekrana sor"), delegate { Trigger(); }).DefaultItem = true;
        m.MenuItems.Add("-");
        miCli = m.MenuItems.Add(App.T("Connection: Claude Code subscription", "Bağlantı: Claude Code aboneliği"), delegate { SetBackend("cli"); });
        miApi = m.MenuItems.Add(App.T("Connection: API key (paid)", "Bağlantı: API anahtarı (ücretli)"), delegate { SetBackend("api"); });
        m.MenuItems.Add(App.T("Enter API key…", "API anahtarı gir…"), delegate { AskKey(); });
        m.MenuItems.Add("-");
        miAuto = m.MenuItems.Add(App.T("Start with Windows", "Windows ile başlat"), delegate { ToggleAutostart(); });
        m.MenuItems.Add(App.T("Open settings file", "Ayarlar dosyasını aç"), delegate { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); });
        m.MenuItems.Add(App.T("Reload settings", "Ayarları yeniden yükle"), delegate { App.S = Settings.Load(); RegisterAll(true); RefreshMenu(); });
        m.MenuItems.Add("-");
        m.MenuItems.Add(App.T("Quit", "Çıkış"), delegate { Quit(); });
        ni.ContextMenu = m;
        ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Trigger(); };
        RefreshMenu();
        RegisterAll(true);
    }

    void Quit()
    {
        if (chat != null && !chat.IsDisposed) chat.Shutdown();
        ni.Visible = false;
        Application.Exit();
    }

    void RefreshMenu()
    {
        bool api = App.S["backend"] == "api";
        miApi.Checked = api; miCli.Checked = !api;
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) miAuto.Checked = k != null && k.GetValue("Glint") != null;
    }

    void SetBackend(string b)
    {
        if (b == "api" && App.S.ApiKey.Length == 0 && !AskKey()) return;
        App.S["backend"] = b; App.S.Save(); RefreshMenu();
    }

    bool AskKey()
    {
        using (var f = new Form { Text = App.T("Anthropic API key", "Anthropic API anahtarı"), Size = new Size(460, 170), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, TopMost = true })
        {
            var l = new Label { Text = App.T("console.anthropic.com > API Keys. The key is stored encrypted (DPAPI) on this computer.\nAPI usage is billed separately from your Claude subscription.",
                                            "console.anthropic.com > API Keys. Anahtar bu bilgisayarda şifreli (DPAPI) saklanır.\nAPI kullanımı Claude aboneliğinden ayrı faturalandırılır."), Location = new Point(12, 10), Size = new Size(430, 36) };
            var t = new TextBox { Location = new Point(12, 52), Width = 420, UseSystemPasswordChar = true };
            var ok = new Button { Text = App.T("Save", "Kaydet"), Location = new Point(276, 86), DialogResult = DialogResult.OK };
            var no = new Button { Text = App.T("Cancel", "İptal"), Location = new Point(356, 86), DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { l, t, ok, no });
            f.AcceptButton = ok; f.CancelButton = no;
            if (f.ShowDialog() != DialogResult.OK || t.Text.Trim().Length == 0) return false;
            App.S.ApiKey = t.Text;
            return true;
        }
    }

    void ToggleAutostart()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k.GetValue("Glint") != null) k.DeleteValue("Glint");
            else k.SetValue("Glint", "\"" + Application.ExecutablePath + "\"");
        }
        RefreshMenu();
    }

    void RegisterAll(bool announce)
    {
        UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2);
        var ok = new List<string>(); var bad = new List<string>();
        (Reg(1, App.S["hotkey"]) ? ok : bad).Add(App.S["hotkey"]);
        if (App.S["hotkey2"].Length > 0) (Reg(2, App.S["hotkey2"]) ? ok : bad).Add(App.S["hotkey2"]);
        string tip = "Glint: " + string.Join(" / ", ok.ToArray());
        ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        if (bad.Count > 0)
            ni.ShowBalloonTip(6000, "Glint", App.T("Could not register shortcut (another program may be using it): ", "Kısayol alınamadı (başka program kullanıyor olabilir): ") + string.Join(", ", bad.ToArray()) +
                (ok.Count > 0 ? App.T("\nWorking: ", "\nÇalışan: ") + string.Join(", ", ok.ToArray()) : App.T("\nPick another shortcut in the settings file.", "\nAyarlar dosyasından başka kısayol seç.")), ToolTipIcon.Warning);
        else if (announce)
            ni.ShowBalloonTip(3000, App.T("Glint is ready", "Glint hazır"), string.Join(App.T(" or ", " veya "), ok.ToArray()) + App.T(" to ask about the screen.", " ile ekrana sor."), ToolTipIcon.Info);
    }

    bool Reg(int id, string spec)
    {
        uint mods, vk;
        return Parse(spec, out mods, out vk) && RegisterHotKey(Handle, id, mods | 0x4000 /*MOD_NOREPEAT*/, vk);
    }

    static bool Parse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrEmpty(spec)) return false;
        foreach (var p0 in spec.Split('+'))
        {
            string p = p0.Trim().ToLowerInvariant();
            if (p == "ctrl" || p == "control") mods |= 2;
            else if (p == "alt") mods |= 1;
            else if (p == "shift") mods |= 4;
            else if (p == "win") mods |= 8;
            else { Keys k; if (!Enum.TryParse(p, true, out k)) return false; vk = (uint)k; }
        }
        return vk != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) // WM_HOTKEY
        {
            if (m.WParam.ToInt32() == 1 && InPassthroughApp()) PassThrough();
            else Trigger();
            return;
        }
        base.WndProc(ref m);
    }

    static bool InPassthroughApp()
    {
        try
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            string n = Process.GetProcessById((int)pid).ProcessName + ".exe";
            foreach (var x in App.S["passthrough"].Split(','))
                if (x.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    // Give the key back to the editor (e.g. VS Code autocomplete): drop our hotkey, replay the key, re-register.
    void PassThrough()
    {
        uint mods, vk; Parse(App.S["hotkey"], out mods, out vk);
        UnregisterHotKey(Handle, 1);
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        keybd_event((byte)vk, 0, 2, UIntPtr.Zero);
        var t = new System.Windows.Forms.Timer { Interval = 200 };
        t.Tick += delegate { t.Stop(); t.Dispose(); Reg(1, App.S["hotkey"]); };
        t.Start();
    }

    void Trigger()
    {
        if (chat != null && !chat.IsDisposed && chat.Visible)
        {
            if (GetForegroundWindow() == chat.Handle) { chat.Hide(); return; }
            chat.Hide(); Application.DoEvents(); Thread.Sleep(150);
        }
        string title, proc;
        Bitmap bmp = Shot.Capture(out title, out proc);
        if (chat == null || chat.IsDisposed) chat = new ChatForm();
        chat.NewConversation(bmp, title, proc);
    }

    static Icon MakeIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
    }

}

static class Shot
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    // Active window (DWM frame bounds = no invisible 8px border); desktop/taskbar/tiny -> monitor under cursor.
    public static Bitmap Capture(out string title, out string proc)
    {
        IntPtr h = Tray.GetForegroundWindow();
        title = ""; proc = "";
        var cls = new StringBuilder(64); GetClassName(h, cls, 64);
        string c = cls.ToString();
        Rectangle r = Rectangle.Empty;
        bool shell = h == IntPtr.Zero || c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        if (!shell && !IsIconic(h))
        {
            RECT rc;
            if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out rc, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out rc);
            r = Rectangle.FromLTRB(rc.L, rc.T, rc.R, rc.B);
            r.Intersect(SystemInformation.VirtualScreen);
            var t = new StringBuilder(256); GetWindowText(h, t, 256); title = t.ToString();
            try { uint pid; Tray.GetWindowThreadProcessId(h, out pid); proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
        }
        if (r.Width < 160 || r.Height < 100) { r = Screen.FromPoint(Cursor.Position).Bounds; title = App.T("Entire screen", "Tüm ekran"); proc = ""; }
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    public static byte[] Jpeg(Bitmap src, int maxEdge, int quality, out int w, out int h)
    {
        double s = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
        w = Math.Max(1, (int)Math.Round(src.Width * s)); h = Math.Max(1, (int)Math.Round(src.Height * s));
        using (var b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            ImageCodecInfo enc = null;
            foreach (var ci in ImageCodecInfo.GetImageEncoders()) if (ci.MimeType == "image/jpeg") enc = ci;
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            using (var ms = new MemoryStream()) { b.Save(ms, enc, ep); return ms.ToArray(); }
        }
    }
}

class Reply
{
    public string Text, Error;
    public bool Escalate;
    public int Model;
    public long In, Out, CacheR, CacheW;
    public double Cost = -1, Limit = -1;
}

interface IBackend : IDisposable
{
    Reply Ask(int model, bool esc, string text, byte[] jpeg);
    void Reset(int warmModel, bool esc);
    bool KeepsEscalatedTurn { get; }
}

static class Content
{
    public static List<object> Build(string text, byte[] jpeg)
    {
        var c = new List<object>();
        if (jpeg != null)
            c.Add(App.Obj("type", "image", "source", App.Obj("type", "base64", "media_type", "image/jpeg", "data", Convert.ToBase64String(jpeg))));
        c.Add(App.Obj("type", "text", "text", text));
        return c;
    }
}

// Option A: Anthropic Messages API with the user's key (billed separately).
class ApiBackend : IBackend
{
    readonly List<Dictionary<string, object>> msgs = new List<Dictionary<string, object>>();
    public bool KeepsEscalatedTurn { get { return false; } }
    public void Reset(int warmModel, bool esc) { lock (msgs) msgs.Clear(); }
    public void Dispose() { }

    public Reply Ask(int mi, bool esc, string text, byte[] jpeg)
    {
        lock (msgs)
        {
            var r = new Reply { Model = mi };
            if (jpeg != null) DropOldImages();
            msgs.Add(App.Obj("role", "user", "content", Content.Build(text, jpeg)));
            Trim();
            int maxTok = App.S.Int("maxTokens", 1024) * (mi == 0 ? 1 : 4);
            var body = App.Obj("model", App.Models[mi], "max_tokens", maxTok, "system", App.SystemPrompt(esc, mi),
                               "messages", msgs, "cache_control", App.Obj("type", "ephemeral"));
            if (mi == 0) body["thinking"] = App.Obj("type", "disabled");
            else { body["output_config"] = App.Obj("effort", mi == 1 ? "low" : "medium"); body["fallbacks"] = "default"; }
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
                req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 240000;
                req.Headers["x-api-key"] = App.S.ApiKey;
                req.Headers["anthropic-version"] = "2023-06-01";
                if (mi > 0) req.Headers["anthropic-beta"] = "server-side-fallback-2026-07-01";
                byte[] b = Encoding.UTF8.GetBytes(App.Json.Serialize(body));
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                string resp;
                using (var res = req.GetResponse()) using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd();
                var d = (Dictionary<string, object>)App.Json.DeserializeObject(resp);
                var sb = new StringBuilder();
                var blocks = d["content"] as object[];
                if (blocks != null) foreach (var o in blocks) if (App.Str(o, "type") == "text") sb.Append(App.Str(o, "text"));
                r.Text = sb.ToString();
                App.Usage(App.D(d, "usage"), r);
                r.Cost = App.Cost(mi, r.In, r.Out, r.CacheR, r.CacheW);
                if (App.Str(d, "stop_reason") == "refusal" && r.Text.Length == 0) r.Error = App.T("The model did not answer this request (refusal).", "Model bu isteği yanıtlamadı (refusal).");
            }
            catch (WebException ex)
            {
                string detail = ex.Message;
                try
                {
                    if (ex.Response != null)
                        using (var sr = new StreamReader(ex.Response.GetResponseStream()))
                        {
                            var d = App.Json.DeserializeObject(sr.ReadToEnd());
                            detail = App.Str(App.D(d, "error"), "message") ?? detail;
                        }
                }
                catch { }
                r.Error = App.T("API error: ", "API hatası: ") + detail;
            }
            catch (Exception ex) { r.Error = App.T("API error: ", "API hatası: ") + ex.Message; }

            if (r.Error != null || App.IsEscalate(r.Text))
            {
                r.Escalate = r.Error == null;
                msgs.RemoveAt(msgs.Count - 1); // caller retries (or gives up) with the same user turn
            }
            else msgs.Add(App.Obj("role", "assistant", "content", r.Text));
            return r;
        }
    }

    void DropOldImages()
    {
        foreach (var m in msgs)
        {
            var c = m["content"] as List<object>;
            if (c == null) continue;
            for (int i = 0; i < c.Count; i++)
                if (App.Str(c[i], "type") == "image") c[i] = App.Obj("type", "text", "text", App.T("[previous screenshot removed]", "[önceki ekran görüntüsü kaldırıldı]"));
        }
    }

    // Keep the first exchange (has the screenshot context) + the last N exchanges.
    void Trim()
    {
        int keep = App.S.Int("historyTurns", 6) * 2;
        int n = msgs.Count; // odd: ends with the new user turn
        if (n > keep + 3) msgs.RemoveRange(2, n - (keep + 3));
    }
}

// Option B: Claude Code CLI (`claude -p`), uses the user's Claude subscription. One warm process per conversation+model.
class CliBackend : IBackend
{
    Process p;
    int pModel = -1; bool pEsc;
    string sid;
    readonly StringBuilder err = new StringBuilder();
    readonly AutoResetEvent done = new AutoResetEvent(false);
    Dictionary<string, object> lastResult;
    double limit = -1;
    readonly object gate = new object();
    public bool KeepsEscalatedTurn { get { return true; } }

    static string ClaudePath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { string f = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(f)) return f; } catch { }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe");
        return File.Exists(local) ? local : "claude.exe";
    }

    public void Reset(int warmModel, bool esc)
    {
        lock (gate) { Kill(); sid = null; try { Start(warmModel, esc); } catch { } }
    }

    public void Dispose() { lock (gate) Kill(); }

    void Kill()
    {
        var old = p; p = null; pModel = -1;
        if (old == null) return;
        try { old.StandardInput.Close(); if (!old.WaitForExit(500)) old.Kill(); } catch { }
    }

    void Start(int mi, bool esc)
    {
        Kill();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Glint\cli");
        Directory.CreateDirectory(dir);
        string args = "-p --model " + App.Models[mi] + " --effort " + (mi == 2 ? "medium" : "low") +
            " --tools \"\" --setting-sources \"\" --strict-mcp-config --disable-slash-commands" +
            " --input-format stream-json --output-format stream-json --verbose" +
            " --system-prompt \"" + App.SystemPrompt(esc, mi).Replace("\"", "'") + "\"" +
            (sid != null ? " --resume " + sid : "");
        var psi = new ProcessStartInfo(ClaudePath(), args)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = (App.S.Int("maxTokens", 1024) * (mi == 0 ? 1 : 4)).ToString();
        lock (err) err.Length = 0;
        var proc = Process.Start(psi);
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) if (err.Length < 4000) err.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        p = proc; pModel = mi; pEsc = esc;
        var th = new Thread(() => ReadLoop(proc)) { IsBackground = true };
        th.Start();
    }

    void ReadLoop(Process proc)
    {
        try
        {
            string line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                if (!line.StartsWith("{")) continue;
                object d;
                try { d = App.Json.DeserializeObject(line); } catch { continue; }
                string t = App.Str(d, "type");
                if (t == "rate_limit_event")
                {
                    var uw = App.D(App.D(d, "rate_limit_info"), "unifiedWindows");
                    var fh = App.D(uw, "five_hour");
                    if (fh != null) limit = App.Num(fh, "utilization");
                }
                else if (t == "result" && proc == p) { lastResult = (Dictionary<string, object>)d; done.Set(); }
            }
        }
        catch { }
        if (proc == p) { lastResult = null; done.Set(); }
    }

    string Err() { lock (err) return err.ToString().Trim(); }


    public Reply Ask(int mi, bool esc, string text, byte[] jpeg)
    {
        lock (gate)
        {
            var r = new Reply { Model = mi };
            try { if (p == null || p.HasExited || pModel != mi || pEsc != esc) Start(mi, esc); }
            catch (Exception ex) { r.Error = App.T("Could not start Claude Code CLI (is claude.exe missing?): ", "Claude Code CLI başlatılamadı (claude.exe bulunamadı mı?): ") + ex.Message; return r; }
            var msg = App.Obj("type", "user", "message", App.Obj("role", "user", "content", Content.Build(text, jpeg)));
            done.Reset(); lastResult = null;
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(App.Json.Serialize(msg) + "\n");
                p.StandardInput.BaseStream.Write(b, 0, b.Length);
                p.StandardInput.BaseStream.Flush();
            }
            catch (Exception ex) { r.Error = App.T("Could not write to Claude CLI: ", "Claude CLI'ya yazılamadı: ") + ex.Message + "\n" + Err(); Kill(); return r; }
            if (!done.WaitOne(240000)) { r.Error = App.T("Timed out (4 min).", "Zaman aşımı (4 dk)."); Kill(); return r; }
            var d = lastResult;
            if (d == null) { r.Error = App.T("Claude CLI closed.\n", "Claude CLI kapandı.\n") + Err(); Kill(); return r; }
            sid = App.Str(d, "session_id") ?? sid;
            r.Text = App.Str(d, "result") ?? "";
            if (App.Str(d, "is_error") == "True") { r.Error = "Claude CLI: " + r.Text; r.Text = null; }
            App.Usage(App.D(d, "usage"), r);
            r.Cost = App.Num(d, "total_cost_usd");
            r.Limit = limit;
            r.Escalate = r.Error == null && App.IsEscalate(r.Text);
            return r;
        }
    }
}

