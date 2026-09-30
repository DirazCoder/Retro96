Retro96 HTML Website QA Suite
==============================

The fixtures are organised by feature rather than one flat directory.
The small pages stay focused and are kept as separate regression cases.
The FULL pages are the broad integration batteries, modelled on the existing
css/css-full-fixed.html approach rather than appending a generic table to every
individual page.

Folders
-------
html/       HTML 3.2 document structure, text, links, lists, entities, legacy markup
css/        CSS1 property/selectors/layout battery + companion assets
forms/      form controls, state and interaction
images/     image loading, alt/fallback, sizing and tables
javascript/ ES3-era JavaScript / DOM-0 / timers / navigation / mouse
frames/     framesets and frame-document interaction
java/       JDK 1.0/1.1 applet pages and .class fixtures
tables/     table layout, links, malformed and edge-case tables
sites/      realistic 1996-style multi-page/frame sites
server/     CGI/Python helper fixtures

Full integration fixtures
--------------------------
- html/html-full-fixed.html     broad valid HTML 3.2 + legacy element battery
- html/html-full-fixed.html     broad valid HTML 3.2 + legacy element battery
- html/broken-html-full.html    dedicated malformed/tag-soup recovery battery
- css/css-full-fixed.html       broad CSS1 battery
- forms/forms-full-fixed.html   broad form-control/state/interaction battery
- images/images-full-fixed.html broad image loading/fallback/sizing/alignment battery
- javascript/javascript-full-fixed.html broad JavaScript/DOM/event/timer/navigation battery
- tables/tables-full-fixed.html broad table layout/spans/nesting/recovery battery
- frames/frames-full-fixed.html broad frameset/named-frame/no-frames battery
- java/java-full-fixed.html     broad classic APPLET/PARAM/class-fixture battery
- server/server-full-fixed.html broad local CGI/request/error battery
- sites/sites-full-fixed.html   broad 1996-style site/layout/form/link battery

Focused fixtures are intentionally not padded with repeated "coverage matrix"
content. They remain useful for bisecting a single regression.

The VisualDiff harness scans html-websites recursively and preserves the
relative URL path of each page when serving it, so links between organised
folders continue to behave like real browser URLs.
