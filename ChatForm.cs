using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class ChatForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    class JsCall { public string Name; public object[] Args; }

    readonly WebBrowser web;
    readonly bool dark;
    IBackend be;
    string beKind;
    byte[] pendingJpeg;
    string ctx;
    int autoFloor, convo, modelIndex;
    bool busy, ready, quitting;
    readonly List<JsCall> queued = new List<JsCall>();
    readonly List<string> codes = new List<string>();
    long totIn, totOut; double totCost;

    public bool OffScreen;
    public event Action AnswerShown;
    public static int ThemeOverride = -1;

    public ChatForm()
    {
        Text = "Glint";
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(520, 720);
        MinimumSize = new Size(380, 420);
        StartPosition = FormStartPosition.Manual;
        TopMost = true; KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (ThemeOverride >= 0) dark = ThemeOverride == 1;
        else try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) dark = k != null && Equals(k.GetValue("AppsUseLightTheme"), 0); } catch { }

        BackColor = dark ? Color.FromArgb(38, 38, 36) : Color.FromArgb(250, 249, 245);

        web = new WebBrowser
        {
            Dock = DockStyle.Fill,
            ScriptErrorsSuppressed = true,
            IsWebBrowserContextMenuEnabled = false,
            WebBrowserShortcutsEnabled = true,
            AllowWebBrowserDrop = false
        };
        web.ObjectForScripting = new Bridge(this);
        web.Navigating += OnNavigating;
        web.DocumentCompleted += delegate
        {
            if (ready || web.Document == null) return;
            if (web.Document.GetElementById("log") == null) return;
            ready = true;
            foreach (var c in queued) InvokeJs(c.Name, c.Args);
            queued.Clear();
        };
        Controls.Add(web);

        FormClosing += delegate (object s, FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { e.Handled = true; Hide(); } };

        web.DocumentText = "<!DOCTYPE html><html" + (dark ? " class=\"dark\"" : "") +
            "><head><meta charset='utf-8'><meta http-equiv='X-UA-Compatible' content='IE=edge'><style>" +
            Css + "</style></head><body>" + BodyHtml() + "<script type='text/javascript'>" + ScriptJs() + "</script></body></html>";
    }

    protected override bool ShowWithoutActivation { get { return OffScreen; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (OffScreen)
            {
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            }
            return cp;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && OffScreen)
        {
            TopMost = false;
            if (!IsHandleCreated) ShowInTaskbar = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (dark) { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); }
    }

    public void Shutdown()
    {
        quitting = true;
        if (be != null) be.Dispose();
        Close();
    }

    // ---- C# -> JS ----------------------------------------------------------

    void InvokeJs(string name, object[] args)
    {
        if (web.Document == null) return;
        try { web.Document.InvokeScript(name, args == null ? new object[0] : args); } catch { }
    }

    void Js(string name, params object[] args)
    {
        if (!ready) { queued.Add(new JsCall { Name = name, Args = args }); return; }
        InvokeJs(name, args);
    }

    internal void Post(Action a)
    {
        if (IsHandleCreated) BeginInvoke(a); else a();
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    // Browser control first, then the textarea (the other order loses the caret); retried until the page is ready.
    void FocusInput()
    {
        if (OffScreen) { Js("focusInput"); return; }
        int tries = 0;
        var tm = new System.Windows.Forms.Timer { Interval = 60 };
        tm.Tick += delegate
        {
            if (!Visible || ++tries > 25) { tm.Stop(); tm.Dispose(); return; }
            try
            {
                SetForegroundWindow(Handle);
                var d = web.Document;
                var t = d == null ? null : d.GetElementById("ta");
                if (!ready || t == null) return;
                web.Focus(); d.Focus(); t.Focus();
                web.Document.InvokeScript("focusInput");
                tm.Stop(); tm.Dispose();
            }
            catch { }
        };
        tm.Start();
    }

    public string InputValue()
    {
        try { return web.Document.GetElementById("ta").GetAttribute("value"); } catch { return null; }
    }

    void SetStatus(string s) { Js("setStatus", s); }

    void BusyLabel(string s) { Js("setBusy", true, s); Js("setStatus", s); }

    // ---- conversation lifecycle -------------------------------------------

    void EnsureBackend()
    {
        string kind = App.S["backend"] == "api" && App.S.ApiKey.Length > 0 ? "api" : "cli";
        if (be != null && beKind == kind) return;
        if (be != null) { var old = be; ThreadPool.QueueUserWorkItem(_ => old.Dispose()); }
        be = kind == "api" ? (IBackend)new ApiBackend() : new CliBackend();
        beKind = kind;
    }

    public void NewConversation(Bitmap bmp, string title, string proc)
    {
        convo++; busy = false; autoFloor = 0; totIn = totOut = 0; totCost = 0;
        EnsureBackend();
        var b = be; int warm = modelIndex == 0 ? 0 : modelIndex - 1; bool esc = modelIndex == 0;
        ThreadPool.QueueUserWorkItem(_ => b.Reset(warm, esc)); // CLI: pre-start claude.exe while the user types
        codes.Clear();
        Js("clearLog");
        Js("setBusy", false);
        Js("setModel", modelIndex);
        SetShot(bmp, title, proc);
        Place();
        Show();
        if (!OffScreen) Activate();
        FocusInput();
        SetStatus(beKind == "api" ? App.T("API key", "API anahtarı") : App.T("Claude Code subscription", "Claude Code aboneliği"));
    }

    void SetShot(Bitmap bmp, string title, string proc)
    {
        int w, h;
        using (bmp) pendingJpeg = Shot.Jpeg(bmp, App.S.Int("maxEdge", 1280), App.S.Int("jpegQuality", 80), out w, out h);
        ctx = App.T("[Active window: ", "[Aktif pencere: ") + title + (proc.Length > 0 ? " — " + proc : "") + "]";
        string caption = title + " · " + w + "×" + h + " · ≈" + (w * h / 750) + " token";
        Js("addShot", "data:image/jpeg;base64," + Convert.ToBase64String(pendingJpeg), caption);
    }

    void Recapture()
    {
        if (busy) return;
        Hide(); Application.DoEvents(); Thread.Sleep(250);
        string title, proc;
        Bitmap bmp = Shot.Capture(out title, out proc);
        SetShot(bmp, title, proc);
        Show();
        if (!OffScreen) Activate();
        FocusInput();
    }

    void Place()
    {
        if (OffScreen)
        {
            TopMost = false; ShowInTaskbar = false;
            Location = new Point(SystemInformation.VirtualScreen.Right + 200, 0);
            return;
        }
        TopMost = true; ShowInTaskbar = true;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    // ---- model routing -----------------------------------------------------

    static int Route(string t)
    {
        string l = t.ToLowerInvariant();
        if (l.StartsWith("!!")) return 2;
        if (l.StartsWith("!") || t.Length > 400 || l.Contains("```")) return 1;
        string[] hard = { "neden çalışmıyor", "hata ayıkla", "debug", "analiz et", "karşılaştır", "optimize", "mimari", "strateji",
                          "adım adım", "step by step", "detaylı", "ayrıntılı", "kanıtla", "prove", "refactor", "algoritma", "algorithm", "analyze", "compare", "architecture", "strategy", "in detail", "why doesn't", "why does not", "güvenlik açığı" };
        foreach (var k in hard) if (l.Contains(k)) return 1;
        return 0;
    }

    void SetModelIndex(int i)
    {
        if (i < 0) i = 0; if (i > 3) i = 3;
        modelIndex = i;
        Js("setModel", i);
    }

    // ---- sending -----------------------------------------------------------

    void SendMessage(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || busy) return;
        if (be == null) EnsureBackend();
        Js("addUser", text);
        bool auto = modelIndex == 0;
        int mi = auto ? Math.Max(autoFloor, Route(text)) : modelIndex - 1;
        string q = Regex.Replace(text, @"^!+\s*", "");
        if (q.Length == 0) q = text;
        byte[] jpg = pendingJpeg;
        string first = jpg != null ? ctx + "\n" + q : q;
        busy = true;
        int my = convo; var b = be;
        BusyLabel(App.Names[mi] + App.T(" is answering…", " yanıtlıyor…"));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int m = mi;
            Reply r = b.Ask(m, auto, first, jpg);
            while (r.Escalate && m < 2)
            {
                int from = m; m++;
                BeginInvoke((Action)(() => BusyLabel(App.Names[from] + App.T(" found it hard → ", " zor buldu → ") + App.Names[m] + "…")));
                r = b.KeepsEscalatedTurn ? b.Ask(m, auto, App.T("(The previous model handed this question to you; answer it.) ", "(Önceki model bu soruyu sana devretti; yanıtla.) ") + q, null) : b.Ask(m, auto, first, jpg);
            }
            if (r.Escalate) r.Escalate = false;
            BeginInvoke((Action)(() => Done(my, r, jpg, auto, mi)));
        });
    }

    string RenderHtml(string mdText)
    {
        string html = Md.ToHtml(mdText);
        int off = codes.Count;
        codes.AddRange(Md.CodeBlocks);
        html = Regex.Replace(html, "sa:copy/(\\d+)\"", mm => "sa:copy/" + (off + int.Parse(mm.Groups[1].Value)) + "\"");
        return html;
    }

    void Done(int my, Reply r, byte[] jpg, bool auto, int from0)
    {
        if (my != convo) return;
        busy = false;
        Js("setBusy", false);
        if (r.Error != null)
        {
            Js("addError", r.Error);
            SetStatus(App.T("Error", "Hata"));
            FinishTurn();
            return;
        }
        if (jpg != null && pendingJpeg == jpg) pendingJpeg = null;
        if (auto) autoFloor = Math.Max(autoFloor, r.Model);
        string html = RenderHtml(r.Text);
        long inTot = r.In + r.CacheR + r.CacheW;
        totIn += inTot; totOut += r.Out; if (r.Cost > 0) totCost += r.Cost;
        string meta = App.Names[r.Model] + (r.Model != from0 ? App.T(" (escalated)", " (yükseltildi)") : "") + " · " + K(inTot) + App.T(" input", " giriş") +
                      (r.CacheR > 0 ? " (" + K(r.CacheR) + App.T(" from cache", " önbellekten") + ")" : "") + " · " + K(r.Out) + App.T(" output", " çıkış") +
                      (r.Cost >= 0 ? " · ≈$" + r.Cost.ToString("0.0000") : "") +
                      (r.Limit >= 0 ? App.T(" · 5-hour limit ", " · 5 saatlik limit %") + Math.Round(r.Limit * 100) : "");
        Js("addAssistant", html, meta);
        SetStatus(App.T("Total ", "Toplam ") + K(totIn + totOut) + App.T(" tokens", " token") + (beKind == "api" ? " · ≈$" + totCost.ToString("0.0000") : ""));
        FinishTurn();
    }

    void FinishTurn()
    {
        FocusInput();
        var h = AnswerShown;
        if (h != null) h();
    }

    static string K(long n) { return n >= 1000 ? (n / 1000.0).ToString("0.0") + "K" : n.ToString(); }

    // ---- bridge helpers ----------------------------------------------------

    void CopyCode(int n)
    {
        if (n >= 0 && n < codes.Count) { try { Clipboard.SetText(codes[n]); } catch { } }
    }

    void OpenUrl(string url)
    {
        if (url != null && (url.StartsWith("http://") || url.StartsWith("https://")))
            try { Process.Start(url); } catch { }
    }

    void OnNavigating(object sender, WebBrowserNavigatingEventArgs e)
    {
        string u = e.Url == null ? "" : e.Url.ToString();
        if (u.Length == 0 || u == "about:blank") return;
        e.Cancel = true;
        int n;
        if (u.StartsWith("sa:copy/") && int.TryParse(u.Substring(8), out n)) CopyCode(n);
        else OpenUrl(u);
    }

    // ---- bridge entry points (called on the UI thread, deferred) ------------

    internal void SetModelFromBridge(int i) { SetModelIndex(i); }
    internal void RecaptureFromBridge() { Recapture(); }
    internal void CopyFromBridge(int n) { CopyCode(n); }
    internal void OpenFromBridge(string url) { OpenUrl(url); }

    // ---- test hooks --------------------------------------------------------

    public void SendText(string text) { SendMessage(text); }

    [ComImport, Guid("0000010d-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IViewObject
    {
        [PreserveSig] int Draw(uint aspect, int lindex, IntPtr pvAspect, IntPtr ptd, IntPtr hdcTarget, IntPtr hdcDraw, ref Rectangle bounds, IntPtr wBounds, IntPtr cont, int contArg);
    }

    // Draws the HTML content itself (works while the window is off-screen; PrintWindow returns blank for IE there).
    public Bitmap Snapshot()
    {
        var sz = web.ClientSize;
        var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                var r = new Rectangle(0, 0, sz.Width, sz.Height); // RECTL layout: left, top, right, bottom
                ((IViewObject)web.Document.DomDocument).Draw(1, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, hdc, ref r, IntPtr.Zero, IntPtr.Zero, 0);
            }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    public void Demo()
    {
        busy = false;
        codes.Clear();
        Js("clearLog");
        Js("setBusy", false);

        int w, h; byte[] jpg;
        using (var bmp = new Bitmap(640, 400))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 30, 30));
                using (var bar = new SolidBrush(Color.FromArgb(45, 45, 48))) g.FillRectangle(bar, 0, 0, 640, 34);
                using (var red = new SolidBrush(Color.FromArgb(255, 95, 86))) g.FillEllipse(red, 14, 12, 11, 11);
                using (var yellow = new SolidBrush(Color.FromArgb(255, 189, 46))) g.FillEllipse(yellow, 32, 12, 11, 11);
                using (var green = new SolidBrush(Color.FromArgb(39, 201, 63))) g.FillEllipse(green, 50, 12, 11, 11);
                using (var txt = new SolidBrush(Color.FromArgb(205, 205, 205)))
                using (var dim = new SolidBrush(Color.FromArgb(130, 130, 130)))
                using (var f1 = new Font("Segoe UI", 10f))
                using (var f2 = new Font("Consolas", 10f))
                {
                    g.DrawString("Visual Studio Code — Program.cs", f1, txt, 72, 9);
                    g.DrawString("public class Program", f2, txt, 24, 60);
                    g.DrawString("{", f2, txt, 24, 82);
                    g.DrawString("    static void Main()", f2, txt, 24, 104);
                    g.DrawString("    {", f2, txt, 24, 126);
                    g.DrawString("        var user = FindUser(id);", f2, txt, 24, 148);
                    g.DrawString("        Console.WriteLine(user.Name);", f2, txt, 24, 170);
                    g.DrawString("        // NullReferenceException ^", f2, dim, 24, 192);
                    g.DrawString("    }", f2, txt, 24, 214);
                    g.DrawString("}", f2, txt, 24, 236);
                }
            }
            jpg = Shot.Jpeg(bmp, 1280, 80, out w, out h);
        }
        Js("addShot", "data:image/jpeg;base64," + Convert.ToBase64String(jpg),
           "Visual Studio Code — Program.cs · " + w + "×" + h + " · ≈" + (w * h / 750) + " token");

        Js("addUser", App.T("Why is this failing? There's a NullReferenceException in the console.", "Bu hata neden oluyor? Konsolda NullReferenceException var."));
        string md1 = App.T(
            "## What's going wrong\n\n" +
            "The console shows a **NullReferenceException**; `user` is `null`, so accessing `Name` blows up.\n\n" +
            "Checklist:\n\n" +
            "- Look at line `Program.cs:42`\n" +
            "- Check the result of `FindUser(id)`\n" +
            "- Return early on an empty result\n\n" +
            "```python\n" +
            "def greet(name):\n" +
            "    if not name:\n" +
            "        return 'Hello!'\n" +
            "    return 'Hello, ' + name\n" +
            "```\n\n" +
            "| Field | Value |\n" +
            "| --- | --- |\n" +
            "| Model | Haiku |\n" +
            "| Time | 1.2 s |\n",
            "## Görünen hata\n\n" +
            "Konsolda **NullReferenceException** çıkıyor; `user` nesnesi `null` olduğu için `Name` erişimi patlıyor.\n\n" +
            "Kontrol listesi:\n\n" +
            "- `Program.cs:42` satırına bak\n" +
            "- `FindUser(id)` sonucunu doğrula\n" +
            "- Boş dönüşte erken çık\n\n" +
            "```python\n" +
            "def greet(name):\n" +
            "    if not name:\n" +
            "        return 'Merhaba!'\n" +
            "    return 'Merhaba, ' + name\n" +
            "```\n\n" +
            "| Alan | Değer |\n" +
            "| --- | --- |\n" +
            "| Model | Haiku |\n" +
            "| Süre | 1.2 sn |\n");
        Js("addAssistant", RenderHtml(md1), App.T("Haiku · 1.2K input (900 from cache) · 320 output · ≈$0.0019 · 5-hour limit 12%",
                                                  "Haiku · 1.2K giriş (900 önbellekten) · 320 çıkış · ≈$0.0019 · 5 saatlik limit %12"));

        Js("addUser", App.T("And how do I fix this in C#?", "Peki bunu C# ile nasıl düzeltirim?"));
        string md2 = App.T(
            "Add a **guard clause** at the start of the method:\n\n" +
            "`if (user == null) return;`\n\n" +
            "Then `user.Name` can be used safely.",
            "Bunu bir **guard clause** olarak metodun başına koy:\n\n" +
            "`if (user == null) return;`\n\n" +
            "Sonra `user.Name` güvenle kullanılabilir.");
        Js("addAssistant", RenderHtml(md2), App.T("Haiku · 420 input · 180 output · ≈$0.0008", "Haiku · 420 giriş · 180 çıkış · ≈$0.0008"));

        SetStatus(App.T("Total 3.1K tokens · Claude Code subscription", "Toplam 3,1K token · Claude Code aboneliği"));
        FocusInput();
    }

    const string Css = @"
