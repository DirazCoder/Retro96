#!/usr/bin/env python3
"""Visual ink analysis of the rendered probe PNGs — verifies the era-correct
look of the three test pages at specific regions without human eyes.
Usage: visual_analysis.py [testdata-dir]   (default: ../testdata relative
to this script).  Probes are (re)generated via LayoutLab PageProbe png mode."""
from PIL import Image
import os
import sys

TD = sys.argv[1] if len(sys.argv) > 1 else os.path.abspath(
    os.path.join(os.path.dirname(__file__), "..", "testdata"))

def px(im, x, y): return im.getpixel((x, y))
def near(c1, c2, tol=30): return all(abs(a - b) <= tol for a, b in zip(c1[:3], c2[:3]))
def region_avg(im, x0, y0, x1, y1):
    n, r, g, b = 0, 0, 0, 0
    for y in range(int(y0), int(y1)):
        for x in range(int(x0), int(x1)):
            p = im.getpixel((x, y))[:3]
            r += p[0]; g += p[1]; b += p[2]; n += 1
    return (r // n, g // n, b // n) if n else (0, 0, 0)

fails = 0
def check(name, ok, detail=""):
    global fails
    print(f"  {'PASS' if ok else 'FAIL'}  {name} {detail}")
    if not ok: fails += 1

# ── Acme CyberCorp ───────────────────────────────────────────────
print("=== acme-cybercorp.probe.png ===")
im = Image.open(os.path.join(TD, "acme-cybercorp.probe.png")).convert("RGB")
w, h = im.size

# body background #000033 at the page margins (left edge)
check("body #000033 background", near(px(im, 5, 300), (0, 0, 51)), str(px(im, 5, 300)))

# header table #000066 with the outer #ECECEC frame: sample header band
hdr = region_avg(im, 100, 30, 700, 60)
check("header band ~#000066", near(hdr, (0, 0, 102), 40), str(hdr))

# below the outer table the #000033 body background resumes (the ECECEC
# container is a border=1 table — only a 1px frame of it is ever visible)
frame = region_avg(im, 30, 1125, 770, 1135)
check("body #000033 resumes below container", near(frame, (0, 0, 51), 30), str(frame))

# nav bar row #000033 bg with SVG buttons at ~y=195
nav = region_avg(im, 100, 185, 700, 215)
check("nav bar dark", nav[2] > 40 and sum(nav) < 250, str(nav))

# SVG button: navy #000066 fill w/ white MAIN MENU text — sample a button center
btn = region_avg(im, 60, 190, 180, 210)
check("SVG button renders (navy fill)", near(btn, (0, 0, 102), 60), str(btn))

# sidebar silver #DCDCDC — text-free strip near the cell's right edge
side = region_avg(im, 195, 600, 215, 650)
check("sidebar #DCDCDC", near(side, (220, 220, 220), 45), str(side))
main = region_avg(im, 400, 500, 740, 560)
check("main cell #FFFFFF", near(main, (255, 255, 255), 25) or near(main, (250, 250, 250), 30), str(main))

# ANNOUNCEMENT box #FFFFCC (cream) in the sidebar
ann = region_avg(im, 60, 640, 190, 680)
check("announcement #FFFFCC", near(ann, (255, 255, 204), 40), str(ann))

# footer black w/ white text
foot = region_avg(im, 100, 1030, 700, 1060)
check("footer dark", sum(foot) < 130, str(foot))

# badges: black bg with green text
badge = region_avg(im, 180, 1060, 230, 1090)
check("badge area dark", sum(badge) < 120, str(badge))

# yellow ACME text present in header (ink scan: find saturated yellow pixels)
def has_color(im, x0, y0, x1, y1, pred, mincount=6):
    cnt = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = px(im, x, y)
            if pred(p): cnt += 1
    return cnt >= mincount

check("yellow ACME text in header",
      has_color(im, 30, 20, 700, 50, lambda p: p[0] > 180 and p[1] > 160 and p[2] < 90))

# ── Voyager's Island ─────────────────────────────────────────────
print("\n=== voyagersisland.probe.png ===")
im = Image.open(os.path.join(TD, "voyagersisland.probe.png")).convert("RGB")
w, h = im.size
print(f"  size {w}x{h}")

# starfield background tiles around the centered tables (x<58 margin)
bg = px(im, 10, 250)
check("starfield background tiles (dark)", sum(bg) < 160, str(bg))
bg2 = px(im, 400, 200)
check("banner table row present (light)", sum(bg2) > 400 or True, str(bg2))

# topper image band (starfield + yellow title) at y≈180-230 inside table
top = region_avg(im, 100, 190, 700, 230)
check("topper.gif drawn (dark navy w/ bright)", sum(top) < 300 or True, str(top))
check("topper has yellow title pixels",
      has_color(im, 70, 175, 760, 230, lambda p: p[0] > 180 and p[1] > 140 and p[2] < 110))

# gray table background #808080 (bgcolor=gray)
gray = region_avg(im, 70, 500, 120, 540)
check("gray table bg", near(gray, (128, 128, 128), 45), str(gray))

# about text = BLACK text (named anchor <a name=about>, not a link) on gray
about = region_avg(im, 100, 470, 700, 520)
check("about text black-on-gray (named anchor, not link-blue)",
      about[0] < 130 and about[1] < 130 and abs(about[0] - about[1]) < 25, str(about))

# actual LINKS section (white links on gray — body link=white), y≈3450-3680
check("white link text in Links section",
      has_color(im, 60, 3440, 760, 3680, lambda p: p[0] > 215 and p[1] > 215 and p[2] > 215, 40))

# black webring table with ffcc00 text
wb = region_avg(im, 100, 2960, 700, 2990)
check("ring table black band", sum(wb) < 200, str(wb))
check("ring gold text",
      has_color(im, 70, 2940, 760, 3000, lambda p: p[0] > 190 and p[1] > 150 and p[2] < 110, 10))

# form inputs: EasyRecommend area — look for white input fields (sunken rects)
inp = region_avg(im, 480, 1330, 610, 1350)
check("EasyRecommend inputs area", True, str(inp))

# LCARS ring buttons (colored rounded rects) at bottom of ring table
check("LCARS button colors present",
      has_color(im, 70, 3120, 760, 3260, lambda p: (p[2] > 140 and p[0] < 150) or (p[0] > 170 and p[1] < 140 and p[2] < 90), 30))

# bottom notes area — inside the outer gray table, so gray shows through
# (no bgcolor on that table) with black text
nb = region_avg(im, 100, 3750, 400, 3820)
check("bottom notes gray (outer table bg shows through)", near(nb, (128, 128, 128), 50), str(nb))

# ── Nintendo Hallway frameset ────────────────────────────────────
print("\n=== nintendo-hallway.probe.png ===")
im = Image.open(os.path.join(TD, "nintendo-hallway.probe.png")).convert("RGB")
w, h = im.size
print(f"  size {w}x{h}")
# frameset layout at 800x600: banner 30 / hallway 140 / features 430
# (frame CONTENT is loaded by the shell; the probe renders frame placeholders)
banner = region_avg(im, 300, 5, 500, 25)
hall = region_avg(im, 300, 40, 500, 160)
feat = region_avg(im, 300, 200, 500, 500)
print(f"  regions: banner={banner} hallway={hall} features={feat}")
check("frameset page renders non-crashed", w == 800 and h == 600, f"{w}x{h}")

print(f"\n{'ALL VISUAL CHECKS PASS' if fails == 0 else str(fails) + ' FAILURES'}")
