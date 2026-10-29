# Checks the Finder layout of a mounted DiskSpaceAnalyzer-macOS.dmg (CI) and renders how Finder shows it.
#   python dmg_check.py <mount point> <dmg_art binary> <out.png>
# Needs the ds_store package (installed with dmgbuild by build.sh). Exit code 0 = layout as intended.
import glob
import os
import subprocess
import sys

from ds_store import DSStore

mnt, art, out = sys.argv[1], sys.argv[2], sys.argv[3]
app = 'Disk Space Analyzer.app'
failed = False


def need(ok, what):
    global failed
    print(('OK    ' if ok else 'FAIL  ') + what)
    if not ok:
        failed = True


need(os.path.isdir(os.path.join(mnt, app)), app + ' on the volume')
link = os.path.join(mnt, 'Applications')
need(os.path.islink(link) and os.readlink(link) == '/Applications', 'Applications is a link to /Applications')
backgrounds = [p for p in glob.glob(os.path.join(mnt, '.background*')) + glob.glob(os.path.join(mnt, '.background', '*'))
               if os.path.isfile(p)]   # dmgbuild writes .background.tiff
need(len(backgrounds) == 1, 'hidden background picture: ' + ', '.join(os.path.basename(b) for b in backgrounds))
store = os.path.join(mnt, '.DS_Store')
need(os.path.isfile(store), '.DS_Store present')

entries = {}
if os.path.isfile(store):
    with DSStore.open(store, 'r') as d:
        for e in d:
            code = e.code.decode() if isinstance(e.code, bytes) else e.code
            value = e.value
            shown = {k: v for k, v in value.items() if k != 'backgroundImageAlias'} if isinstance(value, dict) else value
            print('  .DS_Store  %r  %s  %s' % (e.filename, code, shown))
            entries[(e.filename, code)] = value

icvp = entries.get(('.', 'icvp'), {})
bwsp = entries.get(('.', 'bwsp'), {})
need(icvp.get('iconSize') == 128, 'icon view, icon size 128')
need(icvp.get('backgroundType') == 2 and bool(icvp.get('backgroundImageAlias')), 'background picture set')
need(bwsp.get('ShowToolbar') is False and bwsp.get('ShowStatusBar') is False, 'no toolbar, no status bar')
need('600' in str(bwsp.get('WindowBounds')) and '380' in str(bwsp.get('WindowBounds')), 'window bounds %s' % bwsp.get('WindowBounds'))
app_pos = entries.get((app, 'Iloc'))
link_pos = entries.get(('Applications', 'Iloc'))
need(app_pos is not None and link_pos is not None and app_pos[0] < link_pos[0],
     'app at %s left of Applications at %s' % (app_pos, link_pos))

if app_pos and link_pos and backgrounds:
    subprocess.run([art, 'preview', backgrounds[0], os.path.join(mnt, app), str(app_pos[0]), str(app_pos[1]),
                    str(link_pos[0]), str(link_pos[1]), '128', out], check=False)

sys.exit(1 if failed else 0)
