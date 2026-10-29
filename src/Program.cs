using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Disk Space Analyzer")]
[assembly: AssemblyDescription("Fast disk space analyzer and cleaner for Windows")]
[assembly: AssemblyProduct("Disk Space Analyzer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 sphrd - MIT License")]
[assembly: AssemblyVersion("1.7.3.0")]
[assembly: AssemblyFileVersion("1.7.3.0")]

namespace DiskSpaceAnalyzer
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Headless mode: DiskSpaceAnalyzer.exe --cli scan <folder>  (--scan <folder> is kept as an alias)
            if (args.Length >= 1 && args[0] == "--cli" && !(args.Length >= 4 && args[1] == "screenshot") && !(args.Length == 3 && args[1] == "selftest")) return Headless(args);
            if (args.Length == 2 && args[0] == "--scan") return Headless(new[] { "--cli", "scan", args[1] });

            try { SetCurrentProcessExplicitAppUserModelID("sphrd.DiskSpaceAnalyzer"); } catch (Exception) { }
            Updater.CleanUp();   // the exe replaced by the last update, if any
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Screenshots of every view (for checks and documentation): DiskSpaceAnalyzer.exe --cli screenshot <out folder> <path to scan> [--demo]
            // --demo shows the scanned folder as a made-up drive and hides this computer's drives (for README pictures).
            if (args.Length >= 4 && args[0] == "--cli" && args[1] == "screenshot")
            {
                Application.Run(new MainForm(args[3]) { ShotDir = System.IO.Path.GetFullPath(args[2]), ShotDemo = args.Length > 4 && args[4] == "--demo" });
                return 0;
            }
            // End-to-end self-test of live updates, the delete list, Recycle Bin, contents-only delete, duplicates and export:
            // DiskSpaceAnalyzer.exe --cli selftest <empty scratch folder>   (exit code 0 = passed; details in selftest.log there)
            if (args.Length == 3 && args[0] == "--cli" && args[1] == "selftest")
            {
                var form = new MainForm(SelfTest.Prepare(args[2])) { SelfTestDir = System.IO.Path.GetFullPath(args[2]) };
                Application.Run(form);
                return form.SelfTestFailed ? 1 : 0;
            }
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
                if (args.Length >= 3 && args[1] == "scan") return ConsoleScan(args[2], Array.IndexOf(args, "--mft") > 0, Array.IndexOf(args, "--files") > 0);
                Console.WriteLine();
                Console.WriteLine("Usage: DiskSpaceAnalyzer.exe --cli scan <folder> [--mft] [--files]");
                Console.WriteLine("  --mft    read a whole NTFS drive from its master file table (needs administrator rights)");
                Console.WriteLine("  --files  also list the 10 largest files");
                return 2;
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText("DiskSpaceAnalyzer_error.txt", ex.ToString()); } catch (Exception) { }
                Console.Error.WriteLine("Failed: " + ex.Message.Split('\n')[0]);
                return 1;
            }
        }

        static int ConsoleScan(string path, bool mft, bool files)
        {
            var sw = Stopwatch.StartNew();
            var s = new Scanner();
            var root = s.Run(System.IO.Path.GetFullPath(path), mft);
            sw.Stop();
            Console.WriteLine();
            Console.WriteLine("{0}\t{1} bytes\t{2} on disk\t{3} files\t{4} dirs\t{5:0.000} s\t{6}", root.Name, root.Bytes, root.Alloc, root.Files, root.Dirs,
                sw.Elapsed.TotalSeconds, s.Method);
            if (mft) Console.WriteLine("master file table: " + s.MftNote);
            if (root.Children != null)
                foreach (var c in root.Children)
                    Console.WriteLine("  {0,-40} {1,18:N0} bytes {2,18:N0} on disk {3,10:N0} files", c.Name, c.Bytes, c.Alloc, c.Files);
            if (files)
            {
                long n, sz;
                foreach (var f in FileIndex.Largest(root, 10, null, out n, out sz))
                    Console.WriteLine("  file {0,18:N0}  {1}", f.Bytes, f.FullPath);
            }
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