*{box-sizing:border-box}
html,body{margin:0;padding:0;height:100%;overflow:hidden;background:#FAF9F5;color:#141413}
body{font:14.5px/1.5 'Segoe UI',Arial,sans-serif}
#app{display:flex;flex-direction:column;height:100%}
#log{flex:1 1 auto;min-height:0;overflow-y:auto;overflow-x:hidden;padding:18px 20px 6px;
scrollbar-face-color:#D9D7CE;scrollbar-track-color:#FAF9F5;scrollbar-arrow-color:#A9A79E;
scrollbar-shadow-color:#D9D7CE;scrollbar-highlight-color:#FAF9F5;scrollbar-3dlight-color:#FAF9F5;scrollbar-darkshadow-color:#FAF9F5}
#inputarea{padding:0 14px 2px}
#status{padding:4px 14px 9px;text-align:center;font-size:11.5px;color:#73726C;line-height:15px}
.card{background:#FFFFFF;border:1px solid #E8E6DC;border-radius:20px;box-shadow:0 1px 6px rgba(0,0,0,.05);padding:10px 12px 8px}
#ta{display:block;width:100%;border:0;outline:none;resize:none;background:transparent;color:#141413;
font:14.5px/21px 'Segoe UI',Arial,sans-serif;padding:2px 4px;min-height:25px;max-height:168px;overflow-y:auto}
#ta:-ms-input-placeholder{color:#9C9A92}
#ta::-ms-clear{display:none}
.row2{margin-top:6px;display:flex;align-items:center}
.ghost{display:inline-block;background:none;border:0;color:#73726C;font:12.5px 'Segoe UI',Arial,sans-serif;
padding:6px 8px;border-radius:16px;cursor:pointer;vertical-align:middle}
.ghost:hover{background:#F0EEE6;color:#141413}
.ghost svg{vertical-align:-3px;margin-right:5px}
#modelwrap{position:relative;display:inline-block;vertical-align:middle;margin-left:2px}
#pill{background:none;border:1px solid #E8E6DC;color:#73726C;border-radius:16px;padding:5px 10px;
font:12.5px 'Segoe UI',Arial,sans-serif;cursor:pointer}
#pill:hover{background:#F0EEE6;color:#141413}
#menu{display:none;position:absolute;left:0;bottom:100%;margin-bottom:8px;width:252px;background:#FFFFFF;
border:1px solid #E8E6DC;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,.12);padding:5px;z-index:10}
.mi{padding:8px 10px;border-radius:8px;cursor:pointer;position:relative}
.mi:hover{background:#F0EEE6}
.mi-name{display:block;font-size:13.5px;color:#141413}
.mi-desc{display:block;font-size:11.5px;color:#73726C;margin-top:1px}
.mi .ck{position:absolute;right:10px;top:10px;color:#D97757;visibility:hidden;font-size:13px}
.mi.sel .ck{visibility:visible}
.send{margin-left:auto;width:32px;height:32px;border-radius:50%;border:0;background:#D97757;cursor:pointer;padding:0}
.send svg{display:block;margin:8px auto 0}
#pill svg{vertical-align:-1px}
.send:hover:enabled{background:#C6613F}
.send:disabled{background:#D9D7CE;cursor:default}
.msg{word-wrap:break-word;animation:fade .18s ease-out}
.shot{animation:fade .18s ease-out}
@keyframes fade{from{opacity:0}to{opacity:1}}
.me{display:table;margin:8px 0 8px auto;max-width:85%;background:#F0EEE6;border-radius:18px;padding:10px 14px}
.ai{margin:6px 0 18px;font-family:Georgia,'Times New Roman',serif;font-size:15.5px;line-height:1.65;color:#141413}
.ai.err{font-family:'Segoe UI',Arial,sans-serif;font-size:14px;line-height:1.5;color:#B4492F}
.ai>:first-child{margin-top:0}
.ai p{margin:0 0 .8em}
.ai h1,.ai h2,.ai h3,.ai h4,.ai h5,.ai h6{font-family:'Segoe UI',Arial,sans-serif;font-weight:600;line-height:1.3;
margin:1.1em 0 .5em;color:#141413}
.ai h1{font-size:1.2em}.ai h2{font-size:1.12em}.ai h3{font-size:1.05em}.ai h4,.ai h5,.ai h6{font-size:1em}
.ai ul,.ai ol{margin:0 0 .8em;padding-left:1.5em}
.ai li{margin:.2em 0}
.ai strong{font-weight:600}
.ai a{color:#C6613F;text-decoration:underline}
.ai code{font:0.86em Consolas,monospace;background:#F0EEE6;color:inherit;padding:.12em .38em;border-radius:5px}
.ai blockquote{margin:0 0 .8em;padding:.3em 1em;color:#5C5B55;border-left:3px solid #E0DDD2}
.ai hr{border:0;height:1px;background:#E8E6DC;margin:1em 0}
.ai table{border-collapse:collapse;margin:0 0 1em;font-size:0.9em}
.ai th,.ai td{border:1px solid #E8E6DC;padding:5px 9px;text-align:left}
.ai th{background:#F0EEE6;font-family:'Segoe UI',Arial,sans-serif;font-weight:600;font-size:0.92em}
.code{position:relative;margin:0 0 1em}
.code pre{margin:0;background:#F5F4EE;border:1px solid #E8E6DC;border-radius:10px;padding:12px 14px;overflow:auto;
scrollbar-face-color:#D9D7CE;scrollbar-track-color:#F5F4EE;scrollbar-arrow-color:#A9A79E;
scrollbar-shadow-color:#D9D7CE;scrollbar-highlight-color:#F5F4EE;scrollbar-3dlight-color:#F5F4EE;scrollbar-darkshadow-color:#F5F4EE}
.code pre code{font:13px/1.5 Consolas,monospace;background:none;padding:0;color:#141413;border-radius:0}
.code .copy{display:none;position:absolute;top:8px;right:10px;font:11.5px 'Segoe UI',Arial,sans-serif;color:#73726C;
background:#FFFFFF;border:1px solid #E8E6DC;border-radius:6px;padding:2px 8px;text-decoration:none}
.code:hover .copy{display:block}
.code .c{color:#9C9A92;font-style:italic}
.code .s{color:#6E8B3D}
.code .n{color:#B4674E}
.code .k{color:#8B6FA8}
.meta{font-family:'Segoe UI',Arial,sans-serif;font-size:12px;color:#73726C;margin-top:8px}
.shot{display:flex;align-items:center;border:1px solid #E8E6DC;border-radius:12px;padding:8px;margin:6px 0 14px;background:#FFFFFF}
.shot img{display:block;max-height:120px;max-width:55%;border-radius:8px;border:1px solid #E8E6DC}
.shot .cap{margin-left:12px;font-size:12px;color:#73726C;font-family:'Segoe UI',Arial,sans-serif;line-height:1.4}
.busy{display:flex;align-items:center;font-family:'Segoe UI',Arial,sans-serif;font-size:13px;color:#73726C;margin:6px 0 18px}
.busy .dot{width:8px;height:8px;border-radius:50%;background:#D97757;margin-right:9px;
animation:pulse 1s ease-in-out infinite}
@keyframes pulse{0%{opacity:.3;transform:scale(.8)}50%{opacity:1;transform:scale(1)}100%{opacity:.3;transform:scale(.8)}}
html.dark{background:#262624;scrollbar-face-color:#3D3D3A;scrollbar-track-color:#262624;scrollbar-arrow-color:#9C9A92;
scrollbar-shadow-color:#3D3D3A;scrollbar-highlight-color:#262624;scrollbar-3dlight-color:#262624;scrollbar-darkshadow-color:#262624}
.dark body{background:#262624;color:#ECEBE4}
.dark #log{scrollbar-face-color:#4A4A46;scrollbar-track-color:#262624;scrollbar-arrow-color:#9C9A92;scrollbar-shadow-color:#4A4A46;scrollbar-highlight-color:#4A4A46;scrollbar-3dlight-color:#262624;scrollbar-darkshadow-color:#262624}
.dark #ta{color:#ECEBE4}
.dark #ta:-ms-input-placeholder{color:#9C9A92}
.dark .me{background:#141413;color:#ECEBE4}
.dark .card{background:#30302E;border-color:#3D3D3A;box-shadow:0 1px 6px rgba(0,0,0,.35)}
.dark .ghost{color:#9C9A92}
.dark .ghost:hover{background:#3D3D3A;color:#ECEBE4}
.dark #pill{border-color:#3D3D3A;color:#9C9A92}
.dark #pill:hover{background:#3D3D3A;color:#ECEBE4}
.dark #menu{background:#30302E;border-color:#3D3D3A;box-shadow:0 8px 24px rgba(0,0,0,.5)}
.dark .mi:hover{background:#3D3D3A}
.dark .mi-name{color:#ECEBE4}
.dark .mi-desc{color:#9C9A92}
.dark .send:disabled{background:#4A4A46}
.dark #status{color:#9C9A92}
.dark .ai{color:#ECEBE4}
.dark .ai h1,.dark .ai h2,.dark .ai h3,.dark .ai h4,.dark .ai h5,.dark .ai h6{color:#ECEBE4}
.dark .ai a{color:#E39B7F}
.dark .ai code{background:#1F1E1D}
.dark .ai blockquote{color:#B4B2A9;border-left-color:#4A4A46}
.dark .ai hr{background:#3D3D3A}
.dark .ai th,.dark .ai td{border-color:#3D3D3A}
.dark .ai th{background:#30302E}
.dark .code pre{background:#1F1E1D;border-color:#3D3D3A}
.dark .code pre code{color:#ECEBE4}
.dark .code .copy{background:#30302E;border-color:#3D3D3A;color:#9C9A92}
.dark .code .c{color:#7F7D75}
.dark .code .s{color:#A8C480}
.dark .code .n{color:#E0A184}
.dark .code .k{color:#C3A7E0}
.dark .meta{color:#9C9A92}
.dark .shot{background:#30302E;border-color:#3D3D3A}
.dark .shot img{border-color:#3D3D3A}
.dark .shot .cap{color:#9C9A92}
.dark .busy{color:#9C9A92}
.dark .ai.err{color:#F0947C}
";

    string BodyHtml()
    {
        return @"
<div id='app'>
<div id='log'></div>
<div id='inputarea'>
<div class='card'>
<textarea id='ta' rows='1' placeholder='" + App.T("Ask something about the screen…", "Ekran hakkında bir şey sor…") + @"'></textarea>
<div class='row2'>
<button id='shot' class='ghost' type='button'><svg width='15' height='15' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z'></path><circle cx='12' cy='13' r='4'></circle></svg>" + App.T("New screenshot", "Yeni görüntü") + @"</button>
<div id='modelwrap'>
<button id='pill' type='button'><span id='pillLabel'>" + App.T("Auto", "Otomatik") + @"</span><svg width='10' height='10' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='3' stroke-linecap='round' stroke-linejoin='round' style='margin-left:4px'><polyline points='6 9 12 15 18 9'></polyline></svg></button>
<div id='menu'>
<div class='mi' data-i='0'><span class='mi-name'>" + App.T("Auto", "Otomatik") + @"</span><span class='mi-desc'>" + App.T("Haiku answers, escalates if needed", "Haiku cevaplar, gerekirse yükseltir") + @"</span><span class='ck'>✓</span></div>
<div class='mi' data-i='1'><span class='mi-name'>Haiku 5.5</span><span class='mi-desc'>" + App.T("Fastest and cheapest", "En hızlı ve ucuz") + @"</span><span class='ck'>✓</span></div>
<div class='mi' data-i='2'><span class='mi-name'>Sonnet 5.5</span><span class='mi-desc'>" + App.T("Smarter", "Daha akıllı") + @"</span><span class='ck'>✓</span></div>
<div class='mi' data-i='3'><span class='mi-name'>Opus 5.5</span><span class='mi-desc'>" + App.T("Most powerful, most expensive", "En güçlü, en pahalı") + @"</span><span class='ck'>✓</span></div>
</div>
</div>
<button id='send' class='send' type='button' disabled><svg width='16' height='16' viewBox='0 0 24 24' fill='none' stroke='#ffffff' stroke-width='2.4' stroke-linecap='round' stroke-linejoin='round'><line x1='12' y1='19' x2='12' y2='5'></line><polyline points='5 12 12 5 19 12'></polyline></svg></button>
</div>
</div>
</div>
<div id='status'></div>
</div>
";
    }

    string ScriptJs()
    {
        return @"
(function () {
  var log = document.getElementById('log');
  var ta = document.getElementById('ta');
  var send = document.getElementById('send');
  var menu = document.getElementById('menu');
  var pill = document.getElementById('pill');
  var pillLabel = document.getElementById('pillLabel');
  var statusEl = document.getElementById('status');
  var isBusy = false;
  var cur = 0;
  var names = " + App.Json.Serialize(new string[] { App.T("Auto", "Otomatik"), "Haiku 5.5", "Sonnet 5.5", "Opus 5.5" }) + @";

  function esc(s) {
    s = s == null ? '' : String(s);
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  function el(tag, cls) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    return e;
  }
  function scrollBottom() { log.scrollTop = log.scrollHeight; }
  function autosize() {
    ta.style.height = 'auto';
    var h = ta.scrollHeight;
    if (h > 168) h = 168;
    if (h < 25) h = 25;
    ta.style.height = h + 'px';
  }
  function updateSend() {
    var empty = /^\s*$/.test(ta.value);
    send.disabled = isBusy || empty;
  }
  function clearLog() { log.innerHTML = ''; }
  function addUser(t) {
    var d = el('div', 'msg me');
    d.innerHTML = esc(t).replace(/\n/g, '<br>');
    log.appendChild(d);
    scrollBottom();
  }
  function addAssistant(html, meta) {
    var d = el('div', 'msg ai');
    d.innerHTML = html || '';
    if (meta) {
      var m = el('div', 'meta');
      m.innerHTML = esc(meta);
      d.appendChild(m);
    }
    log.appendChild(d);
    scrollBottom();
  }
  function addError(t) {
    var d = el('div', 'msg ai err');
    d.innerHTML = esc(t).replace(/\n/g, '<br>');
    log.appendChild(d);
    scrollBottom();
  }
  function addShot(uri, caption) {
    var d = el('div', 'shot');
    var img = el('img');
    img.src = uri;
    img.alt = '';
    var cap = el('div', 'cap');
    cap.innerHTML = esc(caption);
    d.appendChild(img);
    d.appendChild(cap);
    log.appendChild(d);
    scrollBottom();
  }
  function setBusy(on, label) {
    var b = document.getElementById('busy');
    if (on) {
      if (!b) {
        b = el('div', 'msg busy');
        b.id = 'busy';
        b.appendChild(el('span', 'dot'));
        b.appendChild(el('span', 'lbl'));
        log.appendChild(b);
      }
      var l = b.getElementsByClassName('lbl');
      if (l && l.length) l[0].innerHTML = esc(label || '');
      scrollBottom();
    } else if (b && b.parentNode) {
      b.parentNode.removeChild(b);
    }
    isBusy = !!on;
    updateSend();
  }
  function setStatus(t) { statusEl.innerHTML = esc(t || ''); }
  function setModel(i) {
    i = i * 1;
    if (isNaN(i) || i < 0) i = 0;
    if (i > 3) i = 3;
    cur = i;
    pillLabel.innerHTML = names[i];
    var rows = menu.getElementsByTagName('div');
    for (var k = 0; k < rows.length; k++) {
      var ri = parseInt(rows[k].getAttribute('data-i'), 10);
      if (ri === i) rows[k].className = 'mi sel'; else rows[k].className = 'mi';
    }
  }
  function closeMenu() { menu.style.display = 'none'; }
  function focusInput() {
    try {
      ta.focus();
      var n = ta.value.length;
      if (ta.setSelectionRange) ta.setSelectionRange(n, n);
    } catch (e) {}
  }
  function closestTag(node, tag) {
    tag = tag.toUpperCase();
    while (node && node !== document) {
      if (node.tagName && node.tagName.toUpperCase() === tag) return node;
      node = node.parentNode;
    }
    return null;
  }
  function hasId(node, id) {
    while (node && node !== document) {
      if (node.id === id) return true;
      node = node.parentNode;
    }
    return false;
  }
  function doSend() {
    var t = ta.value.replace(/^\s+|\s+$/g, '');
    if (!t || isBusy) return;
    ta.value = '';
    autosize();
    updateSend();
    window.external.Send(t);
  }

  ta.addEventListener('keydown', function (e) {
    var k = e.keyCode || e.which;
    if (k === 13 && !e.shiftKey) { e.preventDefault(); doSend(); }
    else if (k === 27) { e.preventDefault(); window.external.HideWindow(); }
  }, false);
  ta.addEventListener('input', function () { autosize(); updateSend(); }, false);
  ta.addEventListener('keyup', function () { autosize(); updateSend(); }, false);
  send.addEventListener('click', function (e) { e.preventDefault(); doSend(); }, false);
  document.getElementById('shot').addEventListener('click', function (e) { e.preventDefault(); window.external.Recapture(); }, false);
  pill.addEventListener('click', function (e) {
    e.preventDefault();
    if (menu.style.display === 'block') closeMenu(); else menu.style.display = 'block';
  }, false);

  var rows = menu.getElementsByTagName('div');
  for (var k = 0; k < rows.length; k++) {
    (function (row) {
      row.addEventListener('click', function (e) {
        e.preventDefault();
        var i = parseInt(row.getAttribute('data-i'), 10);
        window.external.SetModel(i);
        setModel(i);
        closeMenu();
      }, false);
    })(rows[k]);
  }

  document.addEventListener('click', function (e) {
    var t = e.target || e.srcElement;
    var a = closestTag(t, 'A');
    if (a) {
      var href = a.getAttribute('href') || '';
      if (href.indexOf('sa:copy/') === 0) {
        e.preventDefault();
        var n = parseInt(href.substring(8), 10);
        if (!isNaN(n)) window.external.Copy(n);
      } else if (href.indexOf('http://') === 0 || href.indexOf('https://') === 0) {
        e.preventDefault();
        window.external.Open(href);
      }
    }
    if (!hasId(t, 'modelwrap')) closeMenu();
  }, false);

  document.addEventListener('keydown', function (e) {
    var k = e.keyCode || e.which;
    if (k === 27) window.external.HideWindow();
  }, false);

  window.clearLog = clearLog;
  window.addShot = addShot;
  window.addUser = addUser;
  window.addAssistant = addAssistant;
  window.addError = addError;
  window.setBusy = setBusy;
  window.setStatus = setStatus;
  window.setModel = setModel;
  window.focusInput = focusInput;

  autosize();
  updateSend();
  setModel(0);
})();
";
    }
}

[ComVisible(true)]
public class Bridge
{
    readonly ChatForm f;
    internal Bridge(ChatForm form) { f = form; }

    public void Send(string text) { f.Post(delegate { f.SendText(text); }); }
    public void SetModel(int i) { f.Post(delegate { f.SetModelFromBridge(i); }); }
    public void Recapture() { f.Post(delegate { f.RecaptureFromBridge(); }); }
    public void Copy(int n) { f.Post(delegate { f.CopyFromBridge(n); }); }
    public void Open(string url) { f.Post(delegate { f.OpenFromBridge(url); }); }
    public void HideWindow() { f.Post(delegate { f.Hide(); }); }
}
