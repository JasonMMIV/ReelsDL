// =============================================================================
//  ReelsDL - Windows 11 Fluent Setup Installer
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using ReelsDL.UI;

namespace ReelsDL.Installer
{
    static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool silent = false;
            try
            {
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string customDir = null;
                foreach (string a in args)
                {
                    if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("/s", StringComparison.OrdinalIgnoreCase))
                    {
                        silent = true;
                    }
                    else if (a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        customDir = a.Substring(5).Trim('"', '\'');
                    }
                }

                string defaultTarget = customDir;
                if (string.IsNullOrEmpty(defaultTarget))
                {
                    defaultTarget = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", AppInfo.Name);
                }

                if (silent)
                {
                    string warnings = InstallCore(defaultTarget, true, true);
                    if (!string.IsNullOrEmpty(warnings))
                    {
                        // Silent installs cannot show a dialog; leave a log
                        // and a non-zero exit code so deployment scripts see it.
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(Path.GetTempPath(), "ReelsDL-Setup.log"),
                                "[" + DateTime.Now.ToString("s") + "] " + warnings + Environment.NewLine);
                        }
                        catch { }
                        Environment.ExitCode = 2;
                    }
                    return;
                }

                Application.Run(new SetupForm(defaultTarget));
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("Setup encountered an unexpected error:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                        "ReelsDL Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.ExitCode = 1;
            }
        }

        public static string InstallCore(string targetDir, bool createDesktop, bool createStartMenu)
        {
            List<string> warnings = new List<string>();

            // Never scatter payload files into a drive root.
            string fullTarget = Path.GetFullPath(targetDir).TrimEnd('\\', '/');
            string targetRoot = Path.GetPathRoot(fullTarget).TrimEnd('\\', '/');
            if (string.Equals(fullTarget, targetRoot, StringComparison.OrdinalIgnoreCase) ||
                fullTarget.Length <= targetRoot.Length)
            {
                throw new InvalidOperationException("Refusing to install into a drive root: " + targetDir);
            }
            targetDir = fullTarget;

            // 1. Terminate instances installed in this folder only. A portable
            // copy elsewhere must keep running.
            ProcessUtil.KillInstalledApp(targetDir);

            // 2. Ensure target directory exists
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 3. Extract to a staging folder first, then copy into place. A
            // missing payload or a full disk must not leave a half-written
            // installation behind.
            Assembly asm = typeof(SetupProgram).Assembly;
            // Engine first, launcher last: an interrupted swap leaves the old
            // exe with the new engine (compatible) instead of the reverse.
            string[] payloadFiles = new string[] { "ReelsDL.ps1", "app.ico", "Uninstall.exe", "ReelsDL.exe" };
            long totalBytes = 0;
            string staging = Path.Combine(Path.GetTempPath(), "reelsdl_payload_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(staging);
                foreach (string fName in payloadFiles)
                {
                    string dest = Path.Combine(staging, fName);
                    using (Stream resStream = asm.GetManifestResourceStream(fName))
                    {
                        if (resStream == null)
                        {
                            throw new InvalidOperationException("Embedded installer payload missing: " + fName);
                        }
                        using (FileStream fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                        {
                            resStream.CopyTo(fs);
                        }
                        totalBytes += resStream.Length;
                    }
                }

                // Write every payload next to its destination first, then
                // swap it in atomically. A disk-full or extraction failure
                // cannot corrupt the existing installation.
                List<string> pending = new List<string>();
                foreach (string fName in payloadFiles)
                {
                    string tmp = Path.Combine(targetDir, fName + ".new");
                    File.Copy(Path.Combine(staging, fName), tmp, true);
                    pending.Add(tmp);
                }
                for (int i = 0; i < payloadFiles.Length; i++)
                {
                    string dest = Path.Combine(targetDir, payloadFiles[i]);
                    if (File.Exists(dest)) File.Replace(pending[i], dest, null);
                    else File.Move(pending[i], dest);
                }
            }
            finally
            {
                foreach (string fName in payloadFiles)
                {
                    try
                    {
                        string tmp = Path.Combine(targetDir, fName + ".new");
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch { }
                }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }

            string mainExe = Path.Combine(targetDir, "ReelsDL.exe");
            string icoPath = Path.Combine(targetDir, "app.ico");
            string uninstExe = Path.Combine(targetDir, "Uninstall.exe");

            // 4. Create Desktop Shortcut
            if (createDesktop)
            {
                string lnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    AppInfo.Name + ".lnk");
                try
                {
                    CreateShortcut(lnk, mainExe, targetDir, icoPath, AppInfo.Name + " - " + AppInfo.Tagline);
                    if (!File.Exists(lnk)) warnings.Add("Desktop shortcut was not created.");
                }
                catch (Exception ex) { warnings.Add("Desktop shortcut: " + ex.Message); }
            }

            // 5. Create Start Menu Shortcuts
            if (createStartMenu)
            {
                string smFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    AppInfo.Name);
                string lnkApp = Path.Combine(smFolder, AppInfo.Name + ".lnk");
                string lnkUninst = Path.Combine(smFolder, "Uninstall " + AppInfo.Name + ".lnk");
                try
                {
                    if (!Directory.Exists(smFolder)) Directory.CreateDirectory(smFolder);
                    CreateShortcut(lnkApp, mainExe, targetDir, icoPath, AppInfo.Name + " - " + AppInfo.Tagline);
                    CreateShortcut(lnkUninst, uninstExe, targetDir, icoPath, "Uninstall " + AppInfo.Name);
                    if (!File.Exists(lnkApp) || !File.Exists(lnkUninst))
                        warnings.Add("One or more Start Menu shortcuts were not created.");
                }
                catch (Exception ex) { warnings.Add("Start Menu shortcuts: " + ex.Message); }
            }

            // 6. Register in Windows Add/Remove Programs (Registry)
            string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Name;
            try
            {
                using (RegistryKey uninstKey = Registry.CurrentUser.CreateSubKey(uninstallKeyPath))
                {
                    if (uninstKey == null)
                    {
                        warnings.Add("Could not create the Add/Remove Programs registry key.");
                    }
                    else
                    {
                        uninstKey.SetValue("DisplayName", AppInfo.Name, RegistryValueKind.String);
                        uninstKey.SetValue("DisplayVersion", AppInfo.Version, RegistryValueKind.String);
                        uninstKey.SetValue("Publisher", AppInfo.Name + " Studio", RegistryValueKind.String);
                        uninstKey.SetValue("InstallLocation", targetDir, RegistryValueKind.String);
                        uninstKey.SetValue("DisplayIcon", icoPath, RegistryValueKind.String);
                        uninstKey.SetValue("UninstallString", "\"" + uninstExe + "\"", RegistryValueKind.String);
                        uninstKey.SetValue("QuietUninstallString", "\"" + uninstExe + "\" /quiet", RegistryValueKind.String);
                        uninstKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                        uninstKey.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
                        uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }

                using (RegistryKey check = Registry.CurrentUser.OpenSubKey(uninstallKeyPath))
                {
                    if (check == null || check.GetValue("UninstallString") == null)
                        warnings.Add("Add/Remove Programs entry could not be verified.");
                }
            }
            catch (Exception ex) { warnings.Add("Add/Remove Programs entry: " + ex.Message); }

            return warnings.Count == 0 ? null : string.Join("\n", warnings.ToArray());
        }

        public static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string iconPath, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                shortcut.IconLocation = iconPath + ",0";
            }
            shortcut.Description = description;
            shortcut.Save();
        }
    }

    public class SetupForm : Form
    {
    private string targetDir;

    // View panels
    private Card cardOptions;
    private FluentTextBox txtPath;
    private FluentCheckBox chkDesktop;
    private FluentCheckBox chkStartMenu;
    private FluentButton btnInstall;
    private FluentButton btnCancel;
    private bool installing;

    private Card cardProgress;
    private FluentLabel lblProgTitle;
    private FluentLabel lblProgSub;
    private ProgressLine progLine;

    private Card cardComplete;
    private FluentLabel lblCompTitle;
    private FluentLabel lblCompSub;
    private FluentCheckBox chkLaunch;
    private FluentButton btnFinish;

    public SetupForm(string initialTarget)
    {
        targetDir = initialTarget;
        Text = AppInfo.Name + " Setup";
        Size = new Size(Theme.S(560), Theme.S(440));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.WindowBg;
        Font = Theme.Font(10f);

        try
        {
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch { }

        HandleCreated += delegate
        {
            Native.ApplyWindowChrome(this, Theme.WindowBg);
        };

        BuildHeader();
        BuildBody();
        BuildFooter();

        SwitchView(cardOptions);
    }

    private void BuildHeader()
    {
        Panel pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.S(72),
            Padding = new Padding(Theme.S(24), Theme.S(16), Theme.S(24), 0),
            BackColor = Color.Transparent
        };
        Controls.Add(pnlHeader);

        PictureBox pic = new PictureBox
        {
            Location = new Point(Theme.S(24), Theme.S(16)),
            Size = new Size(Theme.S(40), Theme.S(40)),
            SizeMode = PictureBoxSizeMode.StretchImage
        };
        try
        {
            if (this.Icon != null) pic.Image = Bitmap.FromHicon(this.Icon.Handle);
        }
        catch { }
        pnlHeader.Controls.Add(pic);

        FluentLabel lblTitle = new FluentLabel
        {
            Text = AppInfo.Name + " Setup",
            Font = Theme.Semibold(14f),
            Location = new Point(Theme.S(74), Theme.S(14))
        };
        pnlHeader.Controls.Add(lblTitle);

        FluentLabel lblSub = new FluentLabel
        {
            Text = "Universal Short Video Downloader for Windows",
            Font = Theme.Font(9.5f),
            Secondary = true,
            Location = new Point(Theme.S(74), Theme.S(40))
        };
        pnlHeader.Controls.Add(lblSub);
    }

    private void BuildBody()
    {
        Panel pnlBody = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.S(24), Theme.S(8), Theme.S(24), Theme.S(12)),
            BackColor = Color.Transparent
        };
        Controls.Add(pnlBody);
        pnlBody.BringToFront();

        // 1. Options Card
        cardOptions = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.S(20))
        };
        pnlBody.Controls.Add(cardOptions);

        FluentLabel lblDest = new FluentLabel
        {
            Text = "Destination Folder",
            Font = Theme.Semibold(10.5f),
            Location = new Point(Theme.S(20), Theme.S(16))
        };
        cardOptions.Controls.Add(lblDest);

        txtPath = new FluentTextBox
        {
            Text = targetDir,
            Font = Theme.Font(9.5f),
            Height = Theme.S(36),
            Location = new Point(Theme.S(20), Theme.S(42)),
            Width = cardOptions.Width - Theme.S(130),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        cardOptions.Controls.Add(txtPath);

        FluentButton btnBrowse = new FluentButton
        {
            Text = "Browse...",
            Kind = ButtonKind.Standard,
            Height = Theme.S(36),
            Location = new Point(cardOptions.Width - Theme.S(100), Theme.S(42)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnBrowse.FitWidth(Theme.S(80));
        btnBrowse.Click += delegate
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.SelectedPath = txtPath.Text;
                fbd.Description = "Select " + AppInfo.Name + " Installation Directory";
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    txtPath.Text = fbd.SelectedPath;
                }
            }
        };
        cardOptions.Controls.Add(btnBrowse);

        FluentLabel lblOpts = new FluentLabel
        {
            Text = "Shortcuts & Integrations",
            Font = Theme.Semibold(10.5f),
            Location = new Point(Theme.S(20), Theme.S(96))
        };
        cardOptions.Controls.Add(lblOpts);

        chkDesktop = new FluentCheckBox
        {
            Text = "Create a Desktop shortcut",
            Checked = true,
            Location = new Point(Theme.S(22), Theme.S(124)),
            Width = Theme.S(300)
        };
        cardOptions.Controls.Add(chkDesktop);

        chkStartMenu = new FluentCheckBox
        {
            Text = "Create a Start Menu shortcut",
            Checked = true,
            Location = new Point(Theme.S(22), Theme.S(154)),
            Width = Theme.S(300)
        };
        cardOptions.Controls.Add(chkStartMenu);

        // 2. Progress Card
        cardProgress = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.S(20)),
            Visible = false
        };
        pnlBody.Controls.Add(cardProgress);

        lblProgTitle = new FluentLabel
        {
            Text = "Installing " + AppInfo.Name + "...",
            Font = Theme.Semibold(12f),
            Location = new Point(Theme.S(20), Theme.S(40))
        };
        cardProgress.Controls.Add(lblProgTitle);

        lblProgSub = new FluentLabel
        {
            Text = "Extracting components and registering application...",
            Font = Theme.Font(9.5f),
            Secondary = true,
            Location = new Point(Theme.S(20), Theme.S(68))
        };
        cardProgress.Controls.Add(lblProgSub);

        progLine = new ProgressLine
        {
            Location = new Point(Theme.S(20), Theme.S(105)),
            Width = cardProgress.Width - Theme.S(40),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Indeterminate = true
        };
        cardProgress.Controls.Add(progLine);

        // 3. Complete Card
        cardComplete = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.S(20)),
            Visible = false
        };
        pnlBody.Controls.Add(cardComplete);

        lblCompTitle = new FluentLabel
        {
            Text = "Installation Completed!",
            Font = Theme.Semibold(13.5f),
            Override = Theme.Success,
            Location = new Point(Theme.S(20), Theme.S(30))
        };
        cardComplete.Controls.Add(lblCompTitle);

        lblCompSub = new FluentLabel
        {
            Text = AppInfo.Name + " has been successfully installed on your computer.\nYou can launch it anytime from the Desktop or Start Menu.",
            Font = Theme.Font(10f),
            Secondary = true,
            Location = new Point(Theme.S(20), Theme.S(65))
        };
        cardComplete.Controls.Add(lblCompSub);

        chkLaunch = new FluentCheckBox
        {
            Text = "Launch " + AppInfo.Name + " now",
            Checked = true,
            Font = Theme.Semibold(10f),
            Location = new Point(Theme.S(22), Theme.S(135)),
            Width = Theme.S(300)
        };
        cardComplete.Controls.Add(chkLaunch);
    }

    private void BuildFooter()
    {
        Panel pnlFooter = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.S(64),
            Padding = new Padding(Theme.S(24), Theme.S(12), Theme.S(24), Theme.S(12)),
            BackColor = Color.Transparent
        };
        Controls.Add(pnlFooter);

        btnInstall = new FluentButton
        {
            Text = "Install",
            Kind = ButtonKind.Accent,
            Height = Theme.S(36),
            Cursor = Cursors.Hand
        };
        btnInstall.FitWidth(Theme.S(100));
        btnInstall.Location = new Point(pnlFooter.Width - btnInstall.Width - Theme.S(115), Theme.S(14));
        btnInstall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnInstall.Click += BtnInstall_Click;
        pnlFooter.Controls.Add(btnInstall);

        btnCancel = new FluentButton
        {
            Text = "Cancel",
            Kind = ButtonKind.Standard,
            Height = Theme.S(36),
            Cursor = Cursors.Hand
        };
        btnCancel.FitWidth(Theme.S(90));
        btnCancel.Location = new Point(pnlFooter.Width - btnCancel.Width - Theme.S(24), Theme.S(14));
        btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnCancel.Click += delegate { Application.Exit(); };
        pnlFooter.Controls.Add(btnCancel);

        btnFinish = new FluentButton
        {
            Text = "Finish",
            Kind = ButtonKind.Accent,
            Height = Theme.S(36),
            Cursor = Cursors.Hand,
            Visible = false
        };
        btnFinish.FitWidth(Theme.S(100));
        btnFinish.Location = new Point(pnlFooter.Width - btnFinish.Width - Theme.S(24), Theme.S(14));
        btnFinish.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnFinish.Click += BtnFinish_Click;
        pnlFooter.Controls.Add(btnFinish);
    }

    private void SwitchView(Control view)
    {
        cardOptions.Visible = (view == cardOptions);
        cardProgress.Visible = (view == cardProgress);
        cardComplete.Visible = (view == cardComplete);
    }

    private void BtnInstall_Click(object sender, EventArgs e)
    {
        targetDir = txtPath.Text.Trim();
        if (string.IsNullOrEmpty(targetDir))
        {
            MessageBox.Show("Please choose a valid destination directory.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (ProcessUtil.IsInstalledAppRunning(targetDir))
            {
                DialogResult answer = MessageBox.Show(
                    "The installed copy of ReelsDL is currently running. Close it and continue with the installation?",
                    "ReelsDL Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }
        }
        catch { }

        installing = true;
        btnInstall.Enabled = false;
        btnCancel.Enabled = false;
        SwitchView(cardProgress);

        bool desk = chkDesktop.Checked;
        bool sm = chkStartMenu.Checked;

        Thread t = new Thread(() =>
        {
            try
            {
                Thread.Sleep(300);
                string warnings = SetupProgram.InstallCore(targetDir, desk, sm);
                Thread.Sleep(300);

                this.Invoke(new Action(() =>
                {
                    installing = false;
                    btnInstall.Visible = false;
                    btnCancel.Visible = false;
                    btnFinish.Visible = true;
                    SwitchView(cardComplete);

                    if (!string.IsNullOrEmpty(warnings))
                    {
                        MessageBox.Show("Installation completed with warnings:\n\n" + warnings,
                            "ReelsDL Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (Exception ex)
            {
                this.Invoke(new Action(() =>
                {
                    installing = false;
                    MessageBox.Show("Installation failed:\n\n" + ex.Message, "ReelsDL Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnInstall.Enabled = true;
                    btnCancel.Enabled = true;
                    SwitchView(cardOptions);
                }));
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private void BtnFinish_Click(object sender, EventArgs e)
    {
        if (chkLaunch.Checked)
        {
            string mainExe = Path.Combine(targetDir, "ReelsDL.exe");
            if (File.Exists(mainExe))
            {
                try { Process.Start(mainExe); } catch { }
            }
        }
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MessageBox.Show("Installation is still running. Please wait for it to finish.",
                AppInfo.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        base.OnFormClosing(e);
    }
}
}
