// =============================================================================
//  ReelsDL - Windows 11 Fluent Uninstaller
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using ReelsDL.UI;

namespace ReelsDL.Installer
{
    static class UninstallProgram
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

                foreach (string a in args)
                {
                    if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("/s", StringComparison.OrdinalIgnoreCase))
                    {
                        silent = true;
                    }
                }

                if (silent)
                {
                    try { PerformUninstall(false); }
                    catch { Environment.ExitCode = 1; }
                    return;
                }

                Application.Run(new UninstallForm());
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("Uninstall encountered an error:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                        AppInfo.Name + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Removes shortcuts, registry data and (optionally) user data. The
        /// installation folder is deleted by a detached cleanup script after
        /// this process exits, because Uninstall.exe cannot delete itself.
        /// Returns null on success, or a warning message.
        /// </summary>
        public static string PerformUninstall(bool deleteUserData)
        {
            string installDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\', '/');
            ValidateInstallDir(installDir);

            List<string> warnings = new List<string>();

            // 1. Terminate instances installed in this folder only.
            ProcessUtil.KillInstalledApp(installDir);

            // 2. Remove Desktop shortcut
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopLnk = Path.Combine(desktop, AppInfo.Name + ".lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
            }
            catch (Exception ex) { warnings.Add("Desktop shortcut: " + ex.Message); }

            // 3. Remove Start Menu folder
            try
            {
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string smDir = Path.Combine(startMenu, AppInfo.Name);
                if (Directory.Exists(smDir)) Directory.Delete(smDir, true);
            }
            catch (Exception ex) { warnings.Add("Start Menu shortcuts: " + ex.Message); }

            // 4. Remove Windows Registry Uninstall key
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
                {
                    if (key != null) key.DeleteSubKeyTree(AppInfo.Name, false);
                }
            }
            catch (Exception ex) { warnings.Add("Add/Remove Programs entry: " + ex.Message); }

            // 5. Remove user data if requested (%LOCALAPPDATA%\ReelsDL)
            if (deleteUserData)
            {
                try
                {
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string dataDir = Path.Combine(localApp, AppInfo.Name);
                    if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                }
                catch (Exception ex) { warnings.Add("Saved login data: " + ex.Message); }
            }

            // 6. Schedule deletion of the installation folder after exit.
            try
            {
                ScheduleCleanup(installDir);
            }
            catch (Exception ex)
            {
                warnings.Add("Installation folder cleanup: " + ex.Message);
            }

            return warnings.Count == 0 ? null : string.Join("\n", warnings.ToArray());
        }

        private static void ValidateInstallDir(string installDir)
        {
            string root = Path.GetPathRoot(installDir).TrimEnd('\\', '/');
            if (string.Equals(installDir, root, StringComparison.OrdinalIgnoreCase) ||
                installDir.Length <= root.Length)
            {
                throw new InvalidOperationException("Refusing to delete a drive root: " + installDir);
            }

            // Only delete folders that look like an actual ReelsDL install.
            if (!File.Exists(Path.Combine(installDir, "ReelsDL.exe")) ||
                !File.Exists(Path.Combine(installDir, "Uninstall.exe")))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an unexpected folder (ReelsDL.exe / Uninstall.exe not found): " + installDir);
            }
        }

        private static void ScheduleCleanup(string installDir)
        {
            int pid = Process.GetCurrentProcess().Id;
            string cmdPath = Path.Combine(Path.GetTempPath(),
                "reelsdl_uninstall_" + Guid.NewGuid().ToString("N") + ".cmd");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            // The uninstaller is usually launched from a shortcut whose
            // working directory is the install folder. cmd.exe inherits it,
            // and rd would then fail to remove its own current directory.
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine(":wait");
            sb.AppendLine("tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  ping 127.0.0.1 -n 2 >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");

            // Delete only the known payload files. This avoids deleting
            // unrelated files if the user chose a custom folder.
            string[] payload = new string[] { "ReelsDL.exe", "ReelsDL.ps1", "app.ico", "Uninstall.exe" };
            foreach (string file in payload)
            {
                sb.AppendLine("del /f /q \"" + EscapeBatch(Path.Combine(installDir, file)) + "\" 2>nul");
            }
            sb.AppendLine("rd \"" + EscapeBatch(installDir) + "\" 2>nul");
            sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");

            File.WriteAllText(cmdPath, sb.ToString(),
                Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + ProcessUtil.QuoteArg(cmdPath),
                WorkingDirectory = Path.GetTempPath(),
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }

        private static string EscapeBatch(string value)
        {
            return value.Replace("%", "%%");
        }
    }

    public class UninstallForm : Form
    {
    private FluentCheckBox chkData;
    private FluentButton btnUninstall;
    private FluentButton btnCancel;
    private bool uninstalling;

    public UninstallForm()
    {
        Text = AppInfo.Name + " Uninstall";
        Size = new Size(Theme.S(500), Theme.S(290));
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

        BuildUi();
    }

    private void BuildUi()
    {
        Card card = new Card
        {
            Location = new Point(Theme.S(24), Theme.S(20)),
            Size = new Size(ClientSize.Width - Theme.S(48), ClientSize.Height - Theme.S(90)),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(Theme.S(20))
        };
        Controls.Add(card);

        FluentLabel lblTitle = new FluentLabel
        {
            Text = "Uninstall " + AppInfo.Name,
            Font = Theme.Semibold(13f),
            Location = new Point(Theme.S(20), Theme.S(16))
        };
        card.Controls.Add(lblTitle);

        FluentLabel lblMsg = new FluentLabel
        {
            Text = "Are you sure you want to completely remove " + AppInfo.Name + " and all of its components from your computer?",
            Font = Theme.Font(9.5f),
            Secondary = true,
            Location = new Point(Theme.S(20), Theme.S(48)),
            MaximumSize = new Size(card.Width - Theme.S(40), 0)
        };
        card.Controls.Add(lblMsg);

        chkData = new FluentCheckBox
        {
            Text = "Also delete saved login sessions and cached data",
            Location = new Point(Theme.S(22), Theme.S(105)),
            Width = card.Width - Theme.S(44),
            Checked = false
        };
        card.Controls.Add(chkData);

        // Footer buttons
        Panel pnlFooter = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.S(60),
            Padding = new Padding(Theme.S(24), Theme.S(10), Theme.S(24), Theme.S(10)),
            BackColor = Color.Transparent
        };
        Controls.Add(pnlFooter);

        btnUninstall = new FluentButton
        {
            Text = "Uninstall",
            Kind = ButtonKind.Standard,
            ContentColor = Theme.Danger,
            Height = Theme.S(34),
            Cursor = Cursors.Hand
        };
        btnUninstall.FitWidth(Theme.S(100));
        btnUninstall.Location = new Point(pnlFooter.Width - btnUninstall.Width - Theme.S(115), Theme.S(12));
        btnUninstall.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnUninstall.Click += delegate
        {
            bool deleteData = chkData.Checked;
            uninstalling = true;
            btnUninstall.Enabled = false;
            btnCancel.Enabled = false;
            ControlBox = false;
            Cursor = Cursors.WaitCursor;

            // File deletion and process termination can take seconds; keep
            // the window responsive instead of blocking the UI thread.
            Thread worker = new Thread(delegate()
            {
                string warnings = null;
                Exception failure = null;
                try { warnings = UninstallProgram.PerformUninstall(deleteData); }
                catch (Exception ex) { failure = ex; }

                try
                {
                    this.Invoke(new Action(delegate()
                    {
                        Cursor = Cursors.Default;

                        if (failure != null)
                        {
                            uninstalling = false;
                            ControlBox = true;
                            btnUninstall.Enabled = true;
                            btnCancel.Enabled = true;
                            MessageBox.Show("Uninstall failed:\n\n" + failure.Message,
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        Hide();
                        if (string.IsNullOrEmpty(warnings))
                        {
                            MessageBox.Show(AppInfo.Name + " has been removed. The installation folder will be deleted after this window closes.",
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("Uninstall completed with warnings:\n\n" + warnings +
                                "\n\nThe installation folder will be deleted after this window closes.",
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        Application.Exit();
                    }));
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        };
        pnlFooter.Controls.Add(btnUninstall);

        btnCancel = new FluentButton
        {
            Text = "Cancel",
            Kind = ButtonKind.Standard,
            Height = Theme.S(34),
            Cursor = Cursors.Hand
        };
        btnCancel.FitWidth(Theme.S(90));
        btnCancel.Location = new Point(pnlFooter.Width - btnCancel.Width - Theme.S(24), Theme.S(12));
        btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnCancel.Click += delegate { Application.Exit(); };
        pnlFooter.Controls.Add(btnCancel);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (uninstalling && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
}
