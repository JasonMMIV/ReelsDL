// =============================================================================
//  ReelsDL - Main Executable Entry Point
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using ReelsDL.UI;

static class Launcher
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint STD_OUTPUT_HANDLE = 0xFFFFFFF5;
    private const uint FILE_TYPE_UNKNOWN = 0;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [STAThread]
    static void Main(string[] args)
    {
        // If CLI arguments were supplied, delegate to the headless engine directly
        if (args != null && args.Length > 0)
        {
            RunCli(args);
            return;
        }

        try
        {
            Native.SetProcessDPIAware();
        }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool createdNew;
        using (Mutex mutex = new Mutex(true, @"Local\ReelsDL.SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("ReelsDL is already running.", "ReelsDL",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application.Run(new ReelsDL.MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("ReelsDL encountered an unexpected error:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                    "ReelsDL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static void RunCli(string[] args)
    {
        // ReelsDL.exe is a WinExe. Attach to the caller's console (cmd,
        // PowerShell) and rebind Console to CONOUT$, otherwise stdout stays
        // a null stream and CLI output is invisible.
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }

        // Only rebind when the inherited stdout handle is unusable. A pipe
        // (redirection) or a real console must keep working normally.
        IntPtr stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (stdOut == IntPtr.Zero || stdOut == INVALID_HANDLE_VALUE || GetFileType(stdOut) == FILE_TYPE_UNKNOWN)
        {
            BindConsole();
        }

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string ps1 = Path.Combine(dir, "ReelsDL.ps1");
        if (!File.Exists(ps1))
        {
            string devPs1 = Path.Combine(dir, "..", "src", "ReelsDL.ps1");
            if (File.Exists(devPs1)) ps1 = Path.GetFullPath(devPs1);
        }

        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        string joinedArgs = string.Join(" ", Array.ConvertAll(args, ProcessUtil.QuoteArg));
        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + ProcessUtil.QuoteArg(ps1) + " " + joinedArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir
        };

        try
        {
            var p = Process.Start(psi);

            string stdout = "";
            string stderr = "";
            Thread outThread = new Thread(delegate() { try { stdout = p.StandardOutput.ReadToEnd(); } catch { } });
            Thread errThread = new Thread(delegate() { try { stderr = p.StandardError.ReadToEnd(); } catch { } });
            outThread.IsBackground = true;
            errThread.IsBackground = true;
            outThread.Start();
            errThread.Start();

            p.WaitForExit();
            outThread.Join(2000);
            errThread.Join(2000);

            if (!string.IsNullOrEmpty(stdout)) { Console.Out.Write(stdout); Console.Out.Flush(); }
            if (!string.IsNullOrEmpty(stderr)) { Console.Error.Write(stderr); Console.Error.Flush(); }
            Environment.Exit(p.ExitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            Environment.Exit(1);
        }
    }

    private static void BindConsole()
    {
        try
        {
            IntPtr handle = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE_VALUE) return;

            Encoding encoding = Console.OutputEncoding;
            StreamWriter outWriter = new StreamWriter(
                new FileStream(new SafeFileHandle(handle, true), FileAccess.Write), encoding);
            outWriter.AutoFlush = true;
            Console.SetOut(outWriter);

            IntPtr errorHandle = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (errorHandle != INVALID_HANDLE_VALUE)
            {
                StreamWriter errorWriter = new StreamWriter(
                    new FileStream(new SafeFileHandle(errorHandle, true), FileAccess.Write), encoding);
                errorWriter.AutoFlush = true;
                Console.SetError(errorWriter);
            }
            else
            {
                Console.SetError(outWriter);
            }
        }
        catch { }
    }
}
