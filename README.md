# Retro96

A from-scratch HTML 3.2 / CSS1 / ES3 browser engine that renders the web the way it looked in 1996. Not "mostly." Not "quirks mode close enough." Actually correctly — frames, table layouts, DOM-0 scripting, the whole cursed thing.

Built for fun. Runs on Windows. Has no chill.

## Why does this exist

Because Chrome opened a 1997 Geocities page and said "yeah this looks fine" and it did NOT look fine. The `<blink>` tags weren't blinking. The frames were broken. The Bravenet hit counter JavaScript was dead. Retro96 does not fix things. Retro96 renders things.

## Why use this instead of your "very secure" modern browser

### `<blink>` SUPPORT!!!

ok so you know how every modern browser just. REMOVED `<blink>`. gone. deleted. "accessibility concerns" they said. "we don't want people having seizures" they said. ok fair point actually BUT STILL. Chrome filed it as won't fix. Firefox caved in 2015. every single browser just quietly pretended it never existed and moved on with their lives

I made support for it

not just the tag either. `text-decoration: blink` in CSS? works. `String.prototype.blink()` in JavaScript? works. THREE SEPERATE PLACES TO MAKE YOU SEIZURE THREE TIMES IN A ROW hahaha to make things blink at you. THREE separate implementations of the same feature that the entire browser industry decided was too unhinged to ship. a page could hit you with the tag, then the CSS, then the JS one after another. back to back to back. the browser vendors didn't want that. I wanted that. you're welcome

> ⚠️ **DISCLAIMER: this is a joke and the blink thing is genuinely just for fun and historical accuracy. but for real — if you are photosensitive or prone to seizures please be careful, THREE separate blink mechanisms is not a small amount of blink. we think it's funny. we also think you should look after yourself. those two things can both be true. and if something happens to you, don't call us for medical help**

---

### Authentic period layout

Modern browsers in quirks mode still "helpfully" fix things they shouldn't touch. Retro96 doesn't:

- `align=middle` and `valign=center` — HTML 3.0 alignment synonyms that Chrome just ignores
- table layouts where percentage widths resolve against the *actual containing block*, not vibes
- `margin: auto` centering that works like it did in 1996
- per-side border shorthands that didn't get silently dropped
- `<font>` tags with authored colors that are actually honored instead of overridden by the browser going "actually no"

---

### DOM-0 scripting that isn't dead

`document.formName.fieldName`. `window.status`. The live clock JavaScript that was on every personal homepage in 1999. The Bravenet counter. The guestbook form. Chrome renders these pages and the scripts just don't run. The clock is frozen. The counter shows `sw=undefined`. Retro96 actually implements the DOM the way it worked then so the scripts actually run. wild concept

---

### Frames that work

not "frames mostly load." frames LOAD. nested iframes load. scripts run inside frames. clicking a link in a frame navigates that frame and not the entire window like some kind of animal. it all works because it was all actually implemented

---

Chrome won't add `<blink>` back. people have asked. the issues are closed. Retro96 did not ask

---

## Status

Side project built for fun, not production software. The engine has a full regression suite (100 xUnit facts, 29 live JS contract checks, a layout lab, and a pixel-diff harness that runs 37 real pages against Chromium) — see `tests/reports/bug-report.md` for the full QA writeup including root causes for every tracked bug.

## Rendering stack

The whole engine draws through SkiaSharp. `Engine/Drawing/` is a `System.Drawing`-shaped wrapper over `SKCanvas`/`SKBitmap`/`SKFont` — nothing links `System.Drawing.Common` or `libgdiplus`, so the engine builds and tests identically on Linux and Windows. The WinForms shell composites each frame onto the Skia surface and hands it to Windows through a single GDI blit — the one unavoidable GDI call in the whole codebase.

## Project layout

```
Retro96-fixed/       the engine + WinForms shell (net11.0-windows)
  Engine/
    Css/              CSS1 parser, selectors, style resolution
    Dom/              DOM node tree
    Drawing/          SkiaSharp-backed drawing primitives
    Forms/            form submission, hit-testing
    Html/             HTML tokenizer + parser
    Js/               hand-written ES3 lexer/parser/interpreter + DOM bindings
    Layout/           block/inline/table layout engine
    Network/          HTTP client, cookies, URL parsing, frame loading
    Render/           renderer, font/image caches, glyph substitution
tests/
  RetroTests/         xUnit regression suite (engine, JS, DOM, forms, frames, CSS1)
  JsPageTests/        live script-contract checks against real pages
  LayoutLab/          layout laboratory + PageProbe (renders a page, dumps diagnostics)
  VisualDiff/         Retro96-vs-Chromium pixel/geometry diff harness (Playwright)
  html-websites/      hand-authored QA pages (HTML 3.2 + CSS1 + ES3)
  reports/            the QA campaign writeup (bug-report.md)
testdata/             real-world period pages + generated era assets
```

## Building

Requires the .NET 8/11 SDKs. Engine and tests run on Linux; the WinForms shell needs `EnableWindowsTargeting` to cross-compile from Linux, or just build it natively on Windows.

```
cd Retro96-fixed
dotnet build -p:EnableWindowsTargeting=true
```

## Running the tests

```
cd tests/RetroTests  && dotnet test      # engine/JS/DOM/forms/frames + CSS1 regressions
cd tests/JsPageTests && dotnet run       # live script-contract checks
cd tests/LayoutLab   && dotnet run       # layout lab, all checks
```

Inspect a single page in detail:

```
cd tests/LayoutLab && dotnet run -- ../../testdata/voyagersisland.html 800 600 png
```

Full visual diff against Chromium (needs `playwright install` once):

```
cd tests/VisualDiff && dotnet run
```

## License

MIT