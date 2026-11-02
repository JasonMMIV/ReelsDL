// =============================================================================
//  ReelsDL - Windows 11 Fluent Desktop Application
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ReelsDL.UI;

namespace ReelsDL
{
    public class MainForm : Form
    {
        private string scriptPath;
        private string defaultOut;
        private Process currentProc;
        private string currentLogPath;
        private System.Windows.Forms.Timer pollTimer;
        private int lastReadChars;
        private string currentJobKind = "download";
        private bool autoDownloadAfterLogin;
        private string pendingUrl;
        private string logRemainder = "";
        private bool cancelRequested;
        private Dictionary<string, bool> loginStates;
        private DateTime loginStatesAt;
        private bool loginProbeRunning;
        private readonly object loginLock = new object();

        // UI Controls
        private Card cardInput;
        private FluentTextBox txtUrl;
        private FluentTextBox txtOut;
        private FluentButton btnPaste;
        private FluentButton btnBrowse;
        private FluentButton btnOpenFolder;
        private FluentButton btnDownload;
        private FluentButton btnAccounts;
        private FluentButton btnCancelJob;
        private InfoBar infoBar;

        // Activity Card
        private Card cardActivity;
        private FluentLabel lblActTitle;
        private FluentLabel lblActSub;
        private FluentLabel lblMeta;
        private ProgressLine actProgress;
        private Panel actButtons;
        private FluentButton btnPlayFile;
        private FluentButton btnShowInFolder;
        private string lastDownloadedFilePath;
        private bool lastDownloadedIsImage;

        // Details / Log Expander
        private FluentButton btnToggleLog;
        private TextBox txtLog;
        private bool logExpanded;

        public MainForm()
        {
            scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReelsDL.ps1");
            if (!File.Exists(scriptPath))
            {
                // Fallback for development run inside build/
                string devPs1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "ReelsDL.ps1");
                if (File.Exists(devPs1)) scriptPath = Path.GetFullPath(devPs1);
            }

            defaultOut = Path.Combine(KnownFolders.DownloadsPath, "ReelsDL");

            InitializeWindow();
            BuildUi();

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 250;
            pollTimer.Tick += PollTimer_Tick;

            Theme.Changed += delegate
            {
                ApplyTheme();
            };
        }

        private void InitializeWindow()
        {
            // Keep the window inside the work area on small / high-DPI screens.
            Theme.ClampScale(860f, 640f);

            Text = AppInfo.Name + " - " + AppInfo.Tagline;
            Size = new Size(Theme.S(860), Theme.S(640));
            MinimumSize = new Size(Theme.S(740), Theme.S(520));
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.Font(10f);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            HandleCreated += delegate
            {
                Native.ApplyWindowChrome(this, Theme.WindowBg);
            };
        }

