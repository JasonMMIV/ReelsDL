// =============================================================================
//  ReelsDL - Shared Fluent (Windows 11 style) UI toolkit
//  Used by: ReelsDL.exe (main app), ReelsDL-Setup.exe, Uninstall.exe
//  Language level: C# 5 (compiled with the in-box .NET Framework csc.exe)
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReelsDL.UI
{
    public static class AppInfo
    {
        public const string Name = "ReelsDL";
        public const string Version = "2.2.0";
        public const string Tagline = "Download videos and photos from Threads, Instagram, X, TikTok and Facebook";
    }

    /// <summary>Small process helpers shared by the app, setup and uninstaller.</summary>
    public static class ProcessUtil
    {
        /// <summary>Quotes one argument using the Windows CommandLineToArgvW rules.</summary>
        public static string QuoteArg(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                }
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static List<Process> FindInstalledProcesses(string installDir)
        {
            List<Process> found = new List<Process>();
            if (string.IsNullOrEmpty(installDir)) return found;
            string root;
            try { root = Path.GetFullPath(installDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
            catch { return found; }

            foreach (Process p in Process.GetProcessesByName("ReelsDL"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (!string.IsNullOrEmpty(path) &&
                        Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        found.Add(p);
                }
                catch { }
            }
            return found;
        }

        /// <summary>True when a ReelsDL instance is running from installDir.</summary>
        public static bool IsInstalledAppRunning(string installDir)
        {
            return FindInstalledProcesses(installDir).Count > 0;
        }

        /// <summary>Terminates only ReelsDL instances installed under installDir.</summary>
        public static int KillInstalledApp(string installDir)
        {
            int killed = 0;
            foreach (Process p in FindInstalledProcesses(installDir))
            {
                try
                {
                    KillTree(p);
                    p.WaitForExit(3000);
                    killed++;
                }
                catch { }
            }
            return killed;
        }

        /// <summary>Terminates a process and all of its children (e.g. Chrome under PowerShell).</summary>
        public static void KillTree(Process process)
        {
            if (process == null) return;
            try
            {
                string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                ProcessStartInfo psi = new ProcessStartInfo(taskkill, "/PID " + process.Id + " /T /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(3000);
                }
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
        }
    }

    /// <summary>Known folders that respect OneDrive / group-policy redirection.</summary>
    public static class KnownFolders
    {
        private static readonly Guid DownloadsFolderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

        public static string DownloadsPath
        {
            get
            {
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    Guid id = DownloadsFolderId;
                    if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer) == 0 && pointer != IntPtr.Zero)
                    {
                        string path = Marshal.PtrToStringUni(pointer);
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
                catch { }
                finally
                {
                    if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Theme: Windows 11 light / dark palettes, follows the system app theme
    // -------------------------------------------------------------------------
    public static class Theme
    {
        public static bool Dark;
        public static float Scale = 1f;

        public static Color WindowBg, CardBg, CardStroke, Divider, FooterBg;
        public static Color TextPrimary, TextSecondary, TextTertiary;
        public static Color Accent, AccentHover, AccentPressed, AccentDisabled, OnAccent, OnAccentDisabled;
        public static Color ControlFill, ControlHover, ControlPressed, ControlStroke, ControlStrokeStrong;
        public static Color InputFill, InputHover, InputFocus, InputBottom;
        public static Color Success, Warning, Danger, ProgressTrack;
        public static Color InfoBg, SuccessBg, WarningBg, ErrorBg;
        public static Color MenuBg;

        public const string TextFont = "Segoe UI";
        public const string SemiboldFont = "Segoe UI Semibold";
        public static string IconFont = "Segoe MDL2 Assets";

        public static event EventHandler Changed;

        private static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();

        static Theme()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { Scale = Math.Max(1f, g.DpiX / 96f); }
            }
            catch { Scale = 1f; }
            try
            {
                using (InstalledFontCollection fc = new InstalledFontCollection())
                {
                    foreach (FontFamily ff in fc.Families)
                    {
                        if (string.Equals(ff.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase)) { IconFont = ff.Name; break; }
                    }
                }
            }
            catch { }
            Apply(DetectDark());
        }

        public static int S(float v) { return (int)Math.Round(v * Scale); }
        public static float SF(float v) { return v * Scale; }

        /// <summary>Shrinks the design scale so the main window fits the primary work area.</summary>
        public static void ClampScale(float designWidth, float designHeight)
        {
            try
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                float max = Math.Min((work.Width - 64f) / designWidth, (work.Height - 64f) / designHeight);
                if (max > 0.5f && max < Scale) Scale = max;
            }
            catch { }
        }

        /// <summary>Walks up the parent chain until an opaque background is found.</summary>
        public static Color SolidBackColor(Control control)
        {
            Control p = control;
            while (p != null && p.BackColor.A < 255) p = p.Parent;
            return p != null ? p.BackColor : WindowBg;
        }

        public static Font Font(float pt) { return GetFont(TextFont, pt); }
        public static Font Semibold(float pt) { return GetFont(SemiboldFont, pt); }
        public static Font Icon(float pt) { return GetFont(IconFont, pt); }

        private static Font GetFont(string family, float pt)
        {
            string key = family + "|" + pt.ToString("0.0");
            Font f;
            if (!fontCache.TryGetValue(key, out f))
            {
                f = new Font(family, pt, FontStyle.Regular, GraphicsUnit.Point);
                fontCache[key] = f;
            }
            return f;
        }

        public static bool DetectDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Re-reads the system theme. Returns true and raises Changed if it switched.</summary>
        public static bool Refresh()
        {
            bool d = DetectDark();
            if (d == Dark) return false;
            Apply(d);
            if (Changed != null) Changed(null, EventArgs.Empty);
            return true;
        }

        private static Color H(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        public static void Apply(bool dark)
        {
            Dark = dark;
            if (!dark)
            {
                WindowBg = H(0xF3F3F3); CardBg = H(0xFCFCFC); CardStroke = H(0xE5E5E5); Divider = H(0xE5E5E5); FooterBg = H(0xF3F3F3);
                TextPrimary = H(0x1A1A1A); TextSecondary = H(0x5D5D5D); TextTertiary = H(0x8B8B8B);
                Accent = H(0x005FB8); AccentHover = H(0x1A6FC2); AccentPressed = H(0x3381CC); AccentDisabled = H(0xC5C5C5);
                OnAccent = Color.White; OnAccentDisabled = Color.White;
                ControlFill = H(0xFEFEFE); ControlHover = H(0xF7F7F7); ControlPressed = H(0xF3F3F3);
                ControlStroke = H(0xE2E2E2); ControlStrokeStrong = H(0x8A8A8A);
                InputFill = H(0xFFFFFF); InputHover = H(0xF9F9F9); InputFocus = H(0xFFFFFF); InputBottom = H(0x868686);
                Success = H(0x0F7B0F); Warning = H(0x9D5D00); Danger = H(0xC42B1C); ProgressTrack = H(0xE1E1E1);
                InfoBg = H(0xF0F4F9); SuccessBg = H(0xDFF6DD); WarningBg = H(0xFFF4CE); ErrorBg = H(0xFDE7E9);
                MenuBg = H(0xF9F9F9);
            }
            else
            {
                WindowBg = H(0x202020); CardBg = H(0x2B2B2B); CardStroke = H(0x363636); Divider = H(0x3A3A3A); FooterBg = H(0x202020);
                TextPrimary = H(0xFFFFFF); TextSecondary = H(0xC8C8C8); TextTertiary = H(0x9A9A9A);
                Accent = H(0x60CDFF); AccentHover = H(0x5BBAE7); AccentPressed = H(0x52A8D1); AccentDisabled = H(0x434343);
                OnAccent = Color.Black; OnAccentDisabled = H(0xA0A0A0);
                ControlFill = H(0x2D2D2D); ControlHover = H(0x323232); ControlPressed = H(0x282828);
                ControlStroke = H(0x3B3B3B); ControlStrokeStrong = H(0x9A9A9A);
                InputFill = H(0x323232); InputHover = H(0x383838); InputFocus = H(0x1F1F1F); InputBottom = H(0x9A9A9A);
                Success = H(0x6CCB5F); Warning = H(0xFCE100); Danger = H(0xFF99A4); ProgressTrack = H(0x454545);
                InfoBg = H(0x2B2B2B); SuccessBg = H(0x393D1B); WarningBg = H(0x433519); ErrorBg = H(0x442726);
                MenuBg = H(0x2C2C2C);
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Drawing helpers
    // -------------------------------------------------------------------------
    public static class Gfx
    {
        public static void HighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            RectangleF ir = new RectangleF(r.X + w / 2f, r.Y + w / 2f, r.Width - w, r.Height - w);
            using (GraphicsPath p = Round(ir, Math.Max(0, rad - w / 2f)))
            using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static Color Blend(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(255,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static Size Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return Size.Empty;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        /// <summary>Extracts a crisp, alpha-correct bitmap of an icon resource at an arbitrary size.</summary>
        public static Bitmap ExtractIcon(string file, int size)
        {
            try
            {
                IntPtr[] h = new IntPtr[1];
                int[] ids = new int[1];
                if (Native.PrivateExtractIcons(file, 0, size, size, h, ids, 1, 0) > 0 && h[0] != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon ic = System.Drawing.Icon.FromHandle(h[0])) return ic.ToBitmap();
                    }
                    finally { Native.DestroyIcon(h[0]); }
                }
            }
            catch { }
            return null;
        }
    }

    // -------------------------------------------------------------------------
    //  Win32 interop (DWM window chrome, scrollbar theming, cue banners)
    // -------------------------------------------------------------------------
    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, int[] ids, int n, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr h, string app, string idList);

        private static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        private static void Set(IntPtr h, int attr, int v)
        {
            try { DwmSetWindowAttribute(h, attr, ref v, 4); } catch { }
        }

        /// <summary>Dark/light title bar, rounded corners and a caption that blends into the window.</summary>
        public static void ApplyWindowChrome(Form f, Color caption)
        {
            if (f == null || !f.IsHandleCreated) return;
            IntPtr h = f.Handle;
            int dark = Theme.Dark ? 1 : 0;
            Set(h, 20, dark);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            Set(h, 19, dark);   // pre-20H1 value
            Set(h, 33, 2);      // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            Set(h, 35, ColorRef(caption));            // DWMWA_CAPTION_COLOR
            Set(h, 36, ColorRef(Theme.TextPrimary));  // DWMWA_TEXT_COLOR
        }

        public static void RoundPopup(IntPtr h) { Set(h, 33, 3); } // ROUNDSMALL

        public static void ThemeScrollbars(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        public static void SetCueBanner(TextBox tb, string text)
        {
            if (tb == null || !tb.IsHandleCreated) return;
            SendMessage(tb.Handle, 0x1501, (IntPtr)1, text ?? "");
        }
    }

    // -------------------------------------------------------------------------
    //  Base class for owner-drawn controls
    // -------------------------------------------------------------------------
    public class FluentControl : Control
    {
        public FluentControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected Color ParentBack
        {
            get { return Theme.SolidBackColor(this); }
        }

        protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    }

    // -------------------------------------------------------------------------
    //  Card: rounded surface that hosts other controls
    // -------------------------------------------------------------------------
    public class Card : Panel
    {
        public float Radius = 8f;
        public bool DrawStroke = true;

        public Card()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.SolidBackColor(this));
            Gfx.HighQuality(g);
            RectangleF r = new RectangleF(0, 0, Width, Height);
            Gfx.Fill(g, r, Theme.SF(Radius), BackColor);
            if (DrawStroke) Gfx.Stroke(g, r, Theme.SF(Radius), Theme.CardStroke, 1f);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    // -------------------------------------------------------------------------
    //  Button (Accent / Standard / Subtle), with optional Fluent icon glyph
    // -------------------------------------------------------------------------
    public enum ButtonKind { Accent, Standard, Subtle }

    public class FluentButton : FluentControl, IButtonControl
    {
        private ButtonKind kind = ButtonKind.Standard;
        private string glyph = "";
        private bool hover, pressed;
        private float glyphSize = 11f;

        public FluentButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Height = Theme.S(32);
            AccessibleRole = AccessibleRole.PushButton;
        }

        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public string Glyph { get { return glyph; } set { glyph = value ?? ""; Invalidate(); } }
        public float GlyphSize { get { return glyphSize; } set { glyphSize = value; Invalidate(); } }
        public Color? ContentColor;
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (string.IsNullOrEmpty(AccessibleName)) AccessibleName = Text;
            Invalidate();
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hover = pressed = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) PerformClick();
        }

        /// <summary>Resizes the button to fit its content.</summary>
        public void FitWidth(int minWidth)
        {
            Width = Math.Max(minWidth, ContentWidth() + Theme.S(string.IsNullOrEmpty(Text) ? 0 : 32));
        }

        private int ContentWidth()
        {
            int w = 0;
            if (glyph.Length > 0) w += Gfx.Measure(glyph, Theme.Icon(glyphSize)).Width;
            if (!string.IsNullOrEmpty(Text))
            {
                if (w > 0) w += Theme.S(8);
                w += Gfx.Measure(Text, Font).Width;
            }
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color pb = ParentBack;
            g.Clear(pb);
            Gfx.HighQuality(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            float rad = Theme.SF(4);
            Color fill = Color.Empty, stroke = Color.Empty, fore;

            switch (kind)
            {
                case ButtonKind.Accent:
                    fill = !Enabled ? Theme.AccentDisabled : pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent;
                    fore = Enabled ? Theme.OnAccent : Theme.OnAccentDisabled;
                    break;
                case ButtonKind.Subtle:
                    if (Enabled && pressed) fill = Gfx.Blend(pb, Theme.TextPrimary, 0.045);
                    else if (Enabled && hover) fill = Gfx.Blend(pb, Theme.TextPrimary, 0.07);
                    fore = Enabled ? (pressed ? Theme.TextSecondary : Theme.TextPrimary) : Theme.TextTertiary;
                    break;
                default:
                    fill = !Enabled ? Theme.ControlFill : pressed ? Theme.ControlPressed : hover ? Theme.ControlHover : Theme.ControlFill;
                    stroke = Theme.ControlStroke;
                    fore = Enabled ? (pressed ? Theme.TextSecondary : Theme.TextPrimary) : Theme.TextTertiary;
                    break;
            }
            if (ContentColor.HasValue && Enabled) fore = ContentColor.Value;

            if (!fill.IsEmpty) Gfx.Fill(g, r, rad, fill);
            if (!stroke.IsEmpty) Gfx.Stroke(g, r, rad, stroke, 1f);
            if (Focused && ShowFocusCues) Gfx.Stroke(g, r, rad, Theme.TextPrimary, Theme.SF(2));

            // Content: [glyph][gap][text], centred
            Font gf = Theme.Icon(glyphSize);
            Size gs = glyph.Length > 0 ? Gfx.Measure(glyph, gf) : Size.Empty;
            Size ts = string.IsNullOrEmpty(Text) ? Size.Empty : Gfx.Measure(Text, Font);
            int gap = (gs.Width > 0 && ts.Width > 0) ? Theme.S(8) : 0;
            int total = gs.Width + gap + ts.Width;
            int x = Math.Max(Theme.S(8), (Width - total) / 2);
            if (gs.Width > 0)
            {
                Gfx.Text(g, glyph, gf, new Rectangle(x, 0, gs.Width + 2, Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                x += gs.Width + gap;
            }
            if (ts.Width > 0)
            {
                Gfx.Text(g, Text, Font, new Rectangle(x, 0, Width - x - Theme.S(6), Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // -------------------------------------------------------------------------
    //  TextBox with rounded frame, accent focus underline, placeholder and
    //  an optional trailing inline button
    // -------------------------------------------------------------------------
    public class FluentTextBox : FluentControl
    {
        public readonly TextBox Inner = new TextBox();
        private FluentButton trailing;
        private string placeholder = "";
        private bool hover;

        public FluentTextBox()
        {
            Inner.BorderStyle = BorderStyle.None;
            Controls.Add(Inner);
            Height = Theme.S(36);
            Cursor = Cursors.IBeam;
            // The wrapper is painted; only the inner TextBox should be a tab stop.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            Inner.GotFocus += delegate { ApplyColors(); Invalidate(); };
            Inner.LostFocus += delegate { ApplyColors(); Invalidate(); };
            Inner.MouseEnter += delegate { hover = true; ApplyColors(); Invalidate(); };
            Inner.MouseLeave += delegate { hover = ClientRectangle.Contains(PointToClient(MousePosition)); ApplyColors(); Invalidate(); };
            Inner.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            Inner.HandleCreated += delegate { Native.SetCueBanner(Inner, placeholder); };
            ApplyColors();
        }

        public override string Text
        {
            get { return Inner != null ? Inner.Text : string.Empty; }
            set { if (Inner != null) Inner.Text = value; }
        }

        public string Placeholder
        {
            get { return placeholder; }
            set
            {
                placeholder = value ?? "";
                if (Inner != null) Native.SetCueBanner(Inner, placeholder);
            }
        }

        public bool ReadOnly
        {
            get { return Inner != null ? Inner.ReadOnly : false; }
            set { if (Inner != null) Inner.ReadOnly = value; }
        }

        public FluentButton AddTrailingButton(string glyph)
        {
            trailing = new FluentButton();
            trailing.Kind = ButtonKind.Subtle;
            trailing.Glyph = glyph;
            trailing.Cursor = Cursors.Default;
            trailing.TabStop = false;
            Controls.Add(trailing);
            PerformLayout();
            return trailing;
        }

        public void ApplyColors()
        {
            if (Inner == null) return;
            Color bg = Inner.Focused ? Theme.InputFocus : hover ? Theme.InputHover : Theme.InputFill;
            BackColor = bg;
            Inner.BackColor = bg;
            Inner.ForeColor = Theme.TextPrimary;
            if (trailing != null) trailing.Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Inner != null)
            {
                Inner.Font = Font;
                PerformLayout();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; ApplyColors(); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; ApplyColors(); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Inner != null) Inner.Focus(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (Inner != null) Inner.Focus(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Inner == null) return;
            int pad = Theme.S(12);
            int right = pad;
            if (trailing != null)
            {
                int bs = Height - Theme.S(8);
                trailing.SetBounds(Width - bs - Theme.S(4), Theme.S(4), bs, bs);
                right = bs + Theme.S(10);
            }
            Inner.SetBounds(pad, Math.Max(0, (Height - Inner.PreferredHeight) / 2) + 1, Math.Max(10, Width - pad - right), Inner.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            Gfx.HighQuality(g);
            RectangleF r = new RectangleF(0, 0, Width, Height);
            float rad = Theme.SF(4);
            Gfx.Fill(g, r, rad, BackColor);
            Gfx.Stroke(g, r, rad, Theme.ControlStroke, 1f);

            // Bottom edge: thin neutral line at rest, 2px accent line when focused (Windows 11 style)
            bool focused = Inner.Focused;
            float lh = focused ? Theme.SF(2) : 1f;
            using (GraphicsPath clip = Gfx.Round(r, rad))
            {
                g.SetClip(clip);
                using (SolidBrush b = new SolidBrush(focused ? Theme.Accent : Theme.InputBottom))
                    g.FillRectangle(b, 0, Height - lh, Width, lh);
                g.ResetClip();
            }
        }
    }

    // -------------------------------------------------------------------------
    //  CheckBox (Windows 11 style)
    // -------------------------------------------------------------------------
    public class FluentCheckBox : FluentControl
    {
        private bool isChecked;
        private bool hover, pressed;
        public event EventHandler CheckedChanged;

        public FluentCheckBox()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Height = Theme.S(28);
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked != value) { isChecked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }

        public void FitWidth() { Width = Theme.S(30) + Gfx.Measure(Text, Font).Width + Theme.S(4); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color pb = ParentBack;
            g.Clear(pb);
            Gfx.HighQuality(g);
            int bs = Theme.S(20);
            RectangleF box = new RectangleF(0, (Height - bs) / 2f, bs, bs);
            float rad = Theme.SF(4);
            if (isChecked)
            {
                Color f = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent;
                Gfx.Fill(g, box, rad, f);
                Gfx.Text(g, "\uE73E", Theme.Icon(9.5f), Rectangle.Round(box), Theme.OnAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                Color f = pressed ? Gfx.Blend(pb, Theme.TextPrimary, 0.09) : hover ? Gfx.Blend(pb, Theme.TextPrimary, 0.06) : Gfx.Blend(pb, Theme.TextPrimary, 0.025);
                Gfx.Fill(g, box, rad, f);
                Gfx.Stroke(g, box, rad, Theme.ControlStrokeStrong, 1f);
            }
            if (Focused && ShowFocusCues)
            {
                RectangleF fr = RectangleF.Inflate(box, Theme.SF(2), Theme.SF(2));
                Gfx.Stroke(g, fr, rad + Theme.SF(2), Theme.TextPrimary, Theme.SF(1.5f));
            }
            Gfx.Text(g, Text, Font, new Rectangle(bs + Theme.S(10), 0, Width - bs - Theme.S(10), Height),
                Enabled ? Theme.TextPrimary : Theme.TextTertiary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // -------------------------------------------------------------------------
    //  Thin rounded progress bar (determinate / indeterminate)
    // -------------------------------------------------------------------------
    public class ProgressLine : FluentControl
    {
        private float value;
        private bool indeterminate;
        private readonly Timer anim;
        private float phase;
        public Color? BarColor;

        public ProgressLine()
        {
            Height = Theme.S(4);
            anim = new Timer();
            anim.Interval = 30;
            anim.Tick += delegate { phase += 0.018f; if (phase > 1.4f) phase = -0.4f; Invalidate(); };
        }

        public float Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        public bool Indeterminate
        {
            get { return indeterminate; }
            set { indeterminate = value; UpdateTimer(); Invalidate(); }
        }

        private void UpdateTimer()
        {
            bool run = indeterminate && Visible && !IsDisposed;
            if (run && !anim.Enabled) { phase = -0.4f; anim.Start(); }
            else if (!run && anim.Enabled) anim.Stop();
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
        protected override void Dispose(bool disposing) { if (disposing) { anim.Stop(); anim.Dispose(); } base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            Gfx.HighQuality(g);
            float h = Height, rad = h / 2f;
            Gfx.Fill(g, new RectangleF(0, 0, Width, h), rad, Theme.ProgressTrack);
            Color bar = BarColor.HasValue ? BarColor.Value : Theme.Accent;
            if (indeterminate)
            {
                float segW = Width * 0.35f;
                float x = phase * Width;
                RectangleF seg = RectangleF.Intersect(new RectangleF(x, 0, segW, h), new RectangleF(0, 0, Width, h));
                if (seg.Width > 0) Gfx.Fill(g, seg, rad, bar);
            }
            else if (value > 0)
            {
                Gfx.Fill(g, new RectangleF(0, 0, Math.Max(h, Width * value), h), rad, bar);
            }
        }
    }

    // -------------------------------------------------------------------------
    //  InfoBar (Windows 11 style inline notification)
    // -------------------------------------------------------------------------
    public enum Severity { Info, Success, Warning, Error }

    public class InfoBar : FluentControl
    {
        private Severity severity = Severity.Info;
        private string title = "", message = "";
        public readonly FluentButton CloseButton = new FluentButton();
        public readonly FluentButton ActionButton = new FluentButton();

        public InfoBar()
        {
            Height = Theme.S(48);
            CloseButton.Kind = ButtonKind.Subtle;
            CloseButton.Glyph = "\uE711";
            CloseButton.GlyphSize = 9f;
            CloseButton.AccessibleName = "Dismiss notification";
            CloseButton.Click += delegate { Visible = false; };
            Controls.Add(CloseButton);

            ActionButton.Kind = ButtonKind.Standard;
            ActionButton.Visible = false;
            Controls.Add(ActionButton);
        }

        public void Show(Severity sev, string t, string msg)
        {
            severity = sev; title = t ?? ""; message = msg ?? "";
            if (ActionButton != null) ActionButton.Visible = false;
            Visible = true;
            BackColor = Bg();
            PerformLayout();
            Invalidate();
        }

        public void SetAction(string text, EventHandler handler)
        {
            if (ActionButton == null) return;
            ActionButton.Text = text;
            ActionButton.AccessibleName = text;
            ActionButton.FitWidth(Theme.S(80));
            if (lastHandler != null) ActionButton.Click -= lastHandler;
            lastHandler = handler;
            ActionButton.Click += handler;
            ActionButton.Visible = true;
            PerformLayout();
        }
        private EventHandler lastHandler;

        public Severity Severity { get { return severity; } }

        private Color Bg()
        {
            switch (severity)
            {
                case Severity.Success: return Theme.SuccessBg;
                case Severity.Warning: return Theme.WarningBg;
                case Severity.Error: return Theme.ErrorBg;
                default: return Theme.InfoBg;
            }
        }

        public void RefreshTheme() { BackColor = Bg(); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (CloseButton == null || ActionButton == null) return;
            int bs = Theme.S(32);
            CloseButton.SetBounds(Width - bs - Theme.S(8), (Height - bs) / 2, bs, bs);
            if (ActionButton.Visible)
                ActionButton.SetBounds(CloseButton.Left - ActionButton.Width - Theme.S(8), (Height - bs) / 2, ActionButton.Width, bs);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(ParentBack);
            Gfx.HighQuality(g);
            RectangleF r = new RectangleF(0, 0, Width, Height);
            Gfx.Fill(g, r, Theme.SF(4), BackColor);
            Gfx.Stroke(g, r, Theme.SF(4), Theme.CardStroke, 1f);

            string glyph; Color gc;
            switch (severity)
            {
                case Severity.Success: glyph = "\uE930"; gc = Theme.Success; break;
                case Severity.Warning: glyph = "\uE7BA"; gc = Theme.Warning; break;
                case Severity.Error: glyph = "\uEA39"; gc = Theme.Danger; break;
                default: glyph = "\uE946"; gc = Theme.Accent; break;
            }
            int x = Theme.S(16);
            Gfx.Text(g, glyph, Theme.Icon(12f), new Rectangle(x, 0, Theme.S(20), Height), gc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            x += Theme.S(32);

            int right = Width - Theme.S(8);
            if (ActionButton != null && ActionButton.Visible) right = ActionButton.Left - Theme.S(8);
            else if (CloseButton != null) right = CloseButton.Left - Theme.S(8);

            Font tf = Theme.Semibold(10f);
            int tw = Math.Min(Gfx.Measure(title, tf).Width, Math.Max(0, right - x));
            if (tw > 0)
            {
                Gfx.Text(g, title, tf, new Rectangle(x, 0, tw, Height), Theme.TextPrimary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                x += tw + Theme.S(10);
            }
            if (right - x > 10)
                Gfx.Text(g, message, Theme.Font(10f), new Rectangle(x, 0, right - x, Height), Theme.TextPrimary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // -------------------------------------------------------------------------
    //  Context menu renderer (Windows 11 flyout look)
    // -------------------------------------------------------------------------
    public class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        public FluentMenuRenderer() { RoundedEdges = false; }

        public static void Style(ContextMenuStrip m)
        {
            m.Renderer = new FluentMenuRenderer();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = false;
            m.BackColor = Theme.MenuBg;
            m.Font = Theme.Font(10f);
            m.Padding = new Padding(Theme.S(4), Theme.S(4), Theme.S(4), Theme.S(4));
            m.DropShadowEnabled = true;
            m.HandleCreated += delegate { Native.RoundPopup(m.Handle); };
        }

        public static ToolStripMenuItem Item(string text, string rightText, EventHandler click)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.AutoSize = false;
            it.Height = Theme.S(36);
            it.Width = Theme.S(300);
            it.Padding = new Padding(Theme.S(8), 0, Theme.S(8), 0);
            if (!string.IsNullOrEmpty(rightText)) it.ShortcutKeyDisplayString = rightText;
            if (click != null) it.Click += click;
            return it;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.MenuBg);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.CardStroke))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Graphics g = e.Graphics;
            Gfx.HighQuality(g);
            RectangleF r = new RectangleF(Theme.S(2), Theme.S(2), e.Item.Width - Theme.S(4), e.Item.Height - Theme.S(4));
            Gfx.Fill(g, r, Theme.SF(4), Gfx.Blend(Theme.MenuBg, Theme.TextPrimary, 0.07));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
            bool isShortcut = mi != null && e.Text == mi.ShortcutKeyDisplayString && e.Text != mi.Text;
            if (!e.Item.Enabled) e.TextColor = Theme.TextTertiary;
            else if (isShortcut) e.TextColor = mi.Tag is Color ? (Color)mi.Tag : Theme.TextSecondary;
            else e.TextColor = Theme.TextPrimary;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Divider))
                e.Graphics.DrawLine(p, Theme.S(4), y, e.Item.Width - Theme.S(4), y);
        }
    }

    // -------------------------------------------------------------------------
    //  Plain label that keeps theme colours
    // -------------------------------------------------------------------------
    public class FluentLabel : Label
    {
        public bool Secondary;
        public Color? Override;

        public FluentLabel()
        {
            AutoSize = true;
            UseMnemonic = false;
            BackColor = Color.Transparent;
        }

        public void RefreshTheme()
        {
            ForeColor = Override.HasValue ? Override.Value : (Secondary ? Theme.TextSecondary : Theme.TextPrimary);
            if (Parent != null) BackColor = Parent.BackColor;
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); RefreshTheme(); }
    }

    /// <summary>Utility that walks a control tree and re-applies theme colours.</summary>
    public static class ThemeWalker
    {
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                FluentLabel fl = c as FluentLabel;
                if (fl != null) fl.RefreshTheme();
                FluentTextBox tb = c as FluentTextBox;
                if (tb != null) tb.ApplyColors();
                InfoBar ib = c as InfoBar;
                if (ib != null) ib.RefreshTheme();
                Apply(c);
                c.Invalidate();
            }
        }
    }
}
