# Retro96

A from-scratch HTML 3.2 / CSS1 / ES3 browser engine that renders the web the way it looked in 1996. Not "mostly." Not "quirks mode close enough." Actually correctly — frames, table layouts, DOM-0 scripting, the whole cursed thing. It includes its own VBScript 1.0 engine and runs Java applets on a from-scratch JVM.

Built for fun. Runs on Windows. Has no chill.

![Retro96 home page](docs/retro96-homepage.png)

> ⚠️ **WARNING: this is a big, real browser engine, not a toy.** The repository currently contains about 84K tracked text lines — a hand-written HTML tokenizer/parser, CSS parser + selector engine + style resolver, block/inline/table layout, ES3 JavaScript and VBScript 1.0 engines, a Java applet interpreter with selected later-runtime compatibility, networking, and a Skia-backed renderer. If you don't already know C# and have never touched how a browser turns HTML into pixels, this is not a good first project to jump into — you'll spend most of your time lost in `Layout/`, `Js/`, `Vbs/` and `Java/` instead of shipping anything. Poke around the code out of curiosity, sure, but come in expecting a real codebase, not a weekend script.

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

`document.formName.fieldName`. `window.status`. The live clock JavaScript that was on every personal homepage in 1999. The Bravenet counter. The guestbook form. Retro96 implements the era's DOM-0 scripting and includes a separate VBScript 1.0 engine for classic `<script language="VBScript">` pages. wild concept

The VBScript engine is a native parser and interpreter, not a VBScript-to-JavaScript translator. It supports classic procedures and functions, Variants, arrays, runtime error handling, and common intrinsic functions. Each document uses a persistent session so procedures and globals can be shared between VBScript blocks, and browser event handlers can call VBScript procedures. Browser-hosted scripts use browser dialogs and named document/form controls; Windows Script Host objects are intentionally not exposed to pages.

---

### Frames that work

not "frames mostly load." frames LOAD. nested iframes load. scripts run inside frames. clicking a link in a frame navigates that frame and not the entire window like some kind of animal. it all works because it was all actually implemented

---

### Plugins

Retro96 has a real plugin system, and it's a separate SDK, not some internal hook I bolted on and called extensible. The [Retro96 Plugin SDK](https://github.com/DirazCoder/Retro96-plugin-sdk) is a standalone C#/.NET 11 project — the browser host doesn't contain the SDK source and doesn't build it as part of the host solution. A plugin is a `.r96p` package: a manifest plus a DLL, nothing else, and it's installed disabled until you approve it.

The sandbox is what you'd expect from something running arbitrary third-party code next to a browser engine: separate worker process, Windows AppContainer isolation, Job Object resource limits, a named-pipe broker, and permission checks at both the worker and broker boundary. No plugin gets a real host filesystem path — file dialogs copy into the plugin's own sandbox on open and stream out on save, so the plugin never sees where things actually live on disk.

