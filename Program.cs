using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RainyGlass;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            int snap = Array.IndexOf(args, "--snapshot");
            if (snap >= 0)
            {
                Snapshot.Run(args[snap + 1], Arg(args, "--bg"), float.Parse(Arg(args, "--seconds") ?? "6"),
                    int.Parse(Arg(args, "--frames") ?? "0"), int.Parse(Arg(args, "--every") ?? "2"),
                    float.Parse(Arg(args, "--intensity") ?? "1"));
                return;
            }

            int monitor = int.Parse(Arg(args, "--monitor") ?? "-1");
            bool hideFromCapture = !args.Contains("--no-exclude");
            Application.Run(new TrayContext(monitor, hideFromCapture));
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            if (!args.Contains("--snapshot"))
                MessageBox.Show(ex.Message, "APRG", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>タスクトレイに常駐し、オーバーレイの表示と描画スレッドを管理する。</summary>
sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly List<OverlayForm> _forms = new();
    readonly List<Thread> _threads = new();
    volatile bool _running = true;
    volatile bool _paused;
    volatile int _clearGeneration;
    volatile float _intensity = 1f;
    volatile bool _raining = true;

    public TrayContext(int monitorIndex, bool hideFromCapture)
    {
        // 既定は全モニター。--monitor N 指定時はそのモニターだけ
        var screens = Screen.AllScreens;
        var targets = monitorIndex >= 0 && monitorIndex < screens.Length ? new[] { screens[monitorIndex] } : screens;

        for (int i = 0; i < targets.Length; i++)
        {
            var bounds = targets[i].Bounds;
            var form = new OverlayForm(bounds);
            form.Show();
            form.ApplyBounds();
            if (hideFromCapture)
                Native.SetWindowDisplayAffinity(form.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            _forms.Add(form);

            IntPtr hwnd = form.Handle;
            int index = i;
            var thread = new Thread(() => RenderLoop(hwnd, bounds, index)) { IsBackground = true, Name = $"Render{i}" };
            _threads.Add(thread);
            thread.Start();
        }

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Anti-Productivity RainyGlass", Visible = true, ContextMenuStrip = BuildMenu() };
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("一時停止 (非表示)") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _paused = pause.Checked;
            foreach (var f in _forms)
            {
                if (_paused) f.Hide();
                else { f.Show(); f.ApplyBounds(); }
            }
        };
        var rain = new ToolStripMenuItem("雨を降らせる") { Checked = true, CheckOnClick = true };
        rain.CheckedChanged += (_, _) => _raining = rain.Checked;

        var amount = new ToolStripMenuItem("雨量");
        foreach (var (label, v) in new[] { ("小雨", 0.5f), ("普通", 1f), ("強い雨", 1.8f), ("豪雨", 3f) })
        {
            var item = new ToolStripMenuItem(label) { Checked = v == 1f };
            item.Click += (_, _) =>
            {
                _intensity = v;
                foreach (ToolStripMenuItem o in amount.DropDownItems) o.Checked = o == item;
            };
            amount.DropDownItems.Add(item);
        }

        var clear = new ToolStripMenuItem("ガラスを拭く");
        clear.Click += (_, _) => _clearGeneration++;
        var exit = new ToolStripMenuItem("終了");
        exit.Click += (_, _) => ExitThread();

        menu.Items.AddRange(new ToolStripItem[] { pause, rain, amount, clear, new ToolStripSeparator(), exit });
        return menu;
    }

    void RenderLoop(IntPtr hwnd, Rectangle bounds, int index)
    {
        try
        {
            using var renderer = Renderer.CreateLive(hwnd, bounds);
            if (!renderer.CaptureOk) Log.Write("Desktop capture unavailable; drops will show a flat background.");
            var sim = new RainSim(bounds.Width, bounds.Height);
            sim.Prefill();
            var sw = Stopwatch.StartNew();
            double last = 0, lastTop = 0;
            int clearSeen = _clearGeneration;
            while (_running)
            {
                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)(now - last);
                last = now;

                if (_paused) { Thread.Sleep(100); continue; }
                if (clearSeen != _clearGeneration)
                {
                    clearSeen = _clearGeneration;
                    sim.Clear();
                    renderer.ClearGlass();
                }
                sim.Intensity = _intensity;
                sim.Raining = _raining;
                sim.Update(dt);
                renderer.Render(sim, dt);

                // タスクバー等に隠されないよう時々最前面を取り直す
                if (now - lastTop > 2) { lastTop = now; Native.KeepTopmost(hwnd); }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"monitor {index} {bounds}: {ex}");
            _forms[0].BeginInvoke(() =>
            {
                MessageBox.Show(ex.Message, "APRG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
            });
        }
    }

    protected override void ExitThreadCore()
    {
        _running = false;
        foreach (var t in _threads) t.Join(2000);
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var f in _forms) f.Close();
        base.ExitThreadCore();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddBezier(16, 2, 10, 12, 6, 16, 6, 21);
            path.AddArc(6, 11, 20, 20, 180, -180);
            path.AddBezier(26, 21, 26, 16, 22, 12, 16, 2);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(150, 210, 255), Color.FromArgb(30, 110, 200), 90f);
            g.FillPath(brush, path);
            g.FillEllipse(Brushes.White, 11, 17, 5, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>クリックを透過し、フォーカスも奪わない全画面オーバーレイ。</summary>
sealed class OverlayForm : Form
{
    readonly Rectangle _bounds;

    public OverlayForm(Rectangle bounds)
    {
        _bounds = bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        Text = "APRG";
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>DPI の違うモニターでも物理ピクセルでぴったり覆うように位置を合わせ直す。</summary>
    public void ApplyBounds() => Native.SetBounds(Handle, _bounds);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_NOREDIRECTIONBITMAP;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
        const int WM_DPICHANGED = 0x02E0;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        if (m.Msg == WM_DPICHANGED) { ApplyBounds(); m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }
}

static class Native
{
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint LWA_ALPHA = 0x2;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void SetBounds(IntPtr hwnd, Rectangle r)
    {
        const uint SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        SetWindowPos(hwnd, new IntPtr(-1), r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public static void KeepTopmost(IntPtr hwnd)
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}

/// <summary>
/// --snapshot: 画像を背景にシミュレーションを回し、結果を PNG に保存する (見た目の調整用)。
/// --frames N を付けると、ウォームアップ後に N 枚の連番 PNG を書き出す (出力パスの {n} が番号になる)。
/// </summary>
static class Snapshot
{
    public static void Run(string outPath, string? bgPath, float seconds, int frameCount, int every, float intensity)
    {
        using var bg = bgPath != null ? new Bitmap(bgPath) : MakeDemoBackground(1920, 1080);
        int w = bg.Width, h = bg.Height;
        var bgra = ToBgra(bg);
        using var renderer = Renderer.CreateOffscreen(w, h);
        renderer.SetBackground(bgra, w * 4);
        var sim = new RainSim(w, h) { Intensity = intensity };
        sim.Prefill();
        const float dt = 1f / 60f;
        int warmup = (int)(seconds / dt);
        for (int i = 0; i < warmup; i++)
        {
            sim.Update(dt);
            renderer.Render(sim, dt);
        }
        if (frameCount <= 0)
        {
            Save(renderer.ReadComposited(bgra), w, h, outPath);
            return;
        }
        for (int f = 0; f < frameCount; f++)
        {
            for (int k = 0; k < every; k++)
            {
                sim.Update(dt);
                renderer.Render(sim, dt);
            }
            Save(renderer.ReadComposited(bgra), w, h, outPath.Replace("{n}", f.ToString("D4")));
        }
    }

    static void Save(byte[] bgra, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }

    static byte[] ToBgra(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var buf = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
        bmp.UnlockBits(data);
        return buf;
    }

    /// <summary>デモ用の背景: 雨の夜景のボケと、がんばって仕事中のエディタ。</summary>
    static Bitmap MakeDemoBackground(int w, int h)
    {
        var rng = new Random(7);
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);

        using (var sky = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(18, 24, 48), Color.FromArgb(60, 46, 78), 90f))
            g.FillRectangle(sky, 0, 0, w, h);

        // 街の灯りのボケ (中心から縁へ少しだけ明るさが変わる円)
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color[] lights = { Color.FromArgb(255, 190, 90), Color.FromArgb(255, 120, 80), Color.FromArgb(90, 200, 230), Color.FromArgb(240, 230, 200) };
        for (int i = 0; i < 90; i++)
        {
            var c = lights[rng.Next(lights.Length)];
            float r = rng.Next(16, 72);
            float x = rng.Next(w), y = h * (0.35f + 0.65f * (float)rng.NextDouble());
            int a = rng.Next(50, 150);
            using var path = new GraphicsPath();
            path.AddEllipse(x - r, y - r, r * 2, r * 2);
            using var pb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(a * 2 / 3, c),
                SurroundColors = new[] { Color.FromArgb(a, c) },
            };
            g.FillPath(pb, path);
        }

        // 仕事中のエディタ
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var win = new Rectangle(220, 170, 1080, 640);
        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(shadow, win.X + 10, win.Y + 14, win.Width, win.Height);
        using (var body = new SolidBrush(Color.FromArgb(236, 30, 32, 40))) g.FillRectangle(body, win);
        using (var bar = new SolidBrush(Color.FromArgb(255, 44, 46, 58))) g.FillRectangle(bar, win.X, win.Y, win.Width, 44);
        Color[] dots = { Color.FromArgb(255, 95, 87), Color.FromArgb(255, 189, 46), Color.FromArgb(40, 201, 64) };
        for (int i = 0; i < 3; i++) using (var d = new SolidBrush(dots[i])) g.FillEllipse(d, win.X + 18 + i * 26, win.Y + 14, 16, 16);
        using var titleFont = new Font("Segoe UI", 14);
        using var titleBrush = new SolidBrush(Color.FromArgb(170, 175, 190));
        g.DrawString("todo.md — very productive day", titleFont, titleBrush, win.X + 110, win.Y + 10);

        using var mono = new Font("Consolas", 22);
        (string text, Color color)[] lines =
        {
            ("## Today's tasks", Color.FromArgb(120, 180, 255)),
            ("", Color.White),
            ("- [x] Ask AI to write the code", Color.FromArgb(150, 220, 150)),
            ("- [x] Ask AI to review the code", Color.FromArgb(150, 220, 150)),
            ("- [x] Ask AI to write the commit message", Color.FromArgb(150, 220, 150)),
            ("- [ ] Do one thing by myself", Color.FromArgb(230, 230, 235)),
            ("- [ ] Stop watching the raindrops race", Color.FromArgb(230, 230, 235)),
            ("", Color.White),
            ("Productivity  [##########----------]  50%", Color.FromArgb(255, 200, 110)),
            ("Forecast      rain, all day", Color.FromArgb(255, 200, 110)),
        };
        for (int i = 0; i < lines.Length; i++)
        {
            using var lb = new SolidBrush(lines[i].color);
            using var nb = new SolidBrush(Color.FromArgb(90, 95, 110));
            g.DrawString((i + 1).ToString().PadLeft(2), mono, nb, win.X + 20, win.Y + 80 + i * 50);
            g.DrawString(lines[i].text, mono, lb, win.X + 90, win.Y + 80 + i * 50);
        }
        return bmp;
    }
}
