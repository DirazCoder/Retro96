Retro96 Expanded html-websites Test Pack
========================================

These are additional manual regression pages for the existing
Retro96/tests/html-websites suite. They are intentionally written in
Netscape/IE-era HTML and JS style rather than modern DOM APIs.

Coverage:
- dom-properties-events.html
  Live wrapper reads/writes, get/setAttribute, property-assigned onclick,
  innerHTML replacement, window.status hover.
- forms-controls-extended.html
  Text/password/file/textarea/select/radio/checkbox/submit/reset/button,
  focus/blur/change/submit/reset.
- frames-interaction.html + frame-child.html
  Nested frame load, hover/click, DOM mutation, textarea in a frame,
  fragment navigation.
- css-layout-edge.html
  Margins/padding/borders/backgrounds, floats, clear, inline runs.
- table-edge-cases.html
  Stray text, whitespace, nested tables, rowspan/colspan, empty cells.
- legacy-html-96.html
  BLINK, SPACER, FONT, CENTER, HR, IMG and anchor states.
- selection-mouse.html
  Drag/double/triple/Ctrl+A style manual selection coverage in normal flow,
  tables and form controls.
- navigation-history-query.html
  Query strings, fragments and failed local navigation/back behavior.
- images-alt.html
  Loaded/broken images, alt text, dimensions and borders.
- whitespace-inline.html
  Ordinary whitespace, NBSP runs, adjacent inline tags and wrapping.
- javascript-timers.html
  Multiple timers, timer exception, later timer and post-parse document.write.

The files are independent of the engine and can be copied directly into
Retro96/tests/html-websites/.
