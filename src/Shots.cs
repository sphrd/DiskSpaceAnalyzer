using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>Screenshot mode (--cli screenshot): after the first scan, every view is opened and saved as a PNG, then the app closes.</summary>
    internal sealed partial class MainForm
    {
        public string ShotDir;
        bool shotsStarted;

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        void SaveShot(string name)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(Handle, hdc, 2);   // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(Path.Combine(ShotDir, name + ".png"), ImageFormat.Png);
            }
        }

        async void RunShots()
        {
            try
            {
                Directory.CreateDirectory(ShotDir);
                await Task.Delay(1500);
                SaveShot("1-contents");
                for (int i = 1; i < pages.Count; i++)
                {
                    var p = pages[i];
                    if (p == pBasket)
                        foreach (var it in pFiles.List.Items.Cast<ListViewItem>().Select(x => x.Tag as ItemInfo).Where(x => x != null && x.CanDelete).Take(4))
                            foreach (var t in DeleteTarget.From(it)) AddTarget(t);
                    viewTabs.SelectedIndex = i;
                    if (p == pDupes)
                    {
                        FindDuplicates();
                        for (int k = 0; k < 600 && dupeFinder != null; k++) await Task.Delay(200);
                    }
                    if (p == pSuggest) for (int k = 0; k < 300 && suggestions == null; k++) await Task.Delay(200);
                    await Task.Delay(2000);
                    SaveShot((i + 1) + "-" + p.Title.Split(' ')[0].ToLowerInvariant());
                }
                viewTabs.SelectedIndex = 0;
                measure.SelectedIndex = 1;
                await Task.Delay(1500);
                SaveShot((pages.Count + 1) + "-size-on-disk");
            }
            catch (Exception ex) { try { File.WriteAllText(Path.Combine(ShotDir, "error.txt"), ex.ToString()); } catch (Exception) { } }
            Close();
        }
    }
}
