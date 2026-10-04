Retro96 → 1999 QA Suite
=======================

The 1996-era hand-authored QA pages were rewritten as the 1999 upgrade
battery: one page per section of the upgrade checklist (HTML 4.01,
CSS2, DOM Level 1, IE5 + NS4.7 personas, ES3, VBScript 5.0, HTTP/1.1),
plus the checklist §17 test-plan specials: a deliberately broken HTML
recovery page, the Acid1 CSS1 box-model smoke test, a Y2K date/cookie
battery, and the frames/media pages the plan calls for.

Pages
-----
html/html401-full.html        HTML 4.01 element battery (new elements, deprecated set, entities, concealment, ruby)
html/broken-html-1999.html    deliberately malformed tag-soup recovery page
forms/forms-1999.html         label/fieldset/legend/button/optgroup, disabled/readonly, file+image, textarea wrap
tables/tables-1999.html       row groups, frame/rules, IE border colors, CSS2 collapse/fixed/spacing tables
frames/frames-1999.html       frameset battery (+ frames-nav99/banner99/main99/frame-child99 children)
css/css2-selectors.html       child/adjacent/attribute selectors, :first-child, :lang, :before/:after, inherit, @media
css/css2-boxmodel.html        positioning, z-index, min/max, overflow/clip, outline, IE5 box-model persona check
dom/dom-level1.html           DOM Level 1 tree surgery + attributes map
dom/dom-ie5-dhtml.html        document.all, innerHTML family, offset geometry, currentStyle, pixelLeft
dom/dom-ns4-layers.html       Navigator 4.7 layer DOM (load with the NS4.7 profile)
events/events-1999.html       IE5 bubbling + attachEvent, NS4 capture model, 4.01 intrinsic events
javascript/es3-1999.html      ES3/JS1.3/JScript5 language battery + version gating + host objects
vbscript/vbscript5.html       VBScript 5.0: Class/With/Eval/Execute/RegExp/GetRef
ie/ie5-extras.html            marquee, bgsound, comment, ruby, body extras, static filters, xml islands
netscape/ns4-extras.html      layer/ilayer/nolayer, multicol, spacer, JSSS, keygen
images/images-media.html      PNG/GIF/JPEG, lowsrc, image maps, audio embeds
network/http11-page.html      served by the loopback server: keep-alive, chunked, 304 validation
acid1.html                    Acid1 CSS1 box-model smoke test (1998)
y2k.html                      Y2K: getYear/getFullYear, cookie expiry past 2000
test-index.html               index linking every page

Assets
------
images/pixel-dot.gif          1x1 transparent GIF
images/dot16.png              16x16 PNG (alpha-capable)
images/tada.wav               generated 0.3s chord (bgsound fixture)
images/theme.mid              generated 3-note MIDI (embed audio fixture)
css/bullet.gif, css/bg-tile.gif, css/import-test.css  kept from the 1996 suite (referenced by CSS pages)

Kept from the 1996 suite
------------------------
javascript/javascript-basic.html   required by tests/RetroTests/JsEngineTests.cs
java/                              applet .class fixtures required by JavaEngineTests.cs
                                    (Java is parked at 1996 compatibility per the checklist)

Where the assertions live
-------------------------
tests/RetroTests/Retro99SuiteTests.cs runs the same pages headlessly:
parse → style → layout for every page, script contracts for the script
pages, persona switches (IE5 default / NS4.7 / union) where relevant.
The VisualDiff harness still scans this folder recursively and preserves
relative URL paths when serving.

The 1996 regression corpus (the-old-net style real pages) stays in
testdata/ untouched.
