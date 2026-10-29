# dmgbuild settings for DiskSpaceAnalyzer-macOS.dmg (used by macos/build.sh). dmgbuild writes the Finder window
# layout (.DS_Store) itself, so no Finder automation (AppleScript) is needed. Defines (-D):
#   app         path of "Disk Space Analyzer.app"
#   background  window background (600 x 380 points; a 1x + 2x TIFF), made by dmg_art.swift
#   icon        volume icon (.icns)
import os.path

app = defines['app']  # noqa: F821 (defines is provided by dmgbuild)
background = defines['background']  # noqa: F821
icon = defines.get('icon')  # noqa: F821

format = 'UDZO'
filesystem = 'HFS+'
files = [app]
symlinks = {'Applications': '/Applications'}

# Icon view, no toolbar, status bar, path bar or sidebar; the window is as large as the background.
default_view = 'icon-view'
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
window_rect = ((200, 160), (600, 380))
show_icon_preview = False
arrange_by = None
label_pos = 'bottom'
text_size = 13
icon_size = 128
# Icon centres from the top left of the window; dmg_art.swift draws the arrow between them.
icon_locations = {
    os.path.basename(app): (150, 190),
    'Applications': (450, 190),
}