        private void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            Native.ApplyWindowChrome(this, Theme.WindowBg);
            if (cardInput != null) cardInput.BackColor = Theme.CardBg;
            if (cardActivity != null) cardActivity.BackColor = Theme.CardBg;
            if (txtLog != null)
            {
                txtLog.BackColor = Theme.Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(248, 248, 248);
                txtLog.ForeColor = Theme.TextPrimary;
                Native.ThemeScrollbars(txtLog);
            }
            ThemeWalker.Apply(this);
            Invalidate(true);
        }

        private void BuildUi()
        {
            BackColor = Theme.WindowBg;

            // Header bar
            Panel pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = Theme.S(72),
                Padding = new Padding(Theme.S(24), Theme.S(16), Theme.S(24), 0),
                BackColor = Color.Transparent
            };
            Controls.Add(pnlHeader);

            PictureBox picLogo = new PictureBox
            {
                Size = new Size(Theme.S(38), Theme.S(38)),
                Location = new Point(Theme.S(24), Theme.S(16)),
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                picLogo.Image = Gfx.ExtractIcon(icoPath, Theme.S(48));
            }
            else if (this.Icon != null)
            {
                try { picLogo.Image = Bitmap.FromHicon(this.Icon.Handle); } catch { }
            }
            pnlHeader.Controls.Add(picLogo);

            FluentLabel lblTitle = new FluentLabel
            {
                Text = AppInfo.Name,
                Font = Theme.Semibold(15f),
                Location = new Point(Theme.S(70), Theme.S(14))
            };
            pnlHeader.Controls.Add(lblTitle);

            FluentLabel lblSub = new FluentLabel
            {
                Text = "Threads  •  Instagram  •  X (Twitter)  •  TikTok  •  Facebook",
                Font = Theme.Font(9.5f),
                Secondary = true,
                Location = new Point(Theme.S(72), Theme.S(40))
            };
            pnlHeader.Controls.Add(lblSub);

            // Accounts / Login Pill Button
            btnAccounts = new FluentButton
            {
                Text = "Accounts",
                Glyph = "\uE77B", // Contact icon
                Kind = ButtonKind.Standard,
                Height = Theme.S(34),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnAccounts.FitWidth(Theme.S(120));
            btnAccounts.Location = new Point(pnlHeader.Width - btnAccounts.Width - Theme.S(24), Theme.S(18));
            btnAccounts.Click += BtnAccounts_Click;
            pnlHeader.Controls.Add(btnAccounts);

            pnlHeader.Resize += delegate
            {
                btnAccounts.Location = new Point(pnlHeader.Width - btnAccounts.Width - Theme.S(24), Theme.S(18));
            };

            // Main scrollable / layout area
            Panel pnlMain = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(Theme.S(24), Theme.S(8), Theme.S(24), Theme.S(16)),
                AutoScroll = true
            };
            Controls.Add(pnlMain);
            pnlMain.BringToFront();

            // InfoBar for alerts & notifications
            infoBar = new InfoBar
            {
                Dock = DockStyle.Top,
                Visible = false
            };

            // Input Card
            cardInput = new Card
            {
                Dock = DockStyle.Top,
                Padding = new Padding(Theme.S(20)),
                Height = Theme.S(210)
            };

            // Spacer
            Panel sp = new Panel { Dock = DockStyle.Top, Height = Theme.S(12), BackColor = Color.Transparent };

            // Activity / Result Card
            cardActivity = new Card
            {
                Dock = DockStyle.Top,
                Padding = new Padding(Theme.S(20)),
                Height = Theme.S(140)
            };

            // WinForms docks the last-added Top control first. Add from the
            // bottom of the desired visual order so InfoBar ends up on top.
            Panel pnlLog = BuildLogPanel();
            pnlMain.Controls.Add(pnlLog);
            pnlMain.Controls.Add(cardActivity);
            pnlMain.Controls.Add(sp);
            pnlMain.Controls.Add(cardInput);
            pnlMain.Controls.Add(infoBar);

            BuildInputCard();
            BuildActivityCard();

            AcceptButton = btnDownload;
            UpdateAccountButtonLabel();
        }

        private void BuildInputCard()
        {
            FluentLabel lblUrl = new FluentLabel
            {
                Text = "Video or Post URL",
                Font = Theme.Semibold(10.5f),
                Location = new Point(Theme.S(20), Theme.S(16))
            };
            cardInput.Controls.Add(lblUrl);

            txtUrl = new FluentTextBox
            {
                Placeholder = "Paste video link from Threads, Instagram, X, TikTok, Facebook...",
                Font = Theme.Font(10.5f),
                Height = Theme.S(40),
                Location = new Point(Theme.S(20), Theme.S(42)),
                Width = cardInput.Width - Theme.S(40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            btnPaste = txtUrl.AddTrailingButton("\uE77F"); // Paste icon
            btnPaste.AccessibleName = "Paste from clipboard";
            btnPaste.Click += delegate
            {
                try
                {
                    string clip = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clip) && Regex.IsMatch(clip, @"^https?://", RegexOptions.IgnoreCase))
                    {
                        txtUrl.Text = clip.Trim();
                    }
                }
                catch { }
            };
            txtUrl.Inner.TextChanged += delegate
            {
                UpdateAccountButtonLabel();
            };
            cardInput.Controls.Add(txtUrl);

            // Destination Row
            FluentLabel lblDest = new FluentLabel
            {
                Text = "Save Destination",
                Font = Theme.Semibold(9.5f),
                Secondary = true,
                Location = new Point(Theme.S(20), Theme.S(94))
            };
            cardInput.Controls.Add(lblDest);

            txtOut = new FluentTextBox
            {
                Text = defaultOut,
                Font = Theme.Font(9.5f),
                Height = Theme.S(34),
                Location = new Point(Theme.S(20), Theme.S(116)),
                Width = cardInput.Width - Theme.S(180),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            cardInput.Controls.Add(txtOut);

            btnBrowse = new FluentButton
            {
                Text = "Browse...",
                Kind = ButtonKind.Standard,
                Height = Theme.S(34),
                Location = new Point(cardInput.Width - Theme.S(150), Theme.S(116)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnBrowse.FitWidth(Theme.S(80));
            btnBrowse.Click += BtnBrowse_Click;
            cardInput.Controls.Add(btnBrowse);

            btnOpenFolder = new FluentButton
            {
                Glyph = "\uED25", // Open Folder icon
                Kind = ButtonKind.Subtle,
                Height = Theme.S(34),
                Width = Theme.S(40),
                AccessibleName = "Open destination folder",
                Location = new Point(cardInput.Width - Theme.S(60), Theme.S(116)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOpenFolder.Click += delegate
            {
                try
                {
                    string p = txtOut.Text.Trim();
                    if (string.IsNullOrEmpty(p)) p = defaultOut;
                    if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                    string quoted = p.Length > 3 ? p.TrimEnd('\\', '/') : p;
                    Process.Start("explorer.exe", "\"" + quoted + "\"");
                }
                catch (Exception ex)
                {
                    infoBar.Show(Severity.Error, "Error Opening Folder", ex.Message);
                }
            };
            cardInput.Controls.Add(btnOpenFolder);

            // Primary Download Button
            btnDownload = new FluentButton
            {
                Text = "Download Video",
                Glyph = "\uE896", // Download icon
                GlyphSize = 12f,
                Kind = ButtonKind.Accent,
                Height = Theme.S(38),
                Location = new Point(Theme.S(20), Theme.S(160)),
                Font = Theme.Semibold(10.5f)
            };
            btnDownload.FitWidth(Theme.S(160));
            btnDownload.Click += BtnDownload_Click;
            cardInput.Controls.Add(btnDownload);
        }

        private void BuildActivityCard()
        {
            lblActTitle = new FluentLabel
            {
                Text = "Ready to download",
                Font = Theme.Semibold(11f),
                Location = new Point(Theme.S(20), Theme.S(16))
            };
            cardActivity.Controls.Add(lblActTitle);

            lblActSub = new FluentLabel
            {
                Text = "Paste a link above and click Download Video to begin.",
                Font = Theme.Font(9.5f),
                Secondary = true,
                Location = new Point(Theme.S(20), Theme.S(40))
            };
            cardActivity.Controls.Add(lblActSub);

            lblMeta = new FluentLabel
            {
                Text = "",
                Font = Theme.Font(9.5f),
                Secondary = true,
                Location = new Point(Theme.S(20), Theme.S(64)),
                Visible = false
            };
            cardActivity.Controls.Add(lblMeta);

            actProgress = new ProgressLine
            {
                Location = new Point(Theme.S(20), Theme.S(90)),
                Width = cardActivity.Width - Theme.S(40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };
            cardActivity.Controls.Add(actProgress);

            actButtons = new Panel
            {
                Location = new Point(Theme.S(20), Theme.S(94)),
                Size = new Size(cardActivity.Width - Theme.S(40), Theme.S(36)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent,
                Visible = false
            };
            cardActivity.Controls.Add(actButtons);

            btnPlayFile = new FluentButton
            {
                Text = "Play Video",
                Glyph = "\uE768", // Play icon
                Kind = ButtonKind.Accent,
                Height = Theme.S(32),
                Location = new Point(0, 0)
            };
            btnPlayFile.FitWidth(Theme.S(110));
            btnPlayFile.Click += delegate
            {
                if (!string.IsNullOrEmpty(lastDownloadedFilePath) && File.Exists(lastDownloadedFilePath))
                {
                    try { Process.Start(lastDownloadedFilePath); } catch { }
                }
            };
            actButtons.Controls.Add(btnPlayFile);

            btnShowInFolder = new FluentButton
            {
                Text = "Show in Folder",
                Glyph = "\uE838", // Folder view
                Kind = ButtonKind.Standard,
                Height = Theme.S(32),
                Location = new Point(btnPlayFile.Right + Theme.S(10), 0)
            };
            btnShowInFolder.FitWidth(Theme.S(130));
            btnShowInFolder.Click += delegate
            {
                if (!string.IsNullOrEmpty(lastDownloadedFilePath) && File.Exists(lastDownloadedFilePath))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + lastDownloadedFilePath + "\""); } catch { }
                }
                else
                {
                    btnOpenFolder.PerformClick();
                }
            };
            actButtons.Controls.Add(btnShowInFolder);

            btnCancelJob = new FluentButton
            {
                Text = "Cancel",
                Kind = ButtonKind.Subtle,
                Height = Theme.S(28),
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AccessibleName = "Cancel current task"
            };
            btnCancelJob.FitWidth(Theme.S(70));
            btnCancelJob.Location = new Point(cardActivity.Width - btnCancelJob.Width - Theme.S(20), Theme.S(12));
            btnCancelJob.Click += delegate
            {
                cancelRequested = true;
                btnCancelJob.Enabled = false;
                btnCancelJob.Visible = false;
                lblActSub.Text = "Cancelling...";
                if (currentProc != null && !currentProc.HasExited) ProcessUtil.KillTree(currentProc);
            };
            cardActivity.Controls.Add(btnCancelJob);

            UpdatePlayButton();
        }

        private void UpdatePlayButton()
        {
            if (btnPlayFile == null) return;
            btnPlayFile.Text = lastDownloadedIsImage ? "Open Image" : "Play Video";
            btnPlayFile.Glyph = lastDownloadedIsImage ? "\uE8B9" : "\uE768";
            btnPlayFile.AccessibleName = btnPlayFile.Text;
            btnPlayFile.FitWidth(Theme.S(110));
        }

        private Panel BuildLogPanel()
        {
            Panel pnlLog = new Panel
            {
                Dock = DockStyle.Top,
                Padding = new Padding(0, Theme.S(10), 0, Theme.S(10)),
                Height = Theme.S(40)
            };

            btnToggleLog = new FluentButton
            {
                Text = "View Detailed Logs",
                Glyph = "\uE70D", // Chevron down
                GlyphSize = 9f,
                Kind = ButtonKind.Subtle,
                Height = Theme.S(28),
                Location = new Point(0, 0)
            };
            btnToggleLog.FitWidth(Theme.S(140));
            btnToggleLog.Click += delegate
            {
                logExpanded = !logExpanded;
                btnToggleLog.Text = logExpanded ? "Hide Detailed Logs" : "View Detailed Logs";
                btnToggleLog.Glyph = logExpanded ? "\uE70E" : "\uE70D";
                txtLog.Visible = logExpanded;
                pnlLog.Height = logExpanded ? Theme.S(220) : Theme.S(40);
            };
            pnlLog.Controls.Add(btnToggleLog);

            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
                Location = new Point(0, Theme.S(34)),
                Size = new Size(pnlLog.Width, Theme.S(176)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            txtLog.BackColor = Theme.Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(248, 248, 248);
            txtLog.ForeColor = Theme.TextPrimary;
            Native.ThemeScrollbars(txtLog);
            pnlLog.Controls.Add(txtLog);
            return pnlLog;
        }

        private void UpdateAccountButtonLabel()
        {
            string u = txtUrl.Text.Trim();
            string p = DetectPlatform(u);
            if (p != "General" && p != "Unknown")
            {
                btnAccounts.Text = "Login " + p;
                btnAccounts.FitWidth(Theme.S(130));
            }
            else
            {
                btnAccounts.Text = "Accounts";
                btnAccounts.FitWidth(Theme.S(110));
            }
            btnAccounts.AccessibleName = btnAccounts.Text;
        }

        private static bool HostIs(string url, string domain)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }

        private string DetectPlatform(string url)
        {
            if (string.IsNullOrEmpty(url)) return "Unknown";
            if (HostIs(url, "instagram.com")) return "Instagram";
            if (HostIs(url, "threads.com") || HostIs(url, "threads.net")) return "Threads";
            if (HostIs(url, "x.com") || HostIs(url, "twitter.com")) return "X";
            if (HostIs(url, "tiktok.com")) return "TikTok";
            if (HostIs(url, "facebook.com") || HostIs(url, "fb.watch")) return "Facebook";
            return "General";
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select Destination Folder";
                string cur = txtOut.Text.Trim();
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtOut.Text = dlg.SelectedPath;
                }
            }
        }

        private void BtnAccounts_Click(object sender, EventArgs e)
        {
            string u = txtUrl.Text.Trim();
            string p = DetectPlatform(u);
            if (p != "General" && p != "Unknown")
            {
                // Open the post itself so login can return to the requested page.
                StartJob("login", IsHttpUrl(u) ? u : null, p);
                return;
            }

            // Show flyout menu with platform choices and current session status
            ContextMenuStrip menu = new ContextMenuStrip();
            FluentMenuRenderer.Style(menu);

            Dictionary<string, bool> states = GetLoginStates();
            string[] list = new string[] { "Instagram", "Threads", "Facebook", "X (Twitter)", "TikTok" };
            foreach (string platform in list)
            {
                string platName = platform.Split(' ')[0];
                bool logged = states.ContainsKey(platName) && states[platName];
                string tag = logged ? "Logged in ✓" : "Guest mode";
                Color tagColor = logged ? Theme.Success : Theme.TextTertiary;

                ToolStripMenuItem item = FluentMenuRenderer.Item(
                    "Log into " + platform,
                    tag,
                    delegate { StartJob("login", null, platName); });
                item.Tag = tagColor;
                menu.Items.Add(item);
            }

            menu.Show(btnAccounts, new Point(btnAccounts.Width - menu.Width, btnAccounts.Height + Theme.S(4)));
        }

        /// <summary>
        /// Asks the engine for authoritative per-platform session state. The
        /// engine reads the Chrome cookie database with winsqlite3 and pairs
        /// host_key with name, so this is the single source of truth.
        /// </summary>
        private static Dictionary<string, bool> CreateLoginStates()
        {
            Dictionary<string, bool> states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new string[] { "Instagram", "Threads", "Facebook", "X", "TikTok" })
                states[name] = false;
            return states;
        }

        private Dictionary<string, bool> GetLoginStates()
        {
            lock (loginLock)
            {
                if (loginStates != null && (DateTime.UtcNow - loginStatesAt).TotalSeconds < 30)
                    return loginStates;
                if (loginProbeRunning)
                    return loginStates ?? CreateLoginStates();
                loginProbeRunning = true;
            }

            Dictionary<string, bool> states = CreateLoginStates();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = PowerShellPath,
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " +
                                ProcessUtil.QuoteArg(scriptPath) + " -CookieState",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    string output = "";
                    Thread reader = new Thread(delegate()
                    {
                        try { output = p.StandardOutput.ReadToEnd(); }
                        catch { }
                    });
                    // Drain stderr so a chatty failure cannot fill the pipe
                    // and stall the child until the timeout.
                    Thread errorReader = new Thread(delegate()
                    {
                        try { p.StandardError.ReadToEnd(); }
                        catch { }
                    });
                    reader.IsBackground = true;
                    errorReader.IsBackground = true;
                    reader.Start();
                    errorReader.Start();

                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                    }
                    reader.Join(2000);
                    errorReader.Join(1000);

                    foreach (string line in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        if (states.ContainsKey(key)) states[key] = value == "1";
                    }
                }
            }
            catch { }

            lock (loginLock)
            {
                loginStates = states;
                loginStatesAt = DateTime.UtcNow;
                loginProbeRunning = false;
            }
            return states;
        }

        private void InvalidateLoginStates()
        {
            lock (loginLock) { loginStates = null; }
        }

        private static string PowerShellPath
        {
            get
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                return File.Exists(path) ? path : "powershell.exe";
            }
        }

        private static bool IsHttpUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private void BtnDownload_Click(object sender, EventArgs e)
        {
            string url = txtUrl.Text.Trim();
            if (!IsHttpUrl(url))
            {
                infoBar.Show(Severity.Warning, "Invalid URL", "Please enter a valid link starting with http:// or https://");
                txtUrl.Inner.Focus();
                return;
            }

            StartDownload(url);
        }

        private void StartDownload(string url)
        {
            string outDir = txtOut.Text.Trim();
            if (string.IsNullOrEmpty(outDir)) outDir = defaultOut;

            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                infoBar.Show(Severity.Error, "Invalid Destination", ex.Message);
                return;
            }

            infoBar.Visible = false;
            StartJob("download", url, null);
        }

        private void StartJob(string kind, string url, string platform)
        {
            if (currentProc != null && !currentProc.HasExited)
            {
                infoBar.Show(Severity.Warning, "Busy", "A task is already running. Please wait for it to complete.");
                return;
            }

            if (currentProc != null)
            {
                try { currentProc.Dispose(); } catch { }
                currentProc = null;
            }
            if (!string.IsNullOrEmpty(currentLogPath) && File.Exists(currentLogPath))
            {
                try { File.Delete(currentLogPath); } catch { }
            }

            currentJobKind = kind;
            lastDownloadedFilePath = null;
            lastDownloadedIsImage = false;
            logRemainder = "";
            cancelRequested = false;
            if (kind == "download")
            {
                autoDownloadAfterLogin = false;
                pendingUrl = null;
            }

            currentLogPath = Path.Combine(Path.GetTempPath(), "reelsdl_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            try { File.WriteAllText(currentLogPath, ""); }
            catch (Exception ex)
            {
                infoBar.Show(Severity.Error, "Unable to Create Log", ex.Message);
                return;
            }
            lastReadChars = 0;
            txtLog.Text = "";

            btnDownload.Enabled = false;
            btnAccounts.Enabled = false;
            btnCancelJob.Visible = true;
            btnCancelJob.Enabled = true;
            actButtons.Visible = false;
            actProgress.Visible = true;
            actProgress.Indeterminate = true;

            string args = "-NoProfile -ExecutionPolicy Bypass -File " + ProcessUtil.QuoteArg(scriptPath);
            if (kind == "download")
            {
                lblActTitle.Text = "Downloading...";
                lblActSub.Text = "Connecting to page in headless browser...";
                lblMeta.Visible = false;
                string outDir = txtOut.Text.Trim();
                if (string.IsNullOrEmpty(outDir)) outDir = defaultOut;
                args += " -Cli -Url " + ProcessUtil.QuoteArg(url) +
                        " -Out " + ProcessUtil.QuoteArg(outDir) +
                        " -Log " + ProcessUtil.QuoteArg(currentLogPath);
            }
            else // login
            {
                lblActTitle.Text = "Login window open";
                lblActSub.Text = "Please complete login in the opened browser window and then close it.";
                lblMeta.Visible = false;
                args += " -Login";
                if (!string.IsNullOrEmpty(url) && IsHttpUrl(url)) args += " -Url " + ProcessUtil.QuoteArg(url);
                if (!string.IsNullOrEmpty(platform)) args += " -Platform " + ProcessUtil.QuoteArg(platform);
                args += " -Log " + ProcessUtil.QuoteArg(currentLogPath);
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                currentProc = Process.Start(psi);
                try
                {
                    currentProc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendStderrLine(e.Data); };
                    currentProc.BeginErrorReadLine();
                }
                catch { }
                pollTimer.Start();
            }
            catch (Exception ex)
            {
                btnDownload.Enabled = true;
                btnAccounts.Enabled = true;
                btnCancelJob.Visible = false;
                actProgress.Visible = false;
                lblActTitle.Text = "Failed to launch";
                lblActSub.Text = ex.Message;
                infoBar.Show(Severity.Error, "Launch Failed", ex.Message);
            }
        }

        private void DrainLog()
        {
            if (string.IsNullOrEmpty(currentLogPath) || !File.Exists(currentLogPath)) return;
            try
            {
                string txt;
                using (FileStream fs = new FileStream(currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    txt = sr.ReadToEnd();
                }

                if (txt.Length < lastReadChars)
                {
                    lastReadChars = 0;
                    logRemainder = "";
                    txtLog.Text = "";
                }
                if (txt.Length <= lastReadChars) return;

                string newText = logRemainder + txt.Substring(lastReadChars);
                lastReadChars = txt.Length;

                // Only dispatch complete lines; a write may be in progress.
                int lastNewline = newText.LastIndexOf('\n');
                if (lastNewline < 0)
                {
                    logRemainder = newText;
                    return;
                }
                logRemainder = newText.Substring(lastNewline + 1);

                using (StringReader sr = new StringReader(newText.Substring(0, lastNewline)))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("##EVT|"))
                        {
                            HandleEvent(line.Substring(6));
                        }
                        else if (!string.IsNullOrEmpty(line))
                        {
                            AppendLogLine(line);
                        }
                    }
                }

                TrimLog();
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            catch { }
        }

        private void AppendLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            txtLog.AppendText(line + "\r\n");
        }

        private void AppendStderrLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                if (txtLog.InvokeRequired)
                {
                    txtLog.BeginInvoke(new Action<string>(AppendStderrLine), line);
                    return;
                }
                AppendLogLine("[stderr] " + line);
            }
            catch { }
        }

        private void TrimLog()
        {
            const int maxChars = 300000;
            if (txtLog.TextLength <= maxChars) return;
            string all = txtLog.Text;
            txtLog.Text = "[... earlier log truncated ...]\r\n" + all.Substring(all.Length - 150000);
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            DrainLog();

            if (currentProc != null && currentProc.HasExited)
            {
                pollTimer.Stop();
                int code = currentProc.ExitCode;
                try { currentProc.Dispose(); } catch { }
                currentProc = null;

                // The process may have written its final event between the
                // read above and HasExited becoming true.
                DrainLog();

                btnDownload.Enabled = true;
                btnAccounts.Enabled = true;
                btnCancelJob.Visible = false;
                actProgress.Indeterminate = false;

                bool wasCancelled = cancelRequested;
                cancelRequested = false;

                if (wasCancelled)
                {
                    actProgress.Visible = false;
                    actButtons.Visible = false;
                    lblActTitle.Text = "Cancelled";
                    lblActSub.Text = "The current task was stopped.";
                    infoBar.Show(Severity.Info, "Cancelled", "The current task was stopped.");
                    if (currentJobKind == "login")
                    {
                        InvalidateLoginStates();
                        // Do not let a cancelled auto-retry download an old URL
                        // after a later, unrelated manual login.
                        autoDownloadAfterLogin = false;
                        pendingUrl = null;
                    }
                    return;
                }

                if (currentJobKind == "login")
                {
                    actProgress.Visible = false;
                    InvalidateLoginStates();
                    if (code == 0)
                    {
                        lblActTitle.Text = "Login Saved ✓";
                        lblActSub.Text = "Your session credentials were saved securely.";
                        infoBar.Show(Severity.Success, "Login Succeeded", "Session saved! You can now download private or follow-only posts.");
                        if (autoDownloadAfterLogin && !string.IsNullOrEmpty(pendingUrl))
                        {
                            string url = pendingUrl;
                            autoDownloadAfterLogin = false;
                            pendingUrl = null;
                            StartDownload(url);
                        }
                    }
                    else
                    {
                        autoDownloadAfterLogin = false;
                        pendingUrl = null;
                        lblActTitle.Text = "Login Incomplete";
                        lblActSub.Text = "The login window was closed without saving a session.";
                        infoBar.Show(Severity.Warning, "Login Incomplete", "No active session detected. You can retry anytime via Accounts.");
                    }
                }
                else // download
                {
                    if (code == 0)
                    {
                        actProgress.Value = 1f;
                        actProgress.Visible = false;
                        lblActTitle.Text = "Download Complete ✓";
                        actButtons.Visible = true;
                        infoBar.Show(Severity.Success, "Success", "Media saved to: " + (txtOut.Text.Trim().Length > 0 ? txtOut.Text.Trim() : defaultOut));
                    }
                    else if (code == 3) // login required
                    {
                        actProgress.Visible = false;
                        lblActTitle.Text = "Login Required";
                        lblActSub.Text = "This post is restricted or private. Please authenticate to continue.";
                        string plat = DetectPlatform(txtUrl.Text.Trim());
                        string retryUrl = txtUrl.Text.Trim();
                        pendingUrl = retryUrl;
                        infoBar.Show(Severity.Warning, "Login Required", "This " + plat + " post requires an account session.");
                        infoBar.SetAction("Log In Now", delegate
                        {
                            autoDownloadAfterLogin = true;
                            StartJob("login", retryUrl, plat);
                        });
                    }
                    else
                    {
                        actProgress.Visible = false;
                        lblActTitle.Text = "Download Failed";
                        lblActSub.Text = "Unable to extract media from this link.";
                        infoBar.Show(Severity.Error, "Download Error", "Could not download media. Check Detailed Logs below for details.");
                    }
                }
            }
        }

        private void HandleEvent(string evtPayload)
        {
            string[] parts = evtPayload.Split('|');
            if (parts.Length == 0) return;
            string type = parts[0];

            switch (type)
            {
                case "stage":
                    if (parts.Length > 2)
                    {
                        lblActSub.Text = parts[2];
                    }
                    break;
                case "meta":
                    if (parts.Length >= 5)
                    {
                        string plat = parts[1];
                        string author = parts[2];
                        string count = parts[4];
                        lblMeta.Text = plat + "  •  @" + author + "  (" + count + " item" + (count == "1" ? "" : "s") + ")";
                        lblMeta.Visible = true;
                    }
                    break;
                case "item":
                    if (parts.Length >= 5)
                    {
                        string idx = parts[1];
                        string total = parts[2];
                        string name = parts[3];
                        lblActTitle.Text = "Downloading (" + idx + "/" + total + ")...";
                        lblActSub.Text = name;
                    }
                    break;
                case "prog":
                    if (parts.Length >= 5)
                    {
                        long read = 0, total = 0;
                        long.TryParse(parts[3], out read);
                        long.TryParse(parts[4], out total);
                        if (total > 0)
                        {
                            actProgress.Indeterminate = false;
                            actProgress.Value = (float)read / total;
                            double readMb = Math.Round(read / 1048576.0, 2);
                            double totalMb = Math.Round(total / 1048576.0, 2);
                            int pct = (int)Math.Round((read * 100.0) / total);
                            lblActSub.Text = readMb + " MB / " + totalMb + " MB (" + pct + "%)";
                        }
                    }
                    break;
                case "saved":
                    if (parts.Length >= 6)
                    {
                        string path = parts[5];
                        string mediaType = parts.Length >= 7 ? parts[6] : "";
                        if (string.IsNullOrEmpty(lastDownloadedFilePath))
                        {
                            lastDownloadedFilePath = path;
                            lastDownloadedIsImage = mediaType == "image";
                            UpdatePlayButton();
                        }
                        lblActSub.Text = "Saved: " + parts[3] + " (" + parts[4] + " MB)";
                    }
                    break;
                case "done":
                    if (parts.Length >= 3)
                    {
                        lblActSub.Text = "Finished! Saved " + parts[1] + " file(s).";
                    }
                    break;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                string clip = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clip) && Regex.IsMatch(clip, @"^https?://", RegexOptions.IgnoreCase))
                {
                    txtUrl.Text = clip.Trim();
                }
            }
            catch { }

            // Warm the account-state cache off the UI thread so the first
            // Accounts menu open does not block on PowerShell startup.
            ThreadPool.QueueUserWorkItem(delegate { GetLoginStates(); });

            txtUrl.Inner.Focus();
        }

        protected override void WndProc(ref Message m)
        {
            // WM_SETTINGCHANGE: follow Windows light/dark theme changes live.
            if (m.Msg == 0x001A)
            {
                try { Theme.Refresh(); } catch { }
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (currentProc != null && !currentProc.HasExited)
            {
                DialogResult answer = MessageBox.Show(
                    "A download or login task is still running. Cancel it and exit?",
                    "ReelsDL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                ProcessUtil.KillTree(currentProc);
            }

            if (!string.IsNullOrEmpty(currentLogPath) && File.Exists(currentLogPath))
            {
                try { File.Delete(currentLogPath); } catch { }
            }
            base.OnFormClosing(e);
        }
    }
}
