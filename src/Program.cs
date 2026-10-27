using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Disk Space Analyzer")]
[assembly: AssemblyDescription("Fast disk space analyzer and cleaner for Windows")]
[assembly: AssemblyProduct("Disk Space Analyzer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 sphrd - MIT License")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace DiskSpaceAnalyzer
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Headless mode: DiskSpaceAnalyzer.exe --cli scan <folder>  (--scan <folder> is kept as an alias)
            if (args.Length >= 1 && args[0] == "--cli") return Headless(args);
            if (args.Length == 2 && args[0] == "--scan") return Headless(new[] { "--cli", "scan", args[1] });

            try { SetCurrentProcessExplicitAppUserModelID("sphrd.DiskSpaceAnalyzer"); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm(args.Length > 0 ? args[0] : null)); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Disk Space Analyzer - startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }

        static int Headless(string[] args)
        {
            // When stdout is already redirected (piped/captured), write straight to it;
            // only attach to the parent console for interactive use.
            IntPtr stdout = GetStdHandle(-11);
            if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(-1);
            try
            {
                if (args.Length == 3 && args[1] == "scan") return ConsoleScan(args[2]);
                Console.WriteLine();
                Console.WriteLine("Usage: DiskSpaceAnalyzer.exe --cli scan <folder>");
                return 2;
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText("DiskSpaceAnalyzer_error.txt", ex.ToString()); } catch (Exception) { }
                Console.Error.WriteLine("Failed: " + ex.Message.Split('\n')[0]);
                return 1;
            }
        }

        static int ConsoleScan(string path)
        {
            var sw = Stopwatch.StartNew();
            var root = new Scanner(10).Run(System.IO.Path.GetFullPath(path));
            sw.Stop();
            Console.WriteLine();
            Console.WriteLine("{0}\t{1} bytes\t{2} files\t{3} dirs\t{4:0.000} s", root.Name, root.Size, root.Files, root.Dirs, sw.Elapsed.TotalSeconds);
            if (root.Children != null)
                foreach (var c in root.Children)
                    Console.WriteLine("  {0,-40} {1,15:N0} bytes  {2,10:N0} files", c.Name, c.Size, c.Files);
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int which);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