What a plugin can touch, gated behind explicit manifest permissions: reading and navigating the browser, adding UI (menu items, toolbar buttons, panels — constrained widgets, not raw WinForms controls handed to a stranger's code), making network requests, sandboxed file storage, key/value storage, clipboard access, sandbox-relative audio playback, OS notifications, and file pickers. The plugin permission reference is maintained in the [Retro96 Plugin SDK README](https://github.com/DirazCoder/Retro96-plugin-sdk#permission-reference), and the host only grants what a plugin actually asked for in `plugin.json` and what you actually approved.

---

### Java applets

Retro96 runs Java applets. It does not have an external JVM. There's no `java.exe` behind the curtain and no JRE to install. `Engine/Java/` is about 7,800 lines of C# that parse `.class` files and execute the bytecode directly, with the `java.*` classes an applet expects written from scratch underneath. Yes, really. I wrote a JVM to make a 1996 scrolling-text banner work

The bytecode/class-file target is Java 1.0/1.1: the parser accepts class-file version 45.0 through 45.3. Alongside that period-correct core, the built-in runtime deliberately includes selected later library/API conveniences needed by some later retro applets (for example `StringBuilder` and selected later exception types). That compatibility does not mean newer class-file formats are accepted: Java 1.2+ `.class` files are rejected with an explicit unsupported-version error. Compiling an applet for this interpreter? Target Java 1.1 bytecode.

Applets load the way they did back then, through any of three tags:

```html
<applet code="MyApplet.class" width="300" height="200">
  <param name="speed" value="5">
</applet>

<embed code="MyApplet.class" width="300" height="200">

<object classid="java:MyApplet.class" width="300" height="200"></object>
```

`CODEBASE` resolves against the page URL. `ARCHIVE` pulls classes out of `.jar`/`.zip` files (comma or space separated). `<param>` values come back through `getParameter()`. Class lookup checks the archive first, then tries fetching `Foo.class` from the codebase.

What works:

- **Bytecode.** The whole JDK 1.1 instruction set: `jsr`/`ret` for `finally`, `tableswitch`/`lookupswitch`, `wide`, `multianewarray`, `invokeinterface`, exception tables, static initializers, and `synchronized` (real monitors underneath). A failing static initializer marks the class as permanently broken, same as a real JVM. `invokedynamic` gets rejected, since it isn't valid in 45.x class files.
- **`java.lang`.** `Object`, `Class`, `String`, `StringBuffer`, `Math`, `System` (with `arraycopy`), the numeric wrappers, `Character`, `Thread`, and the standard exception hierarchy. Threads are real OS threads.
- **`java.util`.** `Vector`, `Stack`, `Hashtable`, `Enumeration`, `Random`, `Date`. `Random` uses the JDK's own linear congruential generator, so seeded sequences are meant to match.
- **`java.awt`.** `Graphics` (drawing, clipping, XOR mode, translation), `Color`, `Font`/`FontMetrics`, `Image` and `MediaTracker`, offscreen buffers, `Polygon`, and the geometry classes. Widgets too: `Button`, `Label`, `TextField`, `TextArea`, `Checkbox`, `Choice`, `List`, `Scrollbar`, laid out by `FlowLayout`, `BorderLayout` and `GridLayout`. They're drawn plainly, rectangles and text with no 3D bevels, so don't expect them to look like Windows 95 buttons.
- **The 1.1 event model.** `MouseListener`, `MouseMotionListener`, `KeyListener`, `ActionListener`, `ItemListener`, `FocusListener`. The 1.0 `Event` constants are still there for older applets.
- **`java.applet`.** The full `init` / `start` / `stop` / `destroy` lifecycle, `AppletStub`, `AppletContext`, `showStatus`, `showDocument`, and `getImage`, which does load real images.

Applets are painted into the page like any other replaced element, and mouse and keyboard input reaches them through the same hit-testing as the rest of the browser.

#### What doesn't work

Some of this is unfinished. Some is on purpose. Better to know up front.

- **No 1.2+ bytecode.** Covered above. Selected later runtime APIs are provided for compatibility, but Java 1.2+ class-file formats, Swing, and the collections framework are not supported.
- **No sound.** `getAudioClip()` hands back an object and `AudioClip.play()` returns without playing anything. The methods exist so applets that call them don't crash. They're just silent.
- **No JavaScript-to-applet scripting.** No LiveConnect. `document.myApplet.someMethod()` from a page script can't reach into the applet. DOM-0 named access covers forms and images, not applets.
- **No applet-to-applet talking.** `AppletContext.getApplet()` returns null and `getApplets()` returns an empty enumeration, so applets on the same page can't find each other.
- **Barely any `java.io` or `java.net`.** `URL`, `PrintStream` and the common exceptions exist. No streams, no sockets. An applet that phones home over a raw socket won't work.
- **`SecurityManager` is a stub.** `checkPermission` does nothing. The actual protection is elsewhere: an applet from a remote page can never touch the local disk, and `file:` resources are only reachable when the page itself was loaded from `file:`.

The Java side has dedicated tests in `tests/RetroTests/JavaEngineTests.cs`, covering class-file parsing, opcode behavior, the `java.*` natives, `Graphics` pixel output, and full click-to-`paint()` input dispatch. Hand-compiled `.class` fixtures live in `tests/html-websites/java/` and `retro96/assets/java-fixtures/`. `tests/html-websites/java/applet-test.html` is the page to load first.

VBScript has a separate focused regression suite in `tests/VbsTests/`. It can be disabled independently in Preferences and is disabled in High trust mode.

---

Chrome won't add `<blink>` back. people have asked. the issues are closed. Retro96 did not ask

---

## This is a full rewrite of my old Rust project. it was bad. genuinely bad

[Retro1996 (Rust)](https://github.com/DirazCoder/Retro1996) — clone the master branch if you want to see for yourself — was my first attempt at this. it had bookmarks. it had downloads. it had a full feature list in the README. it also had a `crash.txt` in the repo that shows it panicking on a bullet point character inside its own welcome page HTML before it even loads anything from the internet

```
PANIC: panicked at src\engine.rs:740:38:
byte index 8712 is not a char boundary; it is inside '•' (bytes 8711..8714) of `<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML 2.0//EN">...
```

that's the welcome page. the one it ships with. it crashed loading itself

here's what actually happened under the hood:

**layout engine** — the Rust version had one function that did everything. every element, regardless of what it was, got stacked vertically with a hardcoded `current_y += height + 10.0`. that's it. that's the layout engine. ten pixels. between everything. always. framesets returned an empty node. inline layout didn't exist as a concept. Retro96 has `InlineLayout.cs`, `TableLayout.cs`, `LayoutEngine.cs` — actual separate layout passes, actual inline text flow, actual table column width resolution

**CSS parser** — the Rust version's parser handled only a small set of properties. Retro96 currently has a 741-line parser, a 1,170-line computed style system, a 774-line style resolver, and a 364-line selector engine. these are different things that do different things

**JavaScript engine** — both projects have a hand-written JS engine. the Rust one is a single large file. Retro96's is split across a lexer, parser, interpreter, runtime, DOM bindings, AST types, and scope — currently 7,127 lines total, each piece doing one job. the Rust DOM bindings had `getElementById`, `createElement`, `write`, `writeln`, `window.status`, `window.location`. that's roughly it. Retro96's `DomBindings.cs` is currently 1,977 lines on its own

**testing** — the Rust repo has a `test_js_engine.rs` file with zero `#[test]` functions in it. Retro96 currently has 226 xUnit facts and 6 theory cases in `RetroTests`, plus 23 focused VBScript xUnit tests; the live JS page harness contains 29 contract assertions. There are 54 hand-authored QA HTML files, a layout lab, and a Playwright visual-diff harness. i tested Retro96 with my eyes AND with actual tests. the Rust one i tested with hope

**real websites** — Retro96 renders theoldnet.com. it renders spacejam.com/1996/. it renders period Geocities pages. the Rust version rendered 0% of 1996 websites correctly — the layout was broken enough that nothing looked right, and the JS engine was broken enough that nothing ran. the bookmarks and downloads worked great though. the thing they were supposed to navigate to did not render

here's spacejam.com/1996/ in Retro96, not photoshopped, not a mockup, just the browser loading the page:

![spacejam.com/1996/ running in Retro96](docs/spacejam-proof.png)

starfield background. planet nav icons. the logo. footer text. all of it.

here's what the Rust version looked like when I fixed it enough to launch and then searched frogfind.com:

![Retro1996 launched](docs/rust-launched.png)

it launched. "Welcome page not found." it showed me a fallback page. and then I typed frogfind.com into the address bar

![Retro1996 Not Responding](docs/rust-not-responding.png)

"Not Responding." it froze. on frogfind.com. a website that exists specifically to serve simple HTML to old browsers. the Rust browser looked at the simplest possible website on the internet and said no

and here's frogfind.com in Retro96, loaded instantly, no drama:

![frogfind.com in Retro96](docs/retro96-frogfind.png)

the Rust project was much smaller. Retro96 currently has about 84K tracked text lines. one of them works

## Status

Side project built for fun, not production software. The test sources currently define 255 xUnit cases across the main and focused VBScript suites (226 facts, 6 theory cases, and 23 VBScript facts), plus 29 live JavaScript page-contract assertions. The repository includes 54 hand-authored QA HTML files, a layout lab, and a Playwright pixel-diff harness for the selected pages in `tests/html-websites/` and `testdata/`.

## Rendering stack

The whole engine draws through SkiaSharp. `Engine/Drawing/` is a `System.Drawing`-shaped wrapper over `SKCanvas`/`SKBitmap`/`SKFont` — nothing links `System.Drawing.Common` or `libgdiplus`, so the engine builds and tests identically on Linux and Windows. The WinForms shell composites each frame onto the Skia surface and hands it to Windows through a single GDI blit — the one unavoidable GDI call in the whole codebase.

## Project layout

```
retro96/             the engine + WinForms shell (net11.0-windows)
  Engine/
    Css/              CSS1 parser, selectors, style resolution
    Dom/              DOM node tree
    Drawing/          SkiaSharp-backed drawing primitives
    Forms/            form submission, hit-testing
    Html/             HTML tokenizer + parser
    Java/             Java 1.0/1.1 bytecode interpreter + selected later runtime APIs, AWT
    Js/               hand-written ES3 lexer/parser/interpreter + DOM bindings
    Vbs/              native VBScript 1.0 lexer/parser/interpreter + runtime
    Layout/           block/inline/table layout engine
    Network/          HTTP client, cookies, URL parsing, frame loading
    Plugins/          plugin host, .r96p loading, sandbox worker + broker protocol
    Render/           renderer, font/image caches, glyph substitution
tests/
  RetroTests/         xUnit regression suite (engine, JS, DOM, forms, frames, CSS1)
  VbsTests/           focused VBScript 1.0 semantic regression suite
  JsPageTests/        live script-contract checks against real pages
  LayoutLab/          layout laboratory + PageProbe (renders a page, dumps diagnostics)
  VisualDiff/         Retro96-vs-Chromium pixel/geometry diff harness (Playwright)
  html-websites/      hand-authored QA pages (HTML 3.2 + CSS1 + ES3)
testdata/             real-world period pages + generated era assets
docs/                 screenshots used in this README
scripts/              helper scripts (visual_analysis.py)
Retro96.sln           solution file
```

## Building

Requires the .NET 8/11 SDKs. Engine and tests run on Linux; the WinForms shell needs `EnableWindowsTargeting` to cross-compile from Linux, or just build it natively on Windows.

```
cd retro96
dotnet build -p:EnableWindowsTargeting=true
```

## Running the tests

```
cd tests/RetroTests  && dotnet test      # engine/JS/DOM/forms/frames + CSS1 regressions
cd tests/VbsTests    && dotnet test      # VBScript 1.0 semantics
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
