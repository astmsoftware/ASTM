using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AzulGrooveAssistente;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Assistente());
    }
}

/// <summary>Painel com pintura própria (sem tremer) usado para as animações.</summary>
sealed class Canvas : Panel
{
    public Action<Graphics>? Draw;
    public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Draw?.Invoke(e.Graphics);
    }
}

sealed record Rel(string Tag, string Date, bool Pre);

sealed class Assistente : Form
{
    // >>> Repositório de onde as versões são baixadas <<<
    const string Repo = "andrsodremiranda/Azul-Groove-app";
    const string RelUrl = "https://github.com/" + Repo + "/releases";

    static readonly Color Bg = Color.FromArgb(15, 17, 23), Card = Color.FromArgb(24, 27, 37), Line = Color.FromArgb(39, 44, 59),
        Ac = Color.FromArgb(88, 101, 242), Mut = Color.FromArgb(154, 163, 178), Warn = Color.FromArgb(255, 180, 84), Bad = Color.FromArgb(255, 107, 107);

    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGroove-Assistente");
        return h;
    }

    // Caminhos do Azul Groove
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string DataDir => Path.Combine(Local, "AzulGroove");
    static string SetupExe => Path.Combine(Local, "Programs", "Azul Groove", "AzulGroove.exe");
    const string PortName = "AzulGroove-Portable.exe";
    static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    static string Memo => Path.Combine(Local, "AzulGrooveAssistente", "portable.txt");   // lembra onde o Portable foi salvo
    static string? PortExe { get { try { var p = File.ReadAllText(Memo).Trim(); return File.Exists(p) ? p : null; } catch { return null; } } }

    Canvas header = null!, splash = null!, bar = null!, chip = null!, steps = null!;
    Panel pHome = null!, pVers = null!, pProg = null!, pErr = null!;
    Label lblStep = null!, lblDetail = null!, lblPct = null!, lblErr = null!, lblModeV = null!, lblRepair = null!;
    RButton btnLatest = null!, btnRepair = null!, cardSetup = null!, cardPort = null!;
    string statusText = ""; bool statusOk; int stage;
    ComboBox cbVer = null!;
    Bitmap? logo;
    readonly List<Rel> rels = new();
    string? latestTag;
    int mode;                       // 0 = Setup, 1 = Portable
    double prog, shown;
    bool busy, creeping;
    readonly DateTime t0 = DateTime.Now;
    double T => (DateTime.Now - t0).TotalSeconds;

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, int w, int l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    public Assistente()
    {
        Text = "Assistente de instalação do Azul Groove";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 540);
        BackColor = Bg; ForeColor = Color.White; DoubleBuffered = true;
        Font = new Font("Segoe UI", 10f);
        try
        {
            using (var s = Res()) if (s != null) Icon = new Icon(s);
            using (var s = Res()) if (s != null) { using var big = new Icon(s, new Size(128, 128)); logo = big.ToBitmap(); }
        }
        catch { }

        header = new Canvas { Bounds = new Rectangle(0, 0, 780, 150), Draw = DrawHeader };
        header.MouseDown += Drag;
        Controls.Add(header);
        TopButton("×", 742, () => { if (!busy) Close(); });
        TopButton("–", 710, () => WindowState = FormWindowState.Minimized);

        pHome = Page(); pVers = Page(); pProg = Page(); pErr = Page();
        BuildHome(); BuildVers(); BuildProg(); BuildErr();

        splash = new Canvas { Bounds = ClientRectangle, Draw = DrawSplash };
        splash.MouseDown += Drag;
        Controls.Add(splash);
        splash.BringToFront();

        var tm = new System.Windows.Forms.Timer { Interval = 33 };
        tm.Tick += (_, _) => Tick();
        tm.Start();
    }

    static Stream? Res() => typeof(Assistente).Assembly.GetManifestResourceStream("app.ico");

    // ---------------- janela sem borda ----------------
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
    void Drag(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); }
    }
    void TopButton(string text, int x, Action click)
    {
        var l = new Label { Text = text, Left = x, Top = 8, Width = 30, Height = 26, ForeColor = Mut, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 11f) };
        l.MouseEnter += (_, _) => l.ForeColor = Color.White;
        l.MouseLeave += (_, _) => l.ForeColor = Mut;
        l.Click += (_, _) => click();
        header.Controls.Add(l);
    }

    // ---------------- helpers de interface ----------------
    Panel Page()
    {
        var p = new Panel { Bounds = new Rectangle(0, 150, 780, 390), BackColor = Bg, Visible = false };
        Controls.Add(p);
        return p;
    }
    static Label L(Control p, string t, int x, int y, int w, int h, float size, Color c, bool bold = false)
    {
        var l = new Label { Text = t, Left = x, Top = y, Width = w, Height = h, ForeColor = c, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) };
        p.Controls.Add(l);
        return l;
    }
    static RButton B(Control p, string t, int x, int y, int w, int h, bool primary, Action click)
    {
        var b = new RButton { Text = t, Left = x, Top = y, Width = w, Height = h, Primary = primary };
        b.Click += (_, _) => click();
        p.Controls.Add(b);
        return b;
    }
    static Canvas Note(Control p, string text, int x, int y, int w, int h)
    {
        var c = new Canvas { Bounds = new Rectangle(x, y, w, h), BackColor = Bg };
        c.Draw = g =>
        {
            using var path = Round(new RectangleF(0, 0, w - 1, h - 1), 12);
            using (var br = new SolidBrush(Color.FromArgb(30, 255, 180, 84))) g.FillPath(br, path);
            using (var pen = new Pen(Color.FromArgb(70, 255, 180, 84))) g.DrawPath(pen, path);
            using var f = new Font("Segoe UI", 9.5f);
            using var tb = new SolidBrush(Color.FromArgb(255, 205, 140));
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            float cy = h / 2f;
            using (var tri = new SolidBrush(Color.FromArgb(255, 180, 84)))
                g.FillPolygon(tri, new[] { new PointF(24, cy - 9), new PointF(33, cy + 7), new PointF(15, cy + 7) });
            using (var ex = new Font("Segoe UI Semibold", 8f)) g.DrawString("!", ex, Brushes.Black, 20.2f, cy - 7f);
            g.DrawString(text, f, tb, new RectangleF(44, 0, w - 58, h), sf);
        };
        p.Controls.Add(c);
        return c;
    }
    void ShowPage(Panel p)
    {
        foreach (var pg in new[] { pHome, pVers, pProg, pErr }) pg.Visible = pg == p;
        if (p == pVers) lblModeV.Text = "Formato escolhido: " + (mode == 0 ? "Setup (instalador)" : "Portable") + "  ·  mude na tela anterior.";
    }
    void SetMode(int m)
    {
        mode = m;
        Style(cardSetup, m == 0); Style(cardPort, m == 1);
    }
    static void Style(RButton b, bool on) { b.Selected = on; b.Invalidate(); }

    // ---------------- telas ----------------
    void BuildHome()
    {
        chip = new Canvas { Bounds = new Rectangle(40, 14, 700, 36), BackColor = Bg };
        chip.Draw = g =>
        {
            using var f = new Font("Segoe UI", 10.5f);
            float w = g.MeasureString(statusText, f).Width + 50;
            using var path = Round(new RectangleF(0, 2, w, 30), 15);
            using (var br = new SolidBrush(statusOk ? Color.FromArgb(34, 61, 220, 151) : Color.FromArgb(30, 154, 163, 178))) g.FillPath(br, path);
            using (var dot = new SolidBrush(statusOk ? Color.FromArgb(61, 220, 151) : Mut)) g.FillEllipse(dot, 14, 12, 10, 10);
            g.DrawString(statusText, f, Brushes.White, 32, 6);
        };
        pHome.Controls.Add(chip);

        L(pHome, "Como você quer instalar?", 40, 62, 500, 22, 10f, Mut);
        cardSetup = B(pHome, "Setup (instalador)", 40, 90, 335, 84, false, () => SetMode(0));
        cardSetup.Sub = "Instala, cria atalhos e abre o app";
        cardPort = B(pHome, "Portable (sem instalar)", 405, 90, 335, 84, false, () => SetMode(1));
        cardPort.Sub = "Um único .exe, você escolhe a pasta";

        btnLatest = B(pHome, "Baixar a versão mais recente", 40, 196, 440, 62, true, () => Start(latestTag, mode, false));
        btnLatest.Glyph = 1;
        B(pHome, "Outras versões…", 496, 196, 244, 62, false, () => ShowPage(pVers));
        Note(pHome, "Algumas versões podem exigir atualização obrigatória para a versão mais recente.", 40, 272, 700, 40);

        btnRepair = B(pHome, "Reparar instalação", 40, 326, 250, 46, false, () =>
        {
            var d = Detect();
            if (d != null) Start(null, d.Value.kind == "Setup" ? 0 : 1, true);
        });
        btnRepair.Glyph = 2;
        lblRepair = L(pHome, "Reinstala a versão mais recente e mantém seus dados.", 306, 337, 440, 24, 9.5f, Mut);
    }

    void BuildVers()
    {
        L(pVers, "Escolha uma versão", 40, 14, 700, 34, 18f, Color.White, true);
        lblModeV = L(pVers, "", 40, 54, 700, 22, 10f, Mut);
        cbVer = new ComboBox { Left = 40, Top = 90, Width = 700, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Card, ForeColor = Color.White, Font = new Font("Segoe UI", 11.5f) };
        pVers.Controls.Add(cbVer);
        Note(pVers, "Algumas versões podem exigir atualização obrigatória para a versão mais recente. Se o app avisar, abra este assistente e baixe a mais recente.", 40, 140, 700, 62);
        B(pVers, "Instalar esta versão", 40, 316, 260, 54, true, () =>
        {
            if (cbVer.SelectedIndex >= 0 && cbVer.SelectedIndex < rels.Count) Start(rels[cbVer.SelectedIndex].Tag, mode, false);
        });
        B(pVers, "Voltar", 316, 316, 140, 54, false, () => ShowPage(pHome));
    }

    void BuildProg()
    {
        steps = new Canvas { Bounds = new Rectangle(40, 14, 700, 70), BackColor = Bg, Draw = DrawSteps };
        pProg.Controls.Add(steps);
        lblStep = L(pProg, "Preparando…", 40, 108, 700, 40, 20f, Color.White, true);
        lblDetail = L(pProg, "", 40, 152, 700, 24, 10.5f, Mut);
        bar = new Canvas { Bounds = new Rectangle(40, 198, 630, 14), BackColor = Bg, Draw = DrawBar };
        pProg.Controls.Add(bar);
        lblPct = L(pProg, "0%", 676, 190, 64, 30, 14f, Color.White, true);
        lblPct.TextAlign = ContentAlignment.MiddleRight;
        L(pProg, "Não feche esta janela. Seus dados ficam guardados e voltam sozinhos no final.", 40, 330, 700, 22, 9.5f, Mut);
    }

    void BuildErr()
    {
        L(pErr, "Algo deu errado", 40, 24, 700, 36, 18f, Bad, true);
        lblErr = L(pErr, "", 40, 76, 700, 190, 11f, Color.White);
        B(pErr, "Voltar ao início", 40, 300, 200, 52, true, () => { RefreshInstalled(); ShowPage(pHome); });
    }

    // ---------------- animações ----------------
    void Tick()
    {
        if (creeping && prog < 0.9) prog += 0.0015;
        shown += (prog - shown) * 0.12;
        if (Math.Abs(prog - shown) < 0.0005) shown = prog;

        if (splash.Visible)
        {
            splash.Invalidate();
            if (T > 2.6) { splash.Visible = false; _ = InitAsync(); }
            return;
        }
        header.Invalidate();
        if (pProg.Visible) { bar.Invalidate(); steps.Invalidate(); lblPct.Text = (int)(shown * 100) + "%"; }
    }

    internal static GraphicsPath Round(RectangleF r, float rad)
    {
        rad = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void DrawBars(Graphics g, RectangleF a, double t, bool active)
    {
        const int n = 11; const float gap = 6f;
        float w = (a.Width - gap * (n - 1)) / n;
        using var br = new LinearGradientBrush(a, Color.FromArgb(122, 156, 255), Color.FromArgb(88, 101, 242), 90f);
        for (int i = 0; i < n; i++)
        {
            double v = 0.5 + 0.5 * Math.Sin(t * (active ? 6 : 3) + i * 0.75);
            float h = (float)(a.Height * (active ? 0.2 + 0.8 * v : 0.12 + 0.45 * v));
            using var path = Round(new RectangleF(a.X + i * (w + gap), a.Bottom - h, w, h), w / 2);
            g.FillPath(br, path);
        }
    }

    static void Glow(Graphics g, float cx, float cy, float r, Color c)
    {
        using var p = new GraphicsPath();
        p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        using var pb = new PathGradientBrush(p) { CenterColor = Color.FromArgb(70, c), SurroundColors = new[] { Color.FromArgb(0, c) } };
        g.FillPath(pb, p);
    }

    void DrawHeader(Graphics g)
    {
        var r = header.ClientRectangle;
        using (var br = new LinearGradientBrush(r, Color.FromArgb(22, 26, 64), Bg, 90f)) g.FillRectangle(br, r);
        Glow(g, 120, 70, 150, Ac);
        Glow(g, 650, 30, 170, Color.FromArgb(31, 182, 255));
        if (logo != null)
        {
            using var lp = Round(new RectangleF(36, 32, 80, 80), 22);
            g.SetClip(lp); g.DrawImage(logo, 36, 32, 80, 80); g.ResetClip();
        }
        using var f1 = new Font("Segoe UI Semibold", 22f);
        using var f2 = new Font("Segoe UI", 10.5f);
        using var mb = new SolidBrush(Mut);
        g.DrawString("Azul Groove", f1, Brushes.White, 130, 36);
        g.DrawString("Assistente de instalação", f2, mb, 133, 80);
        DrawBars(g, new RectangleF(500, 44, 220, 62), T, busy);
    }

    void DrawSteps(Graphics g)
    {
        string[] n = { "Baixar", "Preparar", "Instalar", "Abrir" };
        float gap = steps.Width / 4f;
        using var f = new Font("Segoe UI", 9.5f);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < 4; i++)
        {
            float cx = gap * i + gap / 2f, cy = 20f;
            bool done = i < stage, cur = i == stage;
            if (i < 3) { using var pen = new Pen(i < stage ? Ac : Line, 3f); g.DrawLine(pen, cx + 16, cy, cx + gap - 16, cy); }
            if (cur)
            {
                float pu = (float)(6 + 5 * Math.Sin(T * 5));
                using var gl = new SolidBrush(Color.FromArgb(55, 88, 101, 242));
                g.FillEllipse(gl, cx - 14 - pu / 2, cy - 14 - pu / 2, 28 + pu, 28 + pu);
            }
            using (var fill = new SolidBrush(done || cur ? Ac : Card)) g.FillEllipse(fill, cx - 14, cy - 14, 28, 28);
            using (var op = new Pen(done || cur ? Ac : Line, 2f)) g.DrawEllipse(op, cx - 14, cy - 14, 28, 28);
            if (done) { using var ck = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(ck, new[] { new PointF(cx - 6, cy), new PointF(cx - 2, cy + 4), new PointF(cx + 6, cy - 4) }); }
            else g.DrawString((i + 1).ToString(), f, Brushes.White, new RectangleF(cx - 14, cy - 12, 28, 24), sf);
            using var tb = new SolidBrush(cur ? Color.White : Mut);
            g.DrawString(n[i], f, tb, new RectangleF(cx - gap / 2, cy + 22, gap, 22), sf);
        }
    }

    void DrawSplash(Graphics g)
    {
        var r = splash.ClientRectangle; double t = T;
        using (var br = new LinearGradientBrush(r, Color.FromArgb(24, 32, 96), Bg, 90f)) g.FillRectangle(br, r);
        float cx = r.Width / 2f, cy = r.Height / 2f - 40;
        for (int k = 0; k < 2; k++)
        {
            double p = (t * 0.7 + k * 0.5) % 1;
            using var pen = new Pen(Color.FromArgb((int)((1 - p) * 110), 31, 182, 255), 2f);
            float rad = (float)(70 + p * 110);
            g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
        }
        double x = Math.Min(1, t / 0.9) - 1;
        double s = 1 + 2.70158 * x * x * x + 1.70158 * x * x;      // easeOutBack
        float size = (float)(120 * s);
        if (logo != null && size > 1) g.DrawImage(logo, cx - size / 2, cy - size / 2, size, size);

        int a = (int)(255 * Math.Clamp((t - 0.6) / 0.6, 0, 1));
        using var f1 = new Font("Segoe UI Semibold", 28f);
        using var f2 = new Font("Segoe UI", 12f);
        using var sf = new StringFormat { Alignment = StringAlignment.Center };
        using (var b1 = new SolidBrush(Color.FromArgb(a, 255, 255, 255))) g.DrawString("Azul Groove", f1, b1, cx, cy + 85, sf);
        using (var b2 = new SolidBrush(Color.FromArgb(a, Mut))) g.DrawString("Assistente de instalação", f2, b2, cx, cy + 135, sf);
        DrawBars(g, new RectangleF(cx - 90, r.Height - 100, 180, 36), t, true);
    }

    void DrawBar(Graphics g)
    {
        var r = new RectangleF(0, 0, bar.Width - 1, bar.Height - 1);
        using (var p1 = Round(r, r.Height / 2)) using (var b1 = new SolidBrush(Card)) g.FillPath(b1, p1);
        float w = (float)(r.Width * shown);
        if (w <= r.Height) return;
        var fr = new RectangleF(0, 0, w, r.Height);
        using var p2 = Round(fr, r.Height / 2);
        using (var b2 = new LinearGradientBrush(fr, Ac, Color.FromArgb(31, 182, 255), 0f)) g.FillPath(b2, p2);
        g.SetClip(p2);
        float sx = (float)((T * 220) % (w + 120)) - 120;
        using (var sh = new LinearGradientBrush(new RectangleF(sx, 0, 120, r.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(90, 255, 255, 255), 0f))
            g.FillRectangle(sh, sx, 0, 120, r.Height);
        g.ResetClip();
    }

    // ---------------- estado e versões ----------------
    async Task InitAsync()
    {
        SetMode(0);
        RefreshInstalled();
        ShowPage(pHome);
        await LoadReleasesAsync();
    }

    static string Ver(string path)
    {
        try { var v = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?"; return v.EndsWith(".0") ? v[..^2] : v; }
        catch { return "?"; }
    }

    static (string kind, string path, string ver)? Detect()
    {
        if (File.Exists(SetupExe)) return ("Setup", SetupExe, Ver(SetupExe));
        var pe = PortExe;
        if (pe != null) return ("Portable", pe, Ver(pe));
        return null;
    }

    void RefreshInstalled()
    {
        var d = Detect();
        statusOk = d != null;
        statusText = d == null ? "O Azul Groove ainda não está instalado neste computador"
                               : $"Azul Groove {d.Value.ver} instalado  ·  {d.Value.kind}";
        chip.Invalidate();
        btnRepair.Visible = lblRepair.Visible = d != null;
        if (d != null) SetMode(d.Value.kind == "Setup" ? 0 : 1);
    }

    async Task LoadReleasesAsync()
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
            using var doc = JsonDocument.Parse(json);
            rels.Clear(); cbVer.Items.Clear();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean()) continue;
                bool hasExe = false;
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                    if ((a.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) hasExe = true;
                if (!hasExe) continue;
                var tag = r.GetProperty("tag_name").GetString() ?? "";
                var date = r.TryGetProperty("published_at", out var pa) && (pa.GetString() ?? "").Length >= 10 ? pa.GetString()![..10] : "";
                rels.Add(new Rel(tag, date, r.GetProperty("prerelease").GetBoolean()));
            }
            foreach (var r in rels) cbVer.Items.Add($"{r.Tag}   ·   {r.Date}{(r.Pre ? "   (pré-lançamento)" : "")}");
            if (cbVer.Items.Count > 0) cbVer.SelectedIndex = 0;
            var lat = rels.FirstOrDefault(x => !x.Pre);
            if (lat != null) { latestTag = lat.Tag; btnLatest.Text = $"Baixar a versão mais recente  ({lat.Tag})"; }
        }
        catch
        {
            cbVer.Items.Clear();
            cbVer.Items.Add("Não foi possível carregar a lista (verifique a internet)");
            cbVer.SelectedIndex = 0;
        }
    }

    // ---------------- baixar, instalar e reparar ----------------
    void SetStep(string s, string d, double p, int st = -1)
    {
        lblStep.Text = s; lblDetail.Text = d; prog = p;
        if (st >= 0) stage = st;
    }

    string? PickFolder()
    {
        using var f = new FolderBrowserDialog { Description = "Escolha onde salvar o Azul Groove Portable", UseDescriptionForTitle = true,
            SelectedPath = Downloads, ShowNewFolderButton = true };
        return f.ShowDialog(this) == DialogResult.OK ? f.SelectedPath : null;
    }

    void Start(string? tag, int kind, bool repair)
    {
        string? dest = null;
        if (kind == 1)
        {
            if (repair) { var d = Detect(); dest = d == null ? null : Path.GetDirectoryName(d.Value.path); }
            else { dest = PickFolder(); if (dest == null) return; }
        }
        _ = RunAsync(tag, kind, repair, dest);
    }

    async Task RunAsync(string? tag, int kind, bool repair, string? dest)
    {
        if (busy) return;
        busy = true; prog = 0; shown = 0; stage = 0;
        ShowPage(pProg);
        string tmp = Path.Combine(Path.GetTempPath(), "AzulGrooveAssistente");
        string? backup = null;
        try
        {
            Directory.CreateDirectory(tmp);
            string file = kind == 0 ? "AzulGroove-Setup.exe" : PortName;
            string url = tag == null ? $"{RelUrl}/latest/download/{file}" : $"{RelUrl}/download/{Uri.EscapeDataString(tag)}/{file}";
            string dl = Path.Combine(tmp, file);

            SetStep(repair ? "Reparando o Azul Groove" : "Baixando o Azul Groove", "Conectando…", 0.02, 0);
            await DownloadAsync(url, dl);

            SetStep("Preparando…", "Fechando o Azul Groove", 0.62, 1);
            await KillAppAsync();

            if (Directory.Exists(DataDir))
            {
                SetStep("Protegendo seus dados…", "Cópia de segurança temporária", 0.68, 1);
                backup = Path.Combine(tmp, "dados_" + Guid.NewGuid().ToString("N"));
                var b0 = backup;
                await Task.Run(() => MoveDir(DataDir, b0));
            }

            string exe;
            if (kind == 0)
            {
                SetStep("Instalando o Azul Groove…", "Isso leva alguns segundos", 0.78, 2);
                creeping = true;
                exe = await InstallSetupAsync(dl);
                creeping = false;
            }
            else
            {
                SetStep("Salvando o Portable…", dest ?? "", 0.78, 2);
                var dir = dest ?? Downloads;
                exe = Path.Combine(dir, PortName);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(dl, exe, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(Memo)!);
                    File.WriteAllText(Memo, exe);
                });
            }

            if (backup != null)
            {
                SetStep("Restaurando seus dados…", "", 0.92, 2);
                var b1 = backup;
                await Task.Run(() => { if (Directory.Exists(DataDir)) DeleteDir(DataDir); MoveDir(b1, DataDir); });
                backup = null;
            }

            SetStep("Tudo pronto!", kind == 1 ? "Abrindo a pasta e o Azul Groove…" : "Abrindo o Azul Groove…", 1.0, 3);
            await Task.Delay(1300);
            if (kind == 1) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true });
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            busy = false;
            await Task.Delay(400);
            Close();
        }
        catch (Exception ex)
        {
            creeping = false;
            string extra = "";
            if (backup != null)
            {
                try
                {
                    var b2 = backup;
                    await Task.Run(() => { if (Directory.Exists(DataDir)) DeleteDir(DataDir); MoveDir(b2, DataDir); });
                    backup = null;
                    extra = "\n\nSeus dados foram devolvidos ao lugar.";
                }
                catch { extra = "\n\nSeus dados estão guardados em:\n" + backup; }
            }
            busy = false;
            lblErr.Text = ex.Message + extra;
            ShowPage(pErr);
        }
        finally
        {
            busy = false;
            if (backup == null) { try { Directory.Delete(tmp, true); } catch { } }
        }
    }

    async Task DownloadAsync(string url, string dest)
    {
        using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!r.IsSuccessStatusCode)
            throw new Exception($"Não encontrei esse arquivo na versão escolhida (erro {(int)r.StatusCode}). Tente outra versão.");
        long total = r.Content.Headers.ContentLength ?? -1;
        await using var inS = await r.Content.ReadAsStreamAsync();
        await using var outS = File.Create(dest);
        var buf = new byte[81920];
        long got = 0; int n;
        var sw = Stopwatch.StartNew();
        while ((n = await inS.ReadAsync(buf)) > 0)
        {
            await outS.WriteAsync(buf.AsMemory(0, n));
            got += n;
            double mb = got / 1048576.0, sec = Math.Max(sw.Elapsed.TotalSeconds, 0.1);
            if (total > 0)
            {
                prog = 0.02 + 0.58 * ((double)got / total);
                lblDetail.Text = $"{mb:0.0} de {total / 1048576.0:0.0} MB  ·  {mb / sec:0.0} MB/s";
            }
            else lblDetail.Text = $"{mb:0.0} MB baixados";
        }
    }

    async Task<string> InstallSetupAsync(string setup)
    {
        var psi = new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new Exception("Não consegui iniciar o instalador.");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception($"O instalador terminou com erro (código {p.ExitCode}).");
        if (!File.Exists(SetupExe)) throw new Exception("A instalação terminou, mas não encontrei o app instalado.");
        return SetupExe;
    }

    static async Task KillAppAsync()
    {
        await Task.Run(() =>
        {
            foreach (var n in new[] { "AzulGroove", "AzulGroove-Portable" })
                foreach (var p in Process.GetProcessesByName(n))
                    try { p.Kill(true); p.WaitForExit(4000); } catch { }
        });
        await Task.Delay(700);
    }

    // ---------------- pastas ----------------
    static void MoveDir(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        for (int i = 0; ; i++)
        {
            try { Directory.Move(src, dst); return; }
            catch (Exception) when (i < 4) { Thread.Sleep(500); }
            catch (Exception) { CopyDir(src, dst); DeleteDir(src); return; }
        }
    }
    static void CopyDir(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
        foreach (var x in Directory.GetDirectories(s)) CopyDir(x, Path.Combine(d, Path.GetFileName(x)));
    }
    static void DeleteDir(string p)
    {
        for (int i = 0; ; i++)
        {
            try { if (Directory.Exists(p)) Directory.Delete(p, true); return; }
            catch when (i < 5) { Thread.Sleep(500); }
        }
    }
}

