using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Disk Space Analyzer")]
[assembly: AssemblyDescription("Fast disk space analyzer and cleaner for Windows")]
[assembly: AssemblyProduct("Disk Space Analyzer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 sphrd - MIT License")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace DiskSpaceAnalyzer
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Headless mode for benchmarking/verification: DiskSpaceAnalyzer.exe --scan <folder>
            if (args.Length == 2 && args[0] == "--scan") return ConsoleScan(args[1]);

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

        static int ConsoleScan(string path)
        {
            // When stdout is already redirected (piped/captured), write straight to it;
            // only attach to the parent console for interactive use.
            IntPtr stdout = GetStdHandle(-11);
            if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(-1);
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
    }
}