/// <summary>Botão arredondado moderno (gradiente, destaque ao passar o mouse e modo "cartão").</summary>
sealed class RButton : Button
{
    public bool Primary, Selected;
    public string? Sub;
    public int Glyph;   // 0 nenhum, 1 baixar, 2 reparar
    bool hover;

    public RButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }
    static void DrawGlyph(Graphics g, int kind, float x, float cy)
    {
        using var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (kind == 1)       // seta de download
        {
            g.DrawLine(pen, x + 11, cy - 8, x + 11, cy + 3);
            g.DrawLines(pen, new[] { new PointF(x + 5, cy - 2), new PointF(x + 11, cy + 5), new PointF(x + 17, cy - 2) });
            g.DrawLine(pen, x + 4, cy + 9, x + 18, cy + 9);
        }
        else                 // setas circulares (reparar)
        {
            float cx = x + 11, r = 8;
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 40, 280);
            double a = 320 * Math.PI / 180;
            float px = cx + r * (float)Math.Cos(a), py = cy + r * (float)Math.Sin(a);
            float dx = -(float)Math.Sin(a), dy = (float)Math.Cos(a);
            using var br = new SolidBrush(Color.White);
            g.FillPolygon(br, new[] { new PointF(px + dx * 5, py + dy * 5), new PointF(px - dy * 4.5f, py + dx * 4.5f), new PointF(px + dy * 4.5f, py - dx * 4.5f) });
        }
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.Black);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Assistente.Round(r, 14);
        if (Primary)
        {
            using var br = new LinearGradientBrush(r, hover ? Color.FromArgb(112, 124, 255) : Color.FromArgb(88, 101, 242),
                hover ? Color.FromArgb(145, 112, 255) : Color.FromArgb(122, 92, 255), 0f);
            g.FillPath(br, path);
        }
        else
        {
            using var br = new SolidBrush(Selected ? Color.FromArgb(28, 34, 80) : hover ? Color.FromArgb(32, 36, 50) : Color.FromArgb(24, 27, 37));
            g.FillPath(br, path);
            using var pen = new Pen(Selected ? Color.FromArgb(88, 101, 242) : Color.FromArgb(44, 50, 68), Selected ? 2f : 1f);
            g.DrawPath(pen, path);
        }
        using var tb = new SolidBrush(Color.White);
        using var f1 = new Font("Segoe UI Semibold", Sub == null ? 11f : 12f);
        if (Sub == null)
        {
            float tw = g.MeasureString(Text, f1).Width;
            float total = Glyph == 0 ? tw : tw + 32, x0 = (Width - total) / 2f;
            if (Glyph != 0) DrawGlyph(g, Glyph, x0, Height / 2f);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(Text, f1, tb, new RectangleF(x0 + (Glyph == 0 ? 0 : 32), 0, tw + 20, Height), sf);
            return;
        }
        using var f2 = new Font("Segoe UI", 9.5f);
        using var sb = new SolidBrush(Color.FromArgb(154, 163, 178));
        g.DrawString(Text, f1, tb, 20, Height / 2f - 26);
        g.DrawString(Sub, f2, sb, 20, Height / 2f + 3);
        float cx = Width - 30, cy = Height / 2f;
        using var rp = new Pen(Selected ? Color.FromArgb(88, 101, 242) : Color.FromArgb(70, 76, 96), 2f);
        g.DrawEllipse(rp, cx - 9, cy - 9, 18, 18);
        if (Selected) { using var db = new SolidBrush(Color.FromArgb(88, 101, 242)); g.FillEllipse(db, cx - 5, cy - 5, 10, 10); }
    }
}
