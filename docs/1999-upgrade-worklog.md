# Retro96 → Retro99 Upgrade Worklog

Shared multi-agent work log. Append-only; new sections start with `---`.

Project: upgrade the Retro96 browser engine (1996: HTML 3.2 / CSS1 / ES3 / VBScript 1.0)
to a 1999 engine per /home/z/my-project/upload/retro99-upgrade-checklist.md
(HTML 4.01, CSS2, DOM Level 1, IE5 + NS4.7 personas, VBScript 5.0, HTTP/1.1).
Then rewrite tests/html-websites as the 1999 QA suite, test everything, zip.

Environment: Linux, .NET 11 RC1 SDK at /home/z/.dotnet (version 11.0.100-rc.1.26425.128).
Repo: /home/z/my-project/retro96

---
Task ID: 1
Agent: main
Task: Setup, .NET 11 RC install, baseline build/test

Work Log:
- Read full 491-line upgrade checklist (saved at /home/z/my-project/upload/retro99-upgrade-checklist.md)
- Cloned https://github.com/dirazcoder/retro96 to /home/z/my-project/retro96
- Installed .NET 11 RC1 SDK (11.0.100-rc.1.26425.128) to /home/z/.dotnet
- Fixed Retro96.sln: removed dangling references to Retro96.SamplePlugin and
  Retro96.PluginWorker (separate plugin SDK repo, not in this checkout)
- retro96/Retro96.csproj: PublishAot defaulted to false + win-x64 RID default when
  AOT enabled (NETSDK1082 fix: AOT implied linux-x64 RID, no WinForms runtime pack)
- Fixed cross-platform bug in Engine/Network/FrameLoader.cs FileUrls:
  - LocalPathFromFileUrl now normalizes leading-slash soup (file:////x etc.),
    maps drive/UNC/POSIX correctly per OS (was producing bogus \\tmp\... UNC paths
    on Linux, breaking every relative link on locally-opened POSIX pages)
  - CanonicalFileUrl maps POSIX "/C:/..." drive paths back to drive URL form
  - TryResolveAddressBarInput accepts absolute POSIX paths on non-Windows hosts
- tests/RetroTests: FileUrlResolution now platform-aware; Bug_IframeNotRendering
  builds canonical file URLs via FileUrls.CanonicalFileUrl

Stage Summary:
- Solution builds on Linux + .NET 11 RC1
- Baseline GREEN: RetroTests 238/238, VbsTests 23/23 (on Linux)
- Next: parallel code mapping (HTML/CSS/JS+DOM/VBS/Net+Layout) then engine upgrades

---
Task ID: 3-b
Agent: explore (CSS layer)
Task: Map CSS engine internals for CSS1→CSS2 upgrade (READ-ONLY research, no code changes)

Files studied: Engine/Css/{CssParser.cs (836L), CssSelector.cs (428L), StyleResolver.cs (994L), ComputedStyle.cs (1293L)}, tests/RetroTests/CssParserTests.cs (449L), plus Form1.cs (import pipeline), LayoutEngine.cs / Renderer.cs / HitTester.cs (consumption points).

1. SELECTORS
Supported (CssParser.ParseSelectorPart cs:270-377; matching right-to-left in CssSelector.cs):
- Type, .class, #id; universal * (PartType.Universal)
- Combinators: descendant (space), child '>', adjacent '+' AND general sibling '~'
  (~ and ^= $= *= are actually CSS3-era features already present — ahead of 1999 spec)
- Attributes: [attr], [attr=v] (quoted/unquoted) plus operators =, ~=, |=, ^=, $=, *=
  (MatchesAttribute, CssSelector.cs:295-336)
- Pseudo-classes: :link, :visited, :hover, :active, :focus ONLY
  (CssSelector.cs:354-376; dynamic states walk hovered target's ancestors; :visited
  resolves href against doc.VisitedUrls)
- Pseudo-elements: :first-line, :first-letter only (CSS1 single-colon + ::double-colon
  both accepted, CssParser.cs:308-333). StyleResolver.ApplyPseudoStyle (cs:380-418)
  clones style into FirstLineStyle/FirstLetterStyle.
Specificity: (b,c,d) = ids / (classes+attrs+pseudo-classes) / (types+pseudo-elements);
universal & combinators count 0 (CssSelector.cs:32-51).
MISSING for CSS2: :first-child, :lang(), :before/:after + content property (no 'content'
case in ComputedStyle.Apply at all; single-colon :before parses as PseudoClass which
never matches; ::before parses as PseudoElement but StyleResolver only routes
first-line/first-letter), :root/:nth-* (out of era anyway).

2. PARSER
- Comments: RemoveComments (cs:90-126), quote-aware; also applied to inline STYLE=
  (ParseInlineStyle cs:75-84).
- @media: handled (HandleAtRule cs:167-203). Applies block if any comma-separated
  part's first token is "screen" or "all"; leading "not" negates; nested {} balanced,
  inner CSS recursively parsed. print/projection rules DROPPED. No real media queries.
- @import: URL parsed (url()/quoted, cs:154-161) but the media descriptor after the
  URL is DISCARDED (SkipToSemicolonOrBrace) — media-dependent imports NOT honored;
  imports always apply. Expansion happens in Form1.ExpandCssImportsAsync
  (Form1.cs:1611-1649): depth ≤ 8, ≤ 32 imports/sheet, visited-set loop guard,
  imported text PREPENDED to parent sheet (order-based cascade). <link rel=stylesheet>
  fetched (Form1.cs:1567-1608) and injected as <style> APPENDED to document (so link
  sheets come after inline <style> in source order).
- !important: parsed in declaration loop (cs:476-487); cascade tiering in StyleResolver
  (see 5). Tests lock the behavior.
- @charset skipped (cs:163-165); unknown @rules (@font-face/@page…) skipped whole-block
  (SkipToMatchingBrace cs:811-836).
- Shorthands expanded AT PARSE TIME (ExpandShorthand cs:503-603): margin/padding box
  expansion, border + per-side border (width/style/color any order), font (incl.
  "12px/1.5" glued form), list-style. ComputedStyle ALSO has Parse*Shorthand handlers
  (both paths maintained — belt and braces).
- Error recovery: stray-block skip (cs:44-61); unknown values ignored per-property.

3. PROPERTIES (ComputedStyle.Apply switch cs:190-293)
Supported: font-family/size/weight/style/variant, font; color, text-decoration,
text-align, text-indent, line-height, letter-spacing, word-spacing, text-transform,
white-space, vertical-align; background-color/image/repeat/attachment/position,
background; margin(-top/right/bottom/left), padding(-…), border-{side}-{width,style,color},
border-{top,right,bottom,left}, border-width/style/color, border; display, visibility,
overflow, position, top/right/bottom/left, float, clear, z-index;
list-style-type/image/position (list-style only via parser expansion); width, height.
Checklist specifics:
- position static/relative/absolute/fixed: YES (cs:1147-1156)
- top/right/bottom/left: YES (ParseOffset cs:797-811; % deferred to layout)
- z-index: YES (int only; 'auto'/invalid → 0, cs:1179-1182)
- min/max-width/height: NO
- overflow: YES (visible/hidden/scroll/auto — enforced only as paint+hit-test CLIP,
  Renderer.cs:741+, HitTester.cs:25+; no scrollbars)
- clip: NO. cursor: NO. outline*: NO. border-collapse: NO. border-spacing: NO.
  table-layout: NO. caption-side: NO. font-size-adjust: NO. text-shadow: NO.
  layer-background-color/image (NS4): NO. content: NO.
- vertical-align: baseline/top/middle/bottom/text-top/text-bottom/super/sub keywords
  + % (kept signed); <length> values NOT parsed → fall to Baseline (SetVerticalAlign
  cs:680-693)
- display: block/inline/inline-block/none/list-item/table/table-row/table-cell/
  table-caption/table-row-group/table-column-group/table-column/table-header-group/
  table-footer-group (cs:1104-1124). MISSING: inline-table, run-in, compact.
- 'inherit' keyword: NOT implemented anywhere. Accidentally works for color and
  font-size (fallbacks keep current value); destructive for margins/padding/border
  widths (ParseLength → 0).

4. COMPUTEDSTYLE (box model + positioning storage)
- Width/Height float? (null=auto) + WidthPercent/HeightPercent (resolved at LAYOUT vs
  containing block — never baked at parse)
- Margin{Top,Right,Bottom,Left} + per-side nullable % + MarginLeftAuto/MarginRightAuto
- Padding 4 sides + per-side %; Border per-side width/style/color
- Positioning fields ALREADY EXIST (cs:107-112): Position, Top/Right/Bottom/Left
  (float?) + TopPercent/RightPercent/BottomPercent/LeftPercent (cs:90-93), ZIndex int,
  Float, Clear, Visibility, Overflow. So z-index/absolute storage is done; layout
  consumes: LayoutEngine.cs:708 (IsAbsolutelyPositioned), 1663-1669 (relative pass),
  FindPositionedContainingBlock cs:1887-1902 + LayoutAbsolute cs:1904-1929 (%
  resolved against containing block W for left/right, H for top/bottom); paint order
  sorted by ZIndex in Renderer.cs:661-678. LayoutBox duplicates box model (cs:690-714)
  + ResolveBoxPercentages (cs:716-727).
- Own* authored-flags (OwnColor, OwnBackground, OwnFontSize, OwnBorder*Style, …) drive
  renderer/hit decisions; FirstLineStyle/FirstLetterStyle pseudo slots exist.

5. STYLERESOLVER (cascade)
Entry: Resolve(doc, viewportWidth=800) (cs:146-182) — gathers rules from every <style>
(incl. injected link sheets), builds AuthorRuleIndex (cs:184-246: perf-only bucketing
by rightmost subject Type; no-type selectors go to every bucket), then recursive
ResolveNode (cs:248-393).
Per element, in order:
  a. ComputedStyle.Inherit(parent) — inherited: fonts, color, text-align, text-indent,
     line-height, letter/word-spacing, text-transform, white-space, visibility,
     list-style-*
  b. UA defaults — the "UA stylesheet" is a C# switch, ApplyUaDefaults (cs:424-723),
     NN3-era defaults (heading scale, ul 40px padding-left, table cell padding, a[href]
     color+underline, noscript hidden when scripting, …)
  c. Author rules matching element: declarations split normal/important, applied
     OrderBy(Specificity).ThenBy(source order) — ascending, so higher spec / later wins
     (cs:276-307). Pseudo decls for first-line/first-letter routed separately.
  d. inline STYLE= (CssParser.ParseInlineStyle) — non-important applied now (beats all
     author normal); its !important queued with fake spec (1,000,000,0,0)+int.MaxValue
     (cs:309-320)
  e. !important tier applied last (cs:322-327): author !important beats inline normal;
     inline !important beats author !important
  f. BASEFONT-driven font size clamp; g. ApplyHtmlAttributes — HTML presentational
     attrs applied LAST (override CSS — documented 1996 "closest to content" choice):
     BODY bgcolor/text/background/margins, FONT color/face/size, TD/TH bgcolor(+row
     fallback)/align/valign/nowrap, TR/TABLE bgcolor, HR color/noshade, DIV/P/H*/caption
     align, UL/OL/LI type, dd indent (cs:751-903); applied BEFORE recursion so
     inherited attrs (body text=, font color=) reach descendants
  h. border-style none→width 0, list-gutter suppression for list-style:none,
     ResolvePendingLineHeight, ApplyPseudoStyle(first-line/first-letter)
- style="" merge = step d. width/height/hspace/vspace/border/img-align→float attrs are
  handled at LAYOUT time instead (LayoutEngine.ApplyHtmlPresentationalAttrs
  cs:735-814) — NOT in StyleResolver.
- No user stylesheet tier; no media-scoped author sheets beyond @media parse-time.

6. VALUES
- Colors (ParseColor cs:477-529): transparent, #RGB, #RRGGBB, rgb(int,int,int) and
  rgb(%,%,%) (clamped), named via ColorTranslator.FromHtml (lowercased). HTML attrs
  additionally accept bare 3/6-digit hex without '#' (StyleResolver.ParseHtmlColor
  cs:111-136). Missing: #RRGGBBAA, hsl(), system colors.
- Lengths (ParseLength cs:699-772): px, pt(×4/3), pc(×16), in(×96), cm, mm,
  em(×parent font size), ex(×0.5 parent), % (×viewportWidth at parse for plain
  lengths; kept as raw % for width/height/offsets/margin/padding and resolved at
  layout), unitless number = px, negatives supported.
- font-size keywords xx-small…xx-large (NN 1-7 scale 11-48px), smaller/larger
  (×0.83/×1.17), % of parent size.

Notable quirks/risks for the upgrade:
- Parser expands shorthands AND ComputedStyle re-handles them (two code paths to keep
  in sync). IsLengthToken duplicated with DIFFERENT unit sets (CssParser.cs:698 =
  px/pt/em only; ComputedStyle.cs:1099 = full set). Dead/weird TryNum helper
  (CssParser.cs:702-704).
- display:table-* enum values exist but real table layout is tag-driven; display:table
  on a non-table element likely not wired to TableLayout (verify in layout task).
- Selector engine is ahead of 1999 (~ sibling, ^= $= *=) but behind on CSS2 basics
  (:first-child, :lang, :before/:after+content).
- CssSelectorTests.cs locks: dynamic pseudo-classes, cascade inline>id>class>type,
  !important, shorthands, inheritance, unit math, vertical-align %, list types,
  per-side borders, imports, color formats. Any upgrade must keep these green.

Next actions (for implementer): add :first-child/:lang; :before/:after + content
storage; missing CSS2 properties (min/max-*, cursor, outline, border-collapse,
border-spacing, table-layout, caption-side, clip, vertical-align lengths, font-size-
adjust, text-shadow, inline-table); real 'inherit' keyword; media-dependent @import;
keep existing quirks unless checklist says otherwise.

---
Task ID: 3-d
Agent: explore (VBS mapping)
Task: Map VBScript engine internals (1.0) for the 1.0→5.0 upgrade

Scope read: retro96/Engine/Vbs/*.cs (11 files, ~4.4k LOC), tests/VbsTests (csproj only —
compiles Engine/Vbs + VbsEngineTests.cs), tests/RetroTests/VbsEngineTests.cs (23 tests),
plus browser wiring: BrowserCanvas.cs:900-1230, Form1.cs:1499-1537, HtmlParser.cs:1136-1199.

## 1. Statements (VbsParser.cs / VbsAst.cs / VbsInterpreter.cs)

- Dim (fixed, dynamic `Dim a()`, multi-dim, comma lists): SUPPORTED
- ReDim + Preserve (last-dim only, err 10): SUPPORTED
- Erase (fixed→zeroed, dynamic→deallocated): SUPPORTED
- Set (object assignment; implicit Let → default prop or err 438): SUPPORTED
- Call (incl. `f(x)` single-paren ForceByVal quirk): SUPPORTED
- Sub / Function (ByVal/ByRef, hoisted, implicit fn-name return var): SUPPORTED
- Exit Sub/Function/Do/For: SUPPORTED (via control-flow exceptions)
- If/Then/Else/ElseIf — block AND single-line forms: SUPPORTED
- Select Case (lists, `lo To hi`, `Case Is <op>`, Case Else): SUPPORTED
- For..Next (+Step, optional `Next i`, bounds evaluated once): SUPPORTED
- For Each..Next (VbsArray row-major + IVbsDispatchObject.TryEnumerate): SUPPORTED
- Do..Loop — all four While/Until top/bottom forms: SUPPORTED
- While..Wend: SUPPORTED
- On Error Resume Next / GoTo 0 (per-procedure flag; label form N/A): SUPPORTED
- Option Explicit (must be first statement, dup rejected): SUPPORTED
- Rem (statement position only) + `'` comments + `_` line continuation: SUPPORTED
- Const (comma list; `Public Const` ok): SUPPORTED
- Public/Private: PARTIAL — script level only, `Public/Private [Dim|Const] x`;
  visibility NOT enforced (parses to plain Dim); `Public Sub Foo()` = syntax error
  (MISSING for procedures); inside procedures → compile err 1024
- With..End With: MISSING (no WITH keyword)
- Class..End Class: MISSING (no CLASS keyword)
- Property Get/Let/Set: MISSING
- Stop: parsed but no-op (VbsNopStatement) — no debugger halt
- Also MISSING: Execute/ExecuteGlobal/Eval statements, GetRef, Exit Property,
  Me, New operator, line numbers/labels, `With` `.Member` shorthand

## 2. Builtins in VbsBuiltins.cs (all 89 table entries)

Conversions: CBool CByte CCur CDate CDbl CInt CLng CSng CStr Hex Oct Asc Chr
Strings: Len Left Right Mid InStr InStrRev Replace Split Join StrComp Space String
UCase LCase Trim LTrim RTrim StrReverse
Math: Abs Atn Cos Sin Tan Exp Log Sqr Fix Int Sgn Round Rnd Randomize
Dates: Now Date Time Timer Year Month Day Hour Minute Second Weekday MonthName
WeekdayName DateAdd DateDiff DatePart DateSerial TimeSerial DateValue TimeValue
FormatDateTime FormatNumber FormatCurrency FormatPercent
Type info: TypeName VarType IsNull IsEmpty IsArray IsObject IsDate IsNumeric LBound UBound
Host-routed: Err (object) MsgBox InputBox CreateObject GetObject

Checklist gaps — MISSING: Array, Filter, RGB, Eval, Execute, ExecuteGlobal, GetRef,
Escape, Unescape, LoadPicture, Debug.Write/WriteLine, and the ScriptEngine /
ScriptEngineMajorVersion / ScriptEngineMinorVersion / ScriptEngineBuildVersion GLOBAL
functions (they exist ONLY as WScript.ScriptEngine* members via
WshStyleVbsHost.CreateWScriptObject — and the browser never registers WScript as a
named item, only "document"). Present from checklist: Split, Join, Replace, InStrRev,
StrReverse, Round, FormatNumber, FormatCurrency, FormatPercent, FormatDateTime, DateAdd,
DateDiff, DatePart, MonthName, WeekdayName, TypeName, Timer, CreateObject, GetObject
(browser host denies both → err 429 by design).

## 3. RegExp object: ENTIRELY ABSENT
No RegExp class, no Pattern/IgnoreCase/Global/Test/Replace/Execute, no Match/Matches/
SubMatches objects anywhere in Engine/Vbs. CreateObject("VBScript.RegExp") is denied by
the sandboxed host. .NET System.Text.RegularExpressions is available for the 5.0 build.

## 4. Named constants + identifier resolution
VbsBuiltins.Constants (case-insensitive dict): vbCr vbLf vbCrLf vbNewLine vbTab
vbFormFeed vbVerticalTab vbNullChar vbNullString; vbBinaryCompare/vbTextCompare;
vbUseSystemDayOfWeek + vbSunday..vbSaturday; vbUseSystem vbFirstJan1 vbFirstFourDays
vbFirstFullWeek; all MsgBox button/icon/result consts; VarType codes (vbEmpty..vbByte,
vbArray=8192); vbGeneralDate/LongDate/ShortDate/LongTime/ShortTime; vbUseDefault
vbTrue vbFalse; vbObjectError (0x80040000).
Resolution order (VbsInterpreter.EvalName): procedure locals → globals → builtin table
(zero-arg CALL — bare `Now`/`Err`/`Rnd` invoke!) → constants table → known procedure
name (returns Empty) → Option Explicit ? err 500 : returns Empty. Note constants are
consulted after the function table, and user variables shadow builtins.

## 5. Err object + On Error
VbsErrObject : IVbsDispatchObject (VbsObjects.cs). Number/Description/Source read+write;
Raise(number[,source[,desc]]) sets state then throws; Clear(). HelpFile/HelpContext:
MISSING. On Error: VbsFrame.ErrorResumeNext bool — procedure-scoped, default false at
frame entry (each call), On Error GoTo 0 → false. ExecuteStatementList wraps each
statement in try/catch(VbsRuntimeException) → UpdateErr (Number/Description/Source
only). Err survives across blocks (VbsSession reuses one interpreter; Run(VbsScript)
shares Err/globals/Option Explicit). Line info attached at statement level (err.Line==0
backfill). Recursion cap 300 → err 28.

## 6. Operators (VbsOps in VbsVariant.cs) — ALL PRESENT
^ * / \ Mod + - & = <> < <= > >= Is Not And Or Xor Eqv Imp; unary -, Not.
Parser precedence (high→low): ^ > unary- > * / > \ > Mod > + - > & > comparisons+Is >
Not > And > Or > Xor > Eqv > Imp. Is is reference equality (Nothing-aware). And/Or/Xor/
Eqv/Imp are bitwise on integrals, logical on Boolean/Null. No short-circuit (both sides
always evaluate) — period-correct.

## 7. Classes/objects — how they'd fit today
- Object values live in VbsVariant as VbVarType.Object with `_box` = IVbsDispatchObject;
  Nothing = Object subtype with null box (already a first-class literal).
- IVbsDispatchObject (TryGetMember/TrySetMember/TryInvoke/TryGetDefault/TrySetDefault/
  TryInvokeDefault/TryEnumerate + VbsTypeName) is an almost perfect shape for a
  VbsClassInstance: fields = Dictionary<string,VbsCell>, methods = dispatch back into
  interpreter; Property Get→TryGetMember, Property Let→TrySetMember, Property Set→
  TrySetMember with object check, `obj.Prop = v` already routes through
  AssignTarget→TrySetMember, `obj.Prop(args)` through EvalInvoke→TryInvoke.
- Gaps to build: (a) parser: CLASS/END CLASS, PUBLIC/PRIVATE members, PROPERTY
  GET/LET/SET (+EXIT PROPERTY), Sub/Function as methods, NEW keyword +
  `Set x = New Foo` (today `New Foo` parses as junk → runtime err 424), ME,
  `With...End With` + `.Member` shorthand; (b) VbsAst: VbsClassStatement,
  VbsPropertyStatement, VbsNewExpr (+With node); (c) interpreter: class registry
  alongside _procedures (hoisted like procedures), a `Me` binding — VbsFrame has no
  this/frame.Me slot and InvokeProcedure builds Locals only, so methods need either a
  bound wrapper object or a frame extension; (d) Class_Initialize at New,
  Class_Terminate has NO lifetime hook today (no GC/Deterministic release — needs an
  explicit strategy, e.g. terminate on session teardown or refcount emulation);
  (e) EvalName/EvalMemberGet must consult a With-context stack for `.Member`.
  VBScript 5.0's Me/Class/Property semantics map 1:1 onto the existing two-scope model
  (class instance = third scope: fields, like globals but per-instance).

## 8. Version + event handler wiring
- Version: VbsBuiltins.EngineMajor/Minor/Build = 1/0/0 (consts). Only surfaced via
  WScript object (WshStyleVbsHost) as a NAMED ITEM — tests use it; the browser does
  NOT register WScript (namedItems = { "document" } only, BrowserCanvas.cs:963).
  No ScriptEngine* global functions. Form1 About page says "VBScript 1.0".
- Event handlers: BrowserCanvas.RunVbsScript keeps one VbsPageState
  (ConditionalWeakTable<DomDocument>) per doc; after each block it calls
  RegisterVbsProcedures(jsInterpreter, page) which defines EVERY VBS procedure as a
  JS global function (JsValue.FromFunction wrapper: JsValue→VbsVariant marshalling,
  Session.Call, VbsVariant→JsValue). So `onclick="SomeVbsSub"` works (JS FireEvent →
  global fn → VBS). But: NO automatic `Sub btn_onclick` implicit wiring (nothing
  scans for <name>_on<event>) and NO `<script for="btn" event="onclick">` support —
  HtmlParser.ExecuteScript dispatches purely on language/type, for/event attrs
  ignored. `vbscript:` href URLs run through RunVbsScript (BrowserCanvas:9511,10425).
  VBS document.write buffers into VbsPageState.WriteBuffer → ApplyVbsDocumentWrite
  REPLACES the whole document (full re-parse + reflow), unlike JS incremental write.
- VBS DOM named item "document" is a minimal private VbsDocumentObject (BrowserCanvas:
  1087): forms by name/id → VbsFormObject → VbsFormControlObject with Value
  get/set only. Far smaller than the JS DomBindings.

## 9. VBS↔JS global sharing
One-way only: VBS procedures → JS globals (RegisterVbsProcedures, refreshed after
each VBS block; overwrites same-name JS globals). Marshalling covers scalars only
(null/bool/number/string; VBS Empty→JS undefined; Currency→number; VBS
arrays/objects→JS undefined). VBS globals/arrays/Err are NOT exposed to JS; JS
globals are NOT exposed to VBS (no reverse bridge). Same-document blocks share VBS
globals/procedures; frames get separate sessions (per-document table).

## 10. Tests (23, all green)
VbsTests.csproj compiles Engine/Vbs/*.cs + VbsEngineTests.cs. Covers: banker's
rounding (CInt/Round), Integer overflow promotion, FormatNumber culture, cross-block
shared globals, document/form control objects, ByRef paren quirks (3 tests), compile
errors 1044/1041, For-bound single-eval + overshoot, On Error reset-at-procedure-entry
+ GoTo 0, Err.Raise incl. vbObjectError range, Null/Empty propagation, binary string
compare, Erase fixed vs dynamic, Select Case forms, ReDim Preserve last-dim-only (err
10), date literals + DateDiff w/ww + DatePart ww, InStr 3-arg ambiguity, Split/Join
Null elements, recursion err 28, CreateObject denied err 429.

## Upgrade priorities for 5.0 (from this map)
1. Parser/AST/interpreter: Class + Property Get/Let/Set + New/Me + With/End With
   (biggest chunk; With also needs the EvalName member-resolution change).
2. Builtins: Array, Filter, Eval/Execute/ExecuteGlobal, GetRef, Escape/Unescape, RGB,
   ScriptEngine* global functions, Debug object, Err HelpFile/HelpContext.
3. RegExp object (pure additive: new IVbsDispatchObject impl over System.Text.RegularExpressions
   + Match/Matches/M SubMatches collections; wire CreateObject("VBScript.RegExp") in hosts).
4. Version bump 1.0→5.x + expose ScriptEngine* + WScript named item in browser wiring.
5. Event handler wiring: `<script for= event=>` parsing + implicit <name>_on<event>
   procedure discovery (HasProcedure already exists for probing).
6. Optional: VBS→JS bridge bidirectional (JS global lookup from VBS; expose VBS
   globals), incremental document.write for VBS.

---
Task ID: 3-c
Agent: explore (JS/DOM binding layer)
Task: Map JavaScript + DOM binding internals for the 1999 (IE5/NS4.7/DOM1) upgrade

Files read (paths relative to repo root retro96/):
  retro96/Engine/Js/{JsLexer,JsParser,JsAst,JsInterpreter,JsRuntime,JsValue,JsScope,DomBindings}.cs
  retro96/Engine/Dom/DomNode.cs, Engine/Html/HtmlParser.cs (script/lang handling),
  BrowserCanvas.cs + Form1.cs (event dispatch/wiring), BrowserRuntime.cs (gating flags),
  tests/RetroTests/{JsEngineTests,FormDomTests,FrameLinkDomTests}.cs. No code changes.

1. LANGUAGE FEATURES
  try/catch/finally/throw      SUPPORTED (ParseTry/ExecuteTry: catch binds Error obj for
                               runtime errors; finally runs then re-propagates; ASI-aware)
  switch (case/default, fall-through) SUPPORTED (StrictEquals match)
  do-while                     SUPPORTED
  labelled break/continue      SUPPORTED (LabeledStatement; labels threaded to loops)
  === / !==                    SUPPORTED (JsValue.StrictEquals)
  in / instanceof              SUPPORTED (ExecuteIn/ExecuteInstanceof; for-init noIn rule)
  delete / void / typeof       SUPPORTED (typeof on undeclared id → "undefined"; delete
                               routes through virtual JsObject.Delete)
  regex literals               SUPPORTED (lexer w/ regex-context detection, g/i/m, classes)
  RegExp object                SUPPORTED (test/exec/toString, lastIndex w/ 'g', source/
                               flags/global/ignoreCase/multiline; RegExp ctor w/ flag
                               validation + copy-from-regex; compile() MISSING)
  String.match/replace/search/split with regex  SUPPORTED (match g/non-g; replace supports
                               $1..$9 groups; search; split w/ regex separator + limit)
  function expressions         SUPPORTED (named or anonymous; closures via JsFunction)
  Array push/pop/shift/unshift/splice/sort/reverse/join/concat/slice  ALL SUPPORTED
                               (splice/indexOf hidden only in strict IE3 mode; sort = stable
                               insertion sort + comparator; forEach/map/filter also present)
  Number toFixed/toExponential/toPrecision  MISSING (Number.prototype has only
                               toString(radix 2-36)/valueOf)
  Date getFullYear vs getYear  BOTH SUPPORTED: getFullYear = full local year; getYear =
                               year-1900 (era semantics, matches IE5/NN for 1999=99).
                               Also getTime/valueOf/setTime/toString/toLocaleString/
                               toGMTString/parse + all local getters. MISSING: getUTC*,
                               set* (except setTime), toDateString/toTimeString.
  Misc present: with, comma ops, all compound assigns incl. >>>=, arguments object,
  call/apply, eval (pref-gated), console.log/warn/error, Error/TypeError/RangeError/
  EvalError/ReferenceError, escape/unescape (JS1.1), parseInt (hex+octal era rules),
  parseFloat, isNaN/isFinite, Math, String HTML wrappers (big/bold/link...).

2. DOM LEVEL 1
  getElementById               SUPPORTED (exact then case-insensitive fallback; hidden in
                               strict IE3 mode)
  getElementsByTagName        SUPPORTED (document + elements, '*' ok)
  createElement                SUPPORTED (elements only)
  createTextNode               MISSING
  appendChild/insertBefore/removeChild  SUPPORTED (return wrapped child, reflow)
  replaceChild / cloneNode     MISSING
  getAttribute/setAttribute    SUPPORTED (setAttribute('onclick', src) wires handler)
  removeAttribute              MISSING
  nodeType/nodeName/nodeValue  SUPPORTED (elements 1, text 3, comment 8 via TextNodeObject)
  parentNode/childNodes/first/last/prev/nextSibling  SUPPORTED (child nodes incl. text)
  attributes (NamedNodeMap)    MISSING
  element.style camelCase      SUPPORTED (InlineStyleObject: camelCase AND hyphenated both
                               normalized; cssText read/write; writes reflow)

3. IE DHTML MODEL
  document.all                 SUPPORTED — callable LegacyDomCollection: all[i], all(i),
                               all("name"), .item(i|name[,dupIdx]), .namedItem(), .tags(tag);
                               live snapshot; document.all===document.all stable; element.all
                               = descendants sub-collection. Gated SupportsInternetExplorerLegacy.
  innerHTML/outerHTML          SUPPORTED (read = serializer incl. void/script rules; write =
                               parse fragment + replace children/self + reflow)
  innerText/outerText          SUPPORTED (read skips script/style/noscript; writes mutate DOM)
  insertAdjacentHTML           SUPPORTED (all 4 positions, IE-legacy gated)
  insertAdjacentText           MISSING
  style.pixelLeft/pixelTop/pixelWidth/pixelHeight/posLeft    MISSING
  currentStyle                 MISSING
  offsetLeft/Top/Width/Height/Parent, clientWidth/Height, scrollTop/Left   MISSING (no
                               geometry exposed to JS at all; LayoutBox/ComputedStyle exist
                               on DomElement so implementation is feasible)
  parentElement                SUPPORTED (same path as parentNode)
  children / contains()        SUPPORTED
  document alinkColor/linkColor/vlinkColor/fgColor/bgColor  SUPPORTED (virtual props, live
                               read/write + repaint)
  location hash/host/hostname/pathname/port/protocol/search  SUPPORTED (LocationObject;
  href setter + bare location="url" navigate)
  uniqueID, readyState, onreadystatechange   MISSING
  window.showModalDialog       MISSING
  document.selection/createTextRange   SUPPORTED (selection.createRange/empty; body.
  createTextRange; TextRange: text/parentElement/moveToElementText/duplicate/collapse/
  select/setEndPoint; IE-legacy gated)
  Also gated IE: ScriptEngine()/ScriptEngineMajorVersion()=1/Minor/Build (JScript 1.0
  probes — need 5.x bump for 1999 personas).

4. NS4 LAYER MODEL
  document.layers              STUB ONLY — returns cached EMPTY array (SupportsNetscapeLegacy);
                               no layer objects at all
  layer left/top/zIndex/visibility/clip/bgColor/src, moveTo/moveBy/resizeTo/moveAbove/
  moveBelow/load()             ALL MISSING
  window.innerWidth/innerHeight SUPPORTED (outerWidth/Height too)
  pageXOffset/pageYOffset      MISSING
  captureEvents/releaseEvents/routeEvent/handleEvent  MISSING

5. EVENTS
  Inline on* attributes: HtmlParser.Create() copies every on* attr into
  element.EventHandlers["onclick"]=source (DomNode.DomElement.EventHandlers dict).
  Physical dispatch: BrowserCanvas.cs mouse/key/focus/change/submit handlers call
  JsInterpreter.FireEvent(element, "onxxx", evtObj) (mouse ~7384/7406/8240/9382-9593,
  keys 5117/5133/5357, focus/change/blur 6257/6307/9644/10034/10072/10270, submit
  10338); Form1.cs fires window/body onload (1285-1317, 2712-2724), frame onload
  (2061-2066) and <img> onload/onerror (1765-1771).
  FireEvent priority: (1) DOM-0 property handler (interpreter._domEventProperties
  keyed by DomElement — element.onclick=fn survives wrapper churn) with wrapper as
  'this'; (2) wrapper.Properties fallback; (3) inline attribute source parsed as a
  function body in a global-scope child with 'this'=ElementWrapperHook(element) and
  'event' in scope; JsReturnException value = handler return ("return false" cancels).
  window.event                 SUPPORTED (IE-legacy gated): installed on window during
                               dispatch, restored after; nested-dispatch safe
  event.srcElement             SUPPORTED (set to this element)
  event.cancelBubble           FIELD ONLY (no bubbling engine — setting it is a no-op)
  event.returnValue            FIELD ONLY — shell cancels on the handler's *return value*
                               only (event.returnValue=false does NOT cancel link/submit)
  event.keyCode / clientX/Y    SUPPORTED (CreateKeyEvent: key/keyCode/which/charCode;
                               CreateMouseEvent: type/clientX/Y/screenX/Y/x/y/button/
                               altKey/ctrlKey/shiftKey/metaKey)
  attachEvent/detachEvent      MISSING
  addEventListener             MISSING
  captureEvents/releaseEvents/routeEvent/handleEvent  MISSING
  e.target, e.modifiers, pageX/pageY, mouse-e.which   MISSING (e.which only on key events)

6. HOST OBJECTS
  window.open(url)             PARTIAL: features string parsed? NO — accepted+ignored;
                               opens via canvas.OpenNewWindow (ScriptedWindowsAllowed gated);
                               returns undefined (no new-window object); target/name ignored
  opener                       MISSING
  top/parent                   PRESENT but SELF-REFERENTIAL (each window/frame sets top/
                               parent/self to itself — no cross-frame hierarchy in JS)
  frames[]                     MISSING (frames render via BrowserCanvas.FrameView with
                               per-frame interpreters in Form1.CreateFrameContext, but no
                               JS window.frames collection / cross-frame scripting)
  status/defaultStatus         SUPPORTED (window.status write + onMouseOver return true)
  setTimeout/setInterval       SUPPORTED with BOTH function AND string-code args (string
                               compiled to native fn); clearTimeout/clearInterval; ticked
                               by shell UI timer → TickTimers() (reentrancy-guarded)
  navigator.userAgent/appName/appVersion/appCodeName/language/platform/cookieEnabled
                               SUPPORTED (profile-dependent: Retro96 native / IE3 / NN3);
                               plugins[]/mimeTypes[] present but EMPTY; javaEnabled() MISSING
  screen.width/height/colorDepth (+availWidth/availHeight/pixelDepth)  SUPPORTED
  history                      SUPPORTED (length/back/forward/go)
  location.replace (+reload/toString)  SUPPORTED
  document.cookie              SUPPORTED (live CookieStore read/write, CookiesEnabled-gated)
  document.lastModified/referrer  SUPPORTED (seeded via DocumentBindingsState)
  document.write/writeln       SUPPORTED (parse-phase stream splicing; post-parse implicit
                               document.open() REPLACES live doc in place + reflow; scripts
                               in replacement markup intentionally not executed)
  document.open/close/clear    present as era no-ops
  javascript: URLs             SUPPORTED for anchor clicks (BrowserCanvas 9505: onclick
                               first, then ExecuteString(url body)); vbscript: too.
                               NOT routed through location-assignment navigation path.
  onerror                      window slot seeded but NEVER fired; element onload/onerror
                               fired for images only
  Image() preloading           SUPPORTED (ImageObject: src setter → canvas.PrefetchImage,
                               complete/width/height)
  escape/unescape              SUPPORTED

7. WRAPPING / DOM-0 FORM ACCESS
  DomBindings.WrapElement(element, state): dictionary cache state.ElementWrappers → ONE
  JsObject identity per DomElement. ElementWrapper : JsObject overrides Get/Set virtually:
  reads/writes route to live element attrs + canvas RequestRerender/ReflowDocument.
  JsInterpreter.ElementWrapperHook (internal Func<DomElement,JsObject>) installed by
  Form1 (1462 main page, 1986 frames) as e => DomBindings.WrapElement(e, state) — gives
  inline handlers a live wrapper as 'this' without JsInterpreter→DomBindings dependency.
  document.formName.fieldName: DocumentObject.Get default case resolves named applets
  (id/name, ci) → [IE-legacy: ANY element by id/name ci] → named forms (Ordinal) → named
  images; ElementWrapper.Get on <form> then resolves the field name against
  ControlsOf(form) (incl. foster-parented controls via FormOwner). Window also gets named
  forms/images/applets (RegisterAll 114-148). form.elements = numeric+named collection,
  form.length = control count, form.submit() bypasses onsubmit.
  Host-object extension pattern: virtual JsObject subclasses (DocumentObject,
  LocationObject, ImageObject, InlineStyleObject, LegacyDomCollection (a JsFunction
  subclass — callable collection), TextNodeObject, LegacySelectionObject,
  LegacyTextRangeObject, EmbeddedScriptObject, JsPromiseObject).

8. SCRIPT LANGUAGE VERSION GATING
  HtmlParser.ExecuteScript (1141-1163): language/type attrs select ONLY VBScript vs
  JavaScript. "JavaScript1.1/1.2/1.3", "JScript", "LiveScript", "ECMAScript" all
  prefix-match → plain JavaScript, NO version-specific behavior. IE3 mode additionally
  rejects application/* MIME types. BrowserRuntime.JavaScriptVersion +
  ScriptEngineMajorVersion()=1 report the profile version — must be bumped for IE5/NN4.7
  personas. No JavaScript1.3 (== NaN fix, function statements in blocks) semantics.

KEY GAPS FOR THE 1999 UPGRADE (ranked):
  - DOM1: createTextNode, replaceChild, cloneNode, removeAttribute, attributes
    NamedNodeMap, ownerDocument/hasChildNodes
  - IE5 DHTML: currentStyle, offset*/client*/scroll* geometry (LayoutBox/ComputedStyle
    already exist on DomElement — feasible), pixel*/pos*, insertAdjacentText, uniqueID,
    readyState/onreadystatechange, showModalDialog, attachEvent/detachEvent,
    event.returnValue honored for cancel, real window.frames[]/opener/top/parent
  - NS4.7: entire layer model (document.layers, layer props/methods, pageXOffset/
    pageYOffset, captureEvents/routeEvent), e.target/which/pageX/pageY/modifiers
  - ES3/JS1.3: Number.toFixed/toExponential/toPrecision, Date setters+UTC+getUTC*,
    RegExp.compile, per-version language gating, ScriptEngine* version bump
  - window.event only exists in IE-legacy profile; returnValue/cancelBubble are inert
    fields (no propagation engine at all)

---
Task ID: 3-e
Agent: Explore
Task: Map network stack (HTTP/1.1 upgrade surface), layout engine (CSS2 positioning), and browser persona/profile concept (IE5 vs NS4.7 emulation)

Findings (RESEARCH ONLY — no code changes):

1. HTTP (Engine/Network/HttpClient.cs, 693 lines)
- Request line: `GET <path>?<query> HTTP/1.0` (line 292–293). Broker path (SandboxBrokerHost.cs:744) also HTTP/1.0. NO HTTP/1.1 anywhere yet.
- Persistent connections: NONE. `Connection: close` hardcoded (line 347); new TcpClient per request; tcpClient.Close() in finally.
- Host header: always sent (port omitted only when default per scheme) — lines 296–301.
- Chunked transfer-encoding: YES, decoded defensively (ReadChunkedAsync, hex sizes, trailers, maxBodySize cap). Gzip: decoded defensively; `Accept-Encoding: gzip` actually sent when RequestCompressedResponses (default TRUE, UserSettings.cs:78).
- Caching: NONE. No Cache-Control/ETag/If-None-Match/If-Modified-Since/Last-Modified conditional requests anywhere. Only `last-modified` is read (Form1.cs:1243) for document.lastModified. ImageCache = in-memory decoded-bitmap cache keyed by absolute URL (session lifetime, knownBad/retryAfter backoff) — no HTTP revalidation.
- Redirects: 301/302/303/307/308; 301/302/303→GET, 307/308 keep method+body (line 229–235); cap = MaxHttpRedirects (default 5); FollowHttpRedirects toggle; https→http downgrade rejected; Set-Cookie processed on redirect hops; Basic auth never crosses origin change.
- Basic auth: shell-only, per-navigation. Form1.cs:999–1032 — 401 → PromptForCredentials → sets HttpClient.BasicAuthHeader/Origin, single retry, cleared in finally. No credential cache. WWW-Authenticate realm surfaced on error page.
- Refresh: <meta http-equiv=refresh> handled (HtmlParser.cs:816–822 → Form1 ParseMetaRefresh: delay 0–600s, URL vs base, file: base via FileUrls). HTTP `Refresh:` response HEADER not handled.
- Charset: BodyDecoder (FrameLoader.cs:548): BOM > HTTP Content-Type charset > strict-UTF-8 sniff > Latin-1; meta charset fallback when no transport charset (Form1.cs:1228–1235 re-decodes when meta ≠ success.Charset). NOTE: HttpClient defaults charset to "iso-8859-1" even with no HTTP charset param, so meta only wins via the Form1 re-decode path.
- UA on the wire: `UserAgentOverride ?? BrowserRuntime.UserAgent` (line 304). Exact default strings (UserSettings.cs:36–41):
  Retro96 mode: "Mozilla/5.0 (Retro96/1.0; Windows 95; IE3+NN3 compatibility)"
  Netscape3 mode: "Mozilla/3.0 (Win95; I)"
  IE3 mode: "Mozilla/2.0 (compatible; MSIE 3.02; Windows 95)"
  Plus hardwired: Accept: "text/html, image/gif, image/x-xbitmap, image/jpeg, image/pjpeg, */*"; Accept-Charset: "iso-8859-1,*,utf-8".
- HTTP/1.1 upgrade gaps: version string, keep-alive pooling, Connection header negotiation, 100-continue (optional). Host/chunked/gzip groundwork already in place.
- ResourceLoader: in-flight dedup (Lazy GetOrAdd), 32 permits weighted by MaxConcurrentResourceFetches (default 8), per-page budget 1000, 15s idle budget reset; plugin network-rule evaluator + optional broker (SandboxContext.BrokerAllNetwork/BrokerImages → broker process fetch, base64 body).

2. Cookies (Engine/Network/CookieStore.cs, 637 lines)
- Netscape 1996 spec, NOT RFC 2109 (Max-Age/Comment/Version deliberately absent). Attributes: expires / domain / path / secure; HostOnly flag (no Domain attr → host-only).
- Y2K-adjacent date parsing: ParseEraDate handles "Wed, 09-Nov-99 23:12:40 GMT" 2-digit years with .NET's 2029 pivot (00–29→2000s, 30–99→1900s), 4-digit, asctime, RFC 1123; parsed as GMT (AssumeUniversal|AdjustToUniversal).
- Limits: 300 total / 20 per domain / 4096B name+value; LRU eviction (LastAccessedUtc updated on read+write). Past-expires = delete idiom. Comma-joined Set-Cookie split with date-comma awareness. Cookie header: longest-path-first, stable ties. Domain must domain-match host + contain a dot (blocks Domain=com).
- API: Set(setCookieHeader, requestUrl), Get(url)→"n=v; n2=v2" (no prefix), GetCookieValue(name,url), SetCookieValue(name,value,url,path,domain,expires,secure) [round-trips through header syntax for validation], Count, ClearExpired(), ClearAll(). Thread-safe (single lock). Identity = (Name,Domain,Path) — value/expiry changes replace.

3. TLS/HTTPS
- Single implementation: HttpClient.SendOverSocketAsync lines 415–428 — `new SslStream(stream, false, (s,cert,chain,errors) => _allowInvalidCertificates || errors == SslPolicyErrors.None)` + `AuthenticateAsClientAsync(url.Host)`. No explicit TLS version (OS default = TLS 1.2/1.3 on modern .NET) — an era-accurate TLS 1.0 cap would need SslProtocols + callback policy. Cert failure → CertError result → error page with "Accept Risk" hook (Form1 _pendingCertRetryUrl, max 3 attempts).

4. Layout (Engine/Layout/*, 5.7k lines total)
- LayoutBox fields: X,Y (border-box origin, document coords), Width,Height (CONTENT size), per-side Margin/Padding/Border + % variants, IsFloated/FloatSide, IsAbsolutelyPositioned, TextRun/ReplacedImage, BoxType (Inline/InlineBlock/Block/ListItem/Replaced/Frame/Anonymous/Table/TableRow/TableCell/TableCaption). NO Position/Top/Left/Bottom/Right/ZIndex on the box — geometry offsets + ZIndex live on ComputedStyle (Position, Top/Right/Bottom/Left + TopPercent etc., ZIndex int, ComputedStyle.cs:107–112) and are read via Element.Style at layout/paint time.
- Positioning IS implemented: IsAbsolutelyPositioned = Position Absolute OR Fixed (LayoutEngine.cs:708). LayoutAbsolute (1904–1958): left/right/top/bottom with % resolved against positioned containing block (width for L/R, height for T/B), static-position fallback, single-shift of subtree. FindPositionedContainingBlock walks to nearest relative/absolute/fixed ancestor, else root. LayoutRelative (1960): offsets whole subtree (dx/dy from Left/Top or -Right/-Bottom).
- GAP: position:fixed is treated as absolute anchored to the root/containing block but does NOT stay pinned on scroll — paint applies one scroll translate to the whole tree (Renderer.RenderToCanvas line 540).
- Floats: FloatContext (InlineLayout.cs:16) Y-band model with left/right edges incl. margin box; PlaceFloat slides float down candidate band bottoms until it fits (Netscape side-by-side behaviour); inline runs wrap via GetLeftEdge/GetRightEdge per line Y; float margins included in band extents; floats don't consume flow height but advance currentY if they physically start lower.
- Clear: CSS `clear` on block children (LayoutBlockChildren 1473–1478: GetClearY + ResetBandsBelow) AND `<br clear=left|right|all|both>` (InlineLayout.GetBrClear 874).
- Margin collapsing: adjacent siblings via CollapseMargins (max for +/+, min for -/-, sum for mixed, LayoutEngine.cs:2236) with prevMarginBottom pending pattern; empty-block collapse-through (1522–1531); whitespace-only inline runs don't break collapsing; floats don't break collapsing. NO parent↔child collapsing (first child's margin-top stays inside parent content box — NN-like).
- Tables (TableLayout.cs, 1671 lines): full min/pref/auto column algorithm, colspan/rowspan, caption, border-box TARGET for table width attr (tableW includes border+cellspacing; tableBox.Width = tableW − 2*border, line 322), cell content width = cellW − 2*cellPadding − borders, NN defaults cellspacing=2 cellpadding=1.
- Bonus: NN <multicol> balanced multi-column (LayoutMulticolumn 1682).

5. Box model
- box.Width/Height = CONTENT box (W3C) for CSS pixel widths (GenerateBoxes line 327: `box.Width = style.Width.Value` direct).
- MIXED quirks already present: percentage CSS widths AND HTML width="%" attributes resolve BORDER-box-ish — `containingWidth*pct/100 − margins − borders − padding` (ResolveAutoWidth 1331–1338, ApplyHtmlPresentationalAttrs 775–781) i.e. the border box equals the percentage. Auto width fills container minus margins/border/padding.
- Quirks mode: DomDocument.QuirksMode string exists ("html32" default, "html20", "strict", "quirks" from HtmlParser.DetermineQuirksMode 1425–1445) but is WRITE-ONLY — no consumer anywhere.
- IE5 border-box gating points if added: (a) GenerateBoxes px-width application, (b) ResolveAutoWidth, (c) LayoutBlockChildren anonymous-box width — all in LayoutEngine; gate flag candidates: new RetroEngineMode member or doc.QuirksMode. Tables already border-box.

6. Personas/profiles (UserSettings.cs + BrowserRuntime.cs + DomBindings.cs)
- Concept EXISTS but only 1996 personas: enum RetroEngineMode { Retro96, Netscape3, InternetExplorer3 } (UserSettings.cs:8), persisted as INI `Engine.Mode`, selected in PreferencesDialog engine dropdown (index 0/1/2). NO IE5/NS4.7 personas yet — enum + UA constants + gates must be extended.
- UA switching: Settings.UserAgentOverride (INI `useragent.override`, ≤2048 chars) overrides all modes; EffectiveUserAgent = override ?? mode default. BrowserRuntime.UserAgent = Settings.EffectiveUserAgent; HttpClient.UserAgentOverride only overrides the wire (broker sessions).
- navigator (DomBindings.RegisterNavigator 340–378): userAgent = prefs.EffectiveUserAgent (identical to wire, by design); appName/appVersion/appCodeName per mode: IE3→"Microsoft Internet Explorer"/"3.02 (Windows 95)"/"Mozilla"; NN3→"Netscape"/"3.01 (Win95; I)"; Retro96→"Retro96"/"1.0 (…)". Also language "en", platform "Win32", cookieEnabled, empty mimeTypes/plugins.
- Persona gating today (BrowserRuntime flags IsInternetExplorer3 / IsNetscape3 / SupportsInternetExplorerLegacy (=Retro96||IE3) / SupportsNetscapeLegacy (=Retro96||NN3)):
  - document.all: gated `SupportsInternetExplorerLegacy` (DomBindings 879; callable LegacyDomCollection via CreateLegacyElementCollection 1336; element.all too at 1623; JsValue.cs:397 IE-style all(...) callability).
  - document.layers: gated `SupportsNetscapeLegacy` (DomBindings 891) but returns an EMPTY ARRAY — a sniffing stub, no layer objects.
  - documentElement HIDDEN in IE3 mode (869); legacy IE event model in JsInterpreter (444, 602); ES3+ features hidden when IsInternetExplorer3 (JsInterpreter 1937/1966/2026); JsRuntime 334 mode switch; DOM-0 named access (forms/images/applets by name/id) with IE-style any-element named access only under SupportsInternetExplorerLegacy (921–929).
  - JavaScriptEngineName/Version: "Retro96 Script Engine"/"Microsoft JScript 1.0"/"Netscape JavaScript 1.1".

7. Marquee/bgsound/blink rendering
- marquee: Renderer.cs 731–917 — block lays out normally; PaintMarqueeContent repaints content under horizontal translate clipped to border box; behaviors scroll/slide/alternate, direction, scrollamount (default 6, clamp 1–4096), scrolldelay (default 85), bgcolor; per-element animation epoch; translation quantized to device pixel; double-paint for seamless wrap. Driven by BrowserCanvas._animationTimer (16 ms ≈ 60 fps GPU path; cached display list excludes animated subtrees via skipAnimatedContent). Prefs: MarqueeEnabled, MarqueeSpeedPercent.
- blink: layout flattens <blink> wrappers (LayoutEngine 298–300); Renderer InsideBlink() checks element ancestry (705); BrowserCanvas._blinkTimer (default 500 ms, BlinkIntervalMilliseconds pref) toggles _blinkVisible. String.prototype.blink() exists in JS. text-decoration: blink accepted by parser.
- bgsound: void element (HtmlParser 1416), display:none (StyleResolver 660), no layout box (LayoutEngine 1224); playback in Form1.Midi.cs — .mid/.wav via Windows MCI, loop attr via LegacyMidi.ParseBgSoundLoop, embed autostart support.
- Animated GIFs: ImageCache AnimationState + AnimatedGifSpeedPercent pref, GPU fast-path repaint.

8. Renderer paint order (Engine/Render/Renderer.cs, 3133 lines)
- Traversal: page background fill → body bg image → PaintBox depth-first. Per box: visibility hidden → skip; blink toggle; frame placeholder; background → border → content (text/images); marquee special-case; overflow clip (padding-box rect); then children via PaintOrder().
- PaintOrder (655–681): children STABLY SORTED by Element.Style.ZIndex then document index (fast path: pre-sorted detection). Hit-testing (LayoutBox.BoxAtPoint) uses the SAME z-then-reverse order. So z-index IS honored per level, but there are NO CSS2 stacking contexts: no z-index-only-on-positioned gating, no negative-z behind parent background (parent bg always paints first), floats/inline content stay in tree order except z-sort. Later siblings paint over earlier ones (the "1996" comment in LayoutBox 130).
- zoom: raster at renderScale (0.25–4 clamp), logical-pixel layout preserved.

Next actions (for the Retro99 upgrade): (a) HTTP/1.1: version string + keep-alive pool + Connection negotiation in HttpClient/broker (Host/chunked already OK); (b) conditional caching (ETag/If-None-Match, Cache-Control) in a new layer + 304 handling; (c) HTTP Refresh header in Form1.ProcessSuccessAsync; (d) CSS2: fixed-position scroll pinning, stacking contexts in Renderer, top/left/etc. already OK; (e) personas: extend RetroEngineMode with IE5/NS4.7 + UA constants + wire document.all/layers/navigator gates; (f) box-sizing switch points identified in §5.

---
Task ID: 3-a
Agent: explore (HTML layer mapping)
Task: Map HTML tokenizer/parser/entities + tag tables so HTML 4.01 additions can be made without guessing. RESEARCH ONLY, no code changes.

Work Log:
- Read HtmlTokenizer.cs (476), HtmlParser.cs (1446), HtmlEntities.cs (288), DomNode.cs (343) in full; surveyed StyleResolver.cs, LayoutEngine.cs, Renderer.cs, DomBindings.cs, JsInterpreter.cs, Form1.cs, FrameLoader.cs, CssParser.cs for tag/display/event/charset/stylesheet handling.

1. HTML 4.0/4.01 element status
- ALREADY: THEAD/TBODY/TFOOT (parser HtmlParser.cs:636-646, row-group-aware recovery :306-314/:873-959; StyleResolver.cs:556-559 TableRowGroup), COLGROUP/COL (HtmlParser.cs:883-890; display:none StyleResolver.cs:580-582), BUTTON (form-pointer HtmlParser.cs:670; InlineBlock StyleResolver.cs:676, outset border LayoutEngine.cs:1150-1157), FIELDSET (block+border LayoutEngine.cs:1123-1128, StyleResolver.cs:468), INS/DEL (underline/linethrough StyleResolver.cs:597-604), IFRAME (BoxType.Frame LayoutEngine.cs:652; Renderer.cs:710 placeholder; Form1.cs:1810+ LoadFramesAsync nested child docs), OBJECT (Replaced LayoutEngine.cs:642; Java host if java classid/code, JavaAppletHost.cs:388-402), SPAN (generic inline, StyleResolver.cs:706 default).
- GENERIC-ONLY (parsed, no semantics): ABBR, ACRONYM (inline, no tooltip styling — only listed in LayoutEngine.cs:1167-1168 fallback), LABEL (inline, no for= focus binding), LEGEND (inline LayoutEngine.cs:1129, no fieldset border inset), Q (case "q": break StyleResolver.cs:648 — NO quote marks are synthesized), BDO (missing: no dir/rtl/bidi support), OPTGROUP (hidden via LayoutEngine.cs:2172 IsNonVisualTag; select rendering InlineLayout.cs:457-461 lists only <option> children — no group labels, no optgroup implied-end rules).

2. IE5/NS4-era tag status
- HANDLED: MARQUEE (block StyleResolver.cs:501; full animation Renderer.cs:792-843: behavior scroll/slide/alternate, direction, scrolldelay, scrollamount, UserSettings toggles), BLINK (TextDecoration.Blink StyleResolver.cs:606; Renderer.cs:997 toggle), NOBR (nowrap StyleResolver.cs:698), WBR (LayoutEngine.cs:1207; break opportunity InlineLayout.cs:303, TableLayout.cs:1328), SPACER (void; full h/v/block sizing LayoutEngine.cs:605-626), MULTICOL (ClosesP HtmlParser.cs:52; block; column layout LayoutEngine.cs:1261), BGSOUND (void HtmlParser.cs:1416, display:none — accepted but SILENT, no audio), LAYER/ILAYER (partial: generic elements, StyleResolver default→inline (no absolute positioning/top/left/clip; LayoutEngine fallback :1210 says block), no NN layer JS API).
- MISSING: COMMENT (IE <comment> = unknown tag, contents VISIBLE; only <!-- --> real comments work, no conditional-comment [if IE] support), RUBY/RT/RP, NOLAYER (content always shown), NOEMBED (content always shown — no reference anywhere), KEYGEN, XML data islands (<?xml…?> PIs become literal text; <xml> element = unknown inline).

3. Master tag table / how to add a tag
- NO enum/central registry. Four cooperating string-switch/HashSet mechanisms, all keyed by lowercased tag names:
  a) HtmlParser.cs: HandleStartTag switch :447-656; IsVoidElement :1412-1418; ClosesP HashSet :46-53; StopBoundaries :56-61; ReconstructibleFormattingTags :70-76; CloseImpliedBefore switch :288-363; IsNonVisual-ish text cases :1236-1273.
  b) StyleResolver.cs ApplyUaDefaults switch :424-712 — THE default display/formatting table (authoritative).
  c) LayoutEngine.cs: FallbackStyleFor switch ~:1010-1230 (synthetic-doc fallback), DetermineBoxType :632-654 (Replaced/Frame), IsNonVisualTag :2165-2174, CreateSpacerBox :605.
  d) DomBindings.cs VoidElements HashSet :76-81 (JS serialization — must stay in sync with HtmlParser.IsVoidElement).
- Recipe for a new tag: (1) usually nothing in parser — default InsertElementNormally pushes it (test UnknownTagsAreKeptAsTransparentBoxes proves unknown tags render as inline boxes); add to IsVoidElement + DomBindings.VoidElements if void; add ClosesP/CloseImpliedBefore rules if it implies ends; (2) add a case in StyleResolver.ApplyUaDefaults for display+formatting defaults; (3) optionally DetermineBoxType (replaced/frame), FallbackStyleFor + IsNonVisualTag for consistency.

4. Intrinsic event attributes
- No recognition list: HtmlParser.Create :194-209 copies ANY attr whose name starts with "on" (len>2, case-insensitive) into DomElement.EventHandlers (key lowercased, "on" kept) — also kept in Attrs. Same for duplicate <body> merges :736-738.
- Execution: JsInterpreter.FireEvent :369-465 — DOM-0 property handlers (el.onclick=fn, "__js_handler__" sentinel) then wrapper props then inline attr source (JsParser.Parse, this=element wrapper, event obj, legacy IE window.event when SupportsInternetExplorerLegacy).
- Actually dispatched by shell: onload (body + any element, Form1.cs:1283-1317), onerror/onload for images (Form1.cs:1765-1771), onclick (BrowserCanvas.cs:9382+), onmouseover/onmouseout (:7384/:7406), onkeydown/onkeypress (:5117/:5357), onfocus/onchange/onblur (:6257/:6307), onsubmit (:10338), select onchange; frames onload Form1.cs:2066. onunload exists only as a window property placeholder (DomBindings.cs:307) — never fired. Mouse event object synthesized per IsMouseEventName JsInterpreter.cs:521-524.

5. Error recovery (HtmlParser.cs unless noted)
- EXISTS: implied end tags BEFORE insert for p/li/dt/dd/tr/td/th/option (CloseImpliedBefore :273-364; HandleTableStructure :873-936, incl. implied <tr> synthesis :906-933); implied html/head/body (EnsureHtml/Head/Body :226-269; head-after-body tolerated :687-715); multiple <body> attr merge (first wins :717-761); frameset replaces empty body (:763-801); </html> ignored; </br>→<br> (:987); stray </p>→empty p (:995); stray end tags ignored (:1033); misnested inline reconstruction for b/big/code/em/font/i/kbd/s/small/strike/strong/sub/sup/tt/u/var (TryReconstructFormattingChain :1083-1124); nested <a> + heading non-nesting + <p> closes through inlines (:338-362); table foster-parenting of TEXT (:1319-1389) and of <form> + form-pointer (:588-633); tokenizer: any case, unquoted values ('/' kept — :441-459), minimized attrs → "" via TryAdd first-wins (:465-472), quoted values entity-decoded, end tags with attrs, bogus comments, raw-text elements (script/style/listing/xmp/plaintext) + RCDATA (textarea/title) with synthetic end tags at EOF (HtmlTokenizer.cs:188-202), document.write token splicing with 200k cap.
- MISSING: implied <tbody> never synthesized (rows land as direct table children — TableLayout copes but tree shape differs); no optgroup/option group recovery; element foster-parenting only for <form> (general in-table text-only); formatting-chain reconstruction limited to the 16 tags above (blocks just nest); minimized attr value is "" not the attr name; no conditional comments.

6. Attribute storage
- DomElement.Attrs: Dictionary<string,string> OrdinalIgnoreCase (DomNode.cs:220) — generic map, everything kept. EventHandlers separate Dictionary (DomNode.cs:227).
- id/class/style functional (inline CSS StyleResolver.cs:310; class/id selectors CssSelector.cs:279-283). title read only by PageInspector/plugin feature host — NOT a tooltip. lang/dir entirely unused (no bidi). tabindex/accesskey NOT implemented (zero engine references).

7. DOCTYPE: DoctypeToken → DomDoctype node + DetermineQuirksMode (HtmlParser.cs:1425-1445) → doc.QuirksMode ∈ html20/html32/strict/quirks — RECORDED ONLY, zero consumers (grep: only DomNode.cs:156 definition + the setter). No mode-based behavior switching.

8. meta http-equiv (HtmlParser.cs:814-842): refresh → doc.MetaRefresh → Form1.cs:1334-1371 timer nav (loop cap 10, FollowMetaRefresh setting; "N" without url ignored since refreshUrl==null); content-type charset → doc.Charset + pre-parse re-decode Form1.cs:1228-1235/BodyDecoder (precedence BOM > HTTP charset > meta; ScanMetaCharset FrameLoader.cs:651 also reads <meta charset=>, first 4KB); set-cookie → CookieStore.Set. Other http-equivs ignored.

9. link rel / style media: <link rel~="stylesheet" href> fetched (HTTP + file://), @import expanded (depth 8), injected as extra <style> at doc end (Form1.cs:1552-1609); StylesheetsEnabled toggle. NO media= filter on <link> (print-only sheets apply); rel="alternate stylesheet" MATCHES the substring check → applied unconditionally (no title/selector UI — alternates are not selectable). <style media> attr ignored (StyleResolver.cs:154-166 parses all <style> text); CSS @media at-rule IS honored (CssParser.cs:167-203: applies screen/all only, `not` first-token check).

10. HtmlEntities.cs: NamedEntities dictionary :21-90 = core + full Latin-1 + common-1996 + large HTML4 symbol set (arrows/math/box?/cards/euro). Numeric decimal+hex, ≤8 digits, with- or without-semicolon (unterminated → longest-prefix match, period behaviour :139-160, :162-185); Windows-1252 C1 remap for 0x80-0x9F :244-280; 0→U+FFFD; surrogates/ >0x10FFFF rejected. MISSING vs full HTML 4.01: Greek letters (alpha…omega), ensp/emsp/thinsp, zwnj/zwj/lrm/rlm, and the remaining ~60 FPI entities.

Stage Summary:
- HTML layer is a 3.2-era tag-soup parser with strong recovery; adding HTML 4.01 elements = mostly StyleResolver.ApplyUaDefaults + parser switch cases; no central registry to extend.
- Highest-value gaps for the 1999 upgrade: OPTGROUP in select rendering, Q quotation marks, BDO dir/bidi, LEGEND rendering, LABEL for=, implied tbody, NOEMBED/NOLAYER/COMMENT concealment, BGSOUND audio, RUBY, alternates/media on link/style, event-attr list is already generic (nothing to add for 4.01 intrinsic events).

---
Task ID: 4-a (Phase A: personas + HTTP/1.1)
Agent: main
Task: 1999 persona system + HTTP/1.1 network upgrade

Work Log:
- UserSettings: RetroEngineMode += InternetExplorer5, Netscape47; UA constants
  (IE5: "Mozilla/4.0 (compatible; MSIE 5.0; Windows 98)", NS4.7: "Mozilla/4.7 [en] (Win98; I)");
  DEFAULT MODE IS NOW InternetExplorer5 (checklist decision #1); EnableHttpCache setting
  (INI Network.HttpCache); Retro96 union UA bumped to 2.0/Win98/IE5+NN4.7
- BrowserRuntime (THE persona contract, consumed by all subsystems):
  IsInternetExplorer5, IsNetscape47, Is1999Persona (IE5||NS4.7||Retro96),
  SupportsInternetExplorerLegacy (+IE5), SupportsNetscapeLegacy (+NS4.7),
  SupportsDocumentAll / SupportsDocumentLayers / SupportsGetElementById (NS4.7 hides it),
  UsesIe5BoxModel (IE5-only quirk gate), Http11Enabled (1999 personas),
  HttpCacheEnabled (1999 && setting), AdvertisesPngImages, VbScriptVersion (5.0),
  JavaScriptEngineName/Version ("Microsoft JScript 5.0 (ES3)" / "Netscape JavaScript 1.3")
- HttpClient: HTTP/1.1 request line + "Connection: keep-alive" in 1999 modes;
  per-origin keep-alive socket pool (ConnectionPool class, 30s idle, stale-socket
  retry-once); HttpValidationCache (ETag/If-None-Match/If-Modified-Since, 304,
  max-age/Expires freshness, no-store/no-cache, LRU 128 entries/16MB; ONLY caches
  responses carrying validators/freshness, never cookie/auth requests);
  Accept now advertises image/png except IE3/NS3; ClearCache() for tests
- PreferencesDialog: 2 new engine modes + HTTP cache checkbox + UA hints
- DomBindings: navigator personas for IE5/NS4.7 (appName/appVersion/appMinorVersion),
  navigator.javaEnabled(), getElementById/createElement/getElementsByTagName now
  gated on SupportsGetElementById

Stage Summary:
- Build green, RetroTests 238/238 on .NET 11 RC1 (default persona = IE5!)
- TestOrigin responses carry no validators → cache never interferes with tests
- Next: Wave 1 parallel agents (VBS 5.0 on VbsTests project, JS/DOM on RetroTests),
  then Wave 2 (CSS2, HTML 4.01), then Phase C integration (layout/UA defaults/
  box model/marquee events/language gating/net8 harness TFM bumps)

---
Task ID: 8
Agent: vbs (Wave 1)
Task: VBScript 1.0 → 5.0 upgrade (Engine/Vbs only)

Work Log:
- Statements 5.0, all in the existing parser/AST/interpreter shape:
  * `With … End With`: new WITH keyword + VbsWithStatement; frame-local With
    stack (VbsFrame.WithStack, outermost first); unqualified `.Member` parses
    in statement AND expression position (VbsWithMemberExpr, gated on parser
    _withDepth); resolution walks the stack INNERMOST FIRST and falls back to
    outer blocks when the inner object lacks the member (documented forgiving
    choice; VBA likely errors); works for get/set/invoke, `Set .X = o`,
    `.Prop(i) = v` indexed form; `With Nothing` → err 91, non-object → 424.
  * `Class … End Class` (CLASS keyword): Public/Private members; fields
    (comma lists, optional array bounds evaluated at construction); methods;
    properties; Class_Initialize/Class_Terminate recognized by name; duplicate
    member names are compile error 1041 (parser-side dict, properties may
    repeat their name across Get/Let/Set); nested class / class-in-procedure
    rejected. Visibility defaults when the keyword is omitted: methods and
    properties Public, fields Private (permissive; documented).
  * `Property Get/Let/Set … End Property` (PROPERTY keyword; Get is a
    contextual identifier, not reserved): Get is function-like — the property
    name is the implicit return variable; Let/Set take the value as LAST
    parameter, so indexed properties work (`obj.Item(i) = v`,
    `Set obj.Item(i) = o`) via a new internal IVbsIndexedPropertyAssign side
    interface implemented only by VbsClassInstance (host objects unaffected).
    `Exit Property` (new VbsExitKind.Property) validates _propertyDepth.
  * `Set x = New Foo` (NEW keyword + VbsNewExpr) — closes the old err-424 gap.
    Undefined class → 424 "Class is not defined". Intrinsic `New RegExp`.
  * `Me` (ME keyword + VbsMeExpr): parser rejects outside class code (1002);
    VbsFrame.Me bound during method/property/accessor invocation; class fields
    are in scope unqualified inside class code (locals → Me fields → globals).
    Private members answered "no such member" (err 438) from outside; a
    MethodDepth counter on the instance gates private access from inside.
  * `Public/Private Sub/Function` at script level now parses (visibility
    meaningless without modules — accepted, not enforced); `Public Property`
    outside a class → clear compile error.
  * Class registry beside _procedures (hoisted, cross-block, also from code
    run via Execute/ExecuteGlobal); class-vs-procedure name clash → 1041.
- Class lifetime: Class_Initialize runs at `New` (fields allocated first);
  Class_Terminate is DETERMINISTIC SESSION-END teardown — VbsSession.Terminate()
  / Dispose() run it for every live instance, newest first, destructor errors
  swallowed (real VBScript COM refcounting is out of scope; documented).
- New file VbsClasses.cs: VbsClass (compiled layout), VbsClassInstance
  (IVbsDispatchObject + IVbsIndexedPropertyAssign; fields via
  TryGet/TrySetMember, Property Get via member-get (zero-arg) or TryInvoke
  (indexed), Let/Set via TrySetMember — Set first when the value is an
  object, zero-arg method invoke on member read like `WScript.Echo
  WScript.ScriptEngine`). TypeName(instance) → class name.
- Builtins (VbsBuiltins + new plumbing):
  * Array(…) fixed 0-based Variant array; Filter(…) include/exclude +
    binary/text compare (Null elements → "", like Join); RGB(r,g,b) → BGR
    packed Long.
  * Eval(exprString): VbsParser.ParseExpressionText compiles one expression;
    evaluated in the CALLER's scope (VbsRuntimeContext.CallerFrame, maintained
    by the interpreter at every builtin call site); `=` stays comparison —
    era rule locked by test. Syntax errors inside re-raised as runtime errors.
  * Execute(statements): compiled and run in the CALLER's frame (procedure
    locals visible/mutable); ExecuteGlobal: global frame. Both register
    procedures/classes declared inside globally. User procs still shadow
    these names (resolution order unchanged).
  * GetRef("proc") → VbsGetRefObject (default-invoke calls the procedure);
    f(x), `f x`, Call f(x), passing as argument all work; unknown name → err 5.
  * Escape/Unescape — JScript-compatible (%XX ≤0xFF, %uXXXX above, malformed
    escapes pass through). ScriptEngine()→"VBScript", Major 5, Minor 0,
    Build 6325 (chosen plausible IE5.0-era build; Win2K's 5.1 reported 5010 —
    documented in VbsBuiltins). Debug.Write (buffers) / Debug.WriteLine
    (flushes through the host) + Debug.Print alias, per-session buffer.
- Err: HelpFile / HelpContext read+write members; Raise now takes
  (number, source, description, helpfile, helpcontext); Clear resets them.
- New file VbsRegExp.cs — VBScript.RegExp 5.0 over System.Text.RegularExpressions:
  Pattern/IgnoreCase/Global get/set; Test(s); Replace(s, repl) with LITERAL
  replacement text (MatchEvaluator bypasses $ substitution — $1..$9 is 5.5),
  count 1 unless Global; Execute(s) → Matches collection (Count, Item(i),
  matches(i), For Each) of Match objects (Value, FirstIndex 0-based, Length).
  Invalid pattern → err 5017 "Syntax error in regular expression" (real
  VBScript regex error range). Pages get one via `Set re = New RegExp`
  (browser host denies CreateObject) and hosts may opt in via the public
  static VbsRegExpObject.TryCreate("VBScript.RegExp") from their
  IVbsScriptHost.CreateObject override (test host does exactly that).
- Version surface: VbsBuiltins.EngineMajor/Minor/Build = 5/0/6325 — drives
  both the new ScriptEngine* globals and the WScript probes; doc comments
  across Engine/Vbs updated 1.0 → 5.0.
- Constants: added the two MSDN-documented missing VarType constants
  (vbDataObject=13, vbDecimal=14); everything else (vbCrLf, MsgBox, weekday,
  compare, triState, vbObjectError …) was already present and untouched.
- ERR registry: 5017 added to VbsErrorNumbers + description table.

Files touched (STRICT ownership respected):
- Modified: retro96/Engine/Vbs/{VbsTokens,VbsAst,VbsParser,VbsInterpreter,
  VbsBuiltins,VbsObjects,VbsErrors,VbsEngine,VbsLexer}.cs (VbsLexer: comment
  only; VbsVariant.cs / VbsArray.cs untouched)
- New: retro96/Engine/Vbs/VbsClasses.cs, retro96/Engine/Vbs/VbsRegExp.cs,
  tests/VbsTests/Vbs50Tests.cs (31 tests)
- VbsTests.csproj unchanged (globs pick up the new files); the shared
  tests/RetroTests/VbsEngineTests.cs unchanged.

Deliberate omissions / documented deviations:
- `Default` keyword (default properties) — not in the checklist;
  TryGet/SetDefault on class instances return false.
- SubMatches and $1..$9 substitution — VBScript 5.5 (2000), checklist says skip.
- Class method/property args are ByVal only (the fixed IVbsDispatchObject
  interface passes values, not cells); script-level procedures keep ByRef.
- Class_Terminate at session teardown, not refcount-0 (documented above).
- Nested With falls back to outer objects (likely VBA/VBS would error).
- Script-level Public/Private on procedures parses but is not enforced.
- `Me` inside Execute/Eval strings → compile error (fresh parser state).
- Runtime-error updates (UpdateErr) don't reset Err.HelpFile/HelpContext.

Stage Summary:
- VbsTests: 54/54 GREEN (baseline 23 kept green + 31 new Vbs50Tests).
- Full Retro96.sln build green — public VBS API only extended (BrowserCanvas
  compiles unchanged; IVbsDispatchObject interface untouched).
- Next actions for later phases: wire VbsSession.Terminate into the browser
  page-teardown path (BrowserCanvas VbsPageState — NOT this task's files);
  optionally VBS event-handler auto-wiring (`Sub name_onclick`) and
  <script for= event=> which were pre-existing gaps outside this checklist.

---
Task ID: 6/7
Agent: JS/DOM implementer
Task: Upgrade the JS/DOM layer from the 1996 DOM-0 surface to the 1999 surface (checklist §10 DOM, §11 Events, §12 JavaScript) on top of the persona contract from Task 4-a.

Files touched (all within my ownership):
- retro96/Engine/Js/JsRuntime.cs     (+316 lines)
- retro96/Engine/Js/JsInterpreter.cs (+425 lines net)
- retro96/Engine/Js/DomBindings.cs   (+1570 lines)
- tests/RetroTests/JsEngineTests.cs  (+816 lines, 32 new [Fact]s; 1 existing
  fact updated — LegacyScriptEngineProbeIsExposed now expects JScript 5/6325)
NOT touched: Engine/Vbs, Engine/Css, Engine/Html, Engine/Layout, Engine/Network,
Engine/Render, RetroTests.csproj, BrowserCanvas.cs, Form1.cs, BrowserRuntime.cs,
UserSettings.cs, TestHelpers.cs, BrowserCanvasStub.cs.

Work Log:

1. DOM Level 1 (§10):
- document.createTextNode(text) → live TextNodeObject wrapping a real DomText
  (nodeType 3, nodeName "#text", nodeValue/data read+WRITE — writes mutate the
  DOM node and repaint). TextNodeObject reworked from a static carrier to a
  live wrapper; detached nodes work until grafted.
- appendChild/insertBefore/removeChild now accept text nodes as well as
  elements (UnwrapNodeArg + TextNodeObject.UnderlyingNode), so
  div.appendChild(document.createTextNode("hi")) really builds DOM.
- replaceChild(new, old) → returns the replaced (old) node per DOM1.
- cloneNode(deep) → recursive clone (attrs + inline event handler sources
  copied, IE behaviour); shallow clone copies no children.
- removeAttribute(name) → removes the attribute; on* attributes also clear
  the interpreter's DOM-0 property store and the element's handler map.
- element.attributes → NamedNodeMap-ish live map: attributes[i].name/.value,
  attributes.length, attributes[name], getNamedItem/setNamedItem/
  removeNamedItem; attribute nodes are {nodeType:2, specified, nodeName/
  nodeValue} with a live writable .value.
- hasChildNodes(), hasAttributes(), ownerDocument (=== document).
- Bonus fix: innerHTML writes on DETACHED elements (createElement+innerHTML,
  the era UI idiom) now parse the fragment against about:blank instead of
  silently discarding it.

2. IE5 DHTML object model (§10, gated SupportsInternetExplorerLegacy):
- insertAdjacentText(position, text) — all 4 positions, inserts DomText.
- currentStyle → read-only CurrentStyleObject over the resolved ComputedStyle
  (color/background*/display/visibility/position/overflow/float/z-index/
  font*/text-*/white-space/line-height/width/height/top/left/right/bottom/
  margins/paddings/borders/list-style) with the same camelCase surface as
  element.style; Set is a no-op (read-only contract); "" before first resolve.
- offsetLeft/offsetTop/offsetWidth/offsetHeight/offsetParent, clientWidth/
  clientHeight — reads LayoutBox (DomElement.Box, border-box metrics) when a
  layout ran, else the resolved ComputedStyle authored lengths (so a styled
  div reports width+padding+border even pre-layout). offsetParent = nearest
  positioned ancestor, body fallback.
- scrollTop/scrollLeft read/write (write stores for readback + repaint).
- style.pixelLeft/pixelTop/pixelWidth/pixelHeight/pixelRight/pixelBottom and
  posLeft/posTop — px reads of inline left/top/width/height, and WRITES land
  as "Npx" inline declarations + reflow (DHTML animation scripts).
- uniqueID — per-element stable "ms__idN" (state.UniqueIds counter).
- document.readyState ("loading" while streaming, "complete" after parse) and
  document.onreadystatechange fired EXACTLY once at the complete transition
  (the post-parse RegisterAll is the transition point).
- setActive() — sets the document's FocusedElement + repaint (era focus
  no-op-ish); also a window-level stub.
- window.showModalDialog(url) — era-modal via the shell's host-modal alert
  service (canvas.ShowAlert), gated ScriptedWindowsAllowed; returns undefined.
- ScriptEngine()/ScriptEngineMajorVersion()/ScriptEngineMinorVersion()/
  ScriptEngineBuildVersion() bumped per persona: IE5+Retro96 → JScript
  5.0/0/6325, IE3 → 1.0/0/0 (NS personas expose none — unchanged).

3. Events — IE5 model (§11):
- element.attachEvent("onclick", fn) / detachEvent — per element+event
  registration list in JsInterpreter (registration order preserved,
  detach removes by function identity).
- FireEvent restructured into: per-element DispatchToElement (DOM-0 property
  → wrapper property → inline attribute (one slot, first present wins) →
  attachEvent registrations in order) + a BUBBLING walk target → ancestors
  for click/dblclick/mousedown/mouseup/mousemove/mouseover/mouseout/keydown/
  keyup/keypress/change (load/unload/submit/reset/focus/blur/error/
  readystatechange stay on target, matching IE).
- event.cancelBubble = true (any truthy) stops the bubble walk AND stops
  remaining attachEvent handlers of the same element.
- event.returnValue = false now CANCELS the default action exactly like a
  literal "return false" (FireEvent returns false; seeded once per dispatch
  so it survives the bubble chain).
- srcElement always references the ORIGINAL dispatch target during
  bubbling; window.event install/restore preserved per handler call
  (nested-dispatch safe). Handler returning boolean true (onMouseOver
  status idiom) is preserved through the combined result.
- event.type is now stamped on every dispatched event object (was missing on
  keyboard events).
- BONUS behavioural note: attachEvent handlers fire after the DOM-0 handler
  on the SAME element (the documented IE ordering) — pages with only DOM-0/
  inline handlers are unaffected (all 238 baseline tests stayed green).

4. Events — NS4 model (§11, gated IsNetscape47/SupportsNetscapeLegacy):
- window.captureEvents(mask)/releaseEvents(mask) with a mask registry in the
  interpreter; captured event types invoke the window's own handler FIRST
  (script-assigned window.handleEvent, else window.on<event>); a
  __dom_builtin__ marker distinguishes the builtin handleEvent stub from a
  script-assigned capture handler (avoids redispatch loops).
- window.Event constants object: MOUSEDOWN..UNLOAD masks (CLICK=4, KEYDOWN=
  0x100 etc.) + SHIFT/CONTROL/ALT/META_MASK modifier constants.
- window.routeEvent(evt) and window.handleEvent(evt) — era-plausible
  re-dispatch stubs to the event's target (documented).
- NS4 event fields stamped at dispatch (persona-gated): e.target (wrapper),
  e.pageX/pageY (clientX + pageXOffset), e.which (key events: char code;
  mouse events: 1-based button), e.modifiers (0 — modifier state not
  tracked). window.pageXOffset/pageYOffset read-only 0 (documented).
- window.onresize NOT wired here (the shell owns resize; Form1 file is not
  mine) — noted as a known gap for the shell owner.

5. NS4 layer model (§10, gated SupportsDocumentLayers — replaces the empty
   stub):
- document.layers — LIVE collection: elements with CSS position:absolute/
  relative (inline STYLE= OR resolved ComputedStyle) plus <layer>/<ilayer>,
  document order, indexable by number AND name (id/name attr); one stable
  collection object + one stable layer object per element.
- Layer objects: left, top, zIndex, visibility ("show"/"hide"/"inherit"
  mapped to CSS visible/hidden/inherit), clip.{left,top,right,bottom}
  (rect() parsed; defaults to box size), bgColor, background (read+write —
  writes go to inline style + reflow/rerender), src (read; write records the
  attribute), document (the shared parent document view — documented choice:
  the engine keeps ONE DOM, so layer.document IS document; nested layer
  lookups still work through it), name/id.
- Methods: moveTo(x,y), moveBy, resizeTo(w,h), resizeBy, moveAbove(layer)/
  moveBelow(layer) (z-index relative ordering), load(url, width).
- Writing left/top REALLY moves the element: inline style position:absolute
  (implied) + left/top px + canvas.ReflowDocument() — the same hook
  innerHTML writes use.

6. ES3 / JScript 5.0 (§12):
- Number.prototype.toFixed(n) — era round-half-away-from-zero on the double's
  true binary value (1.005 → "1.00"), |x| ≥ 1e21 → ToString form.
- Number.prototype.toExponential(n) — "d.ddde+dd", JS-style unpadded signed
  exponent; no-arg form uses shortest round-trip digits + trailing-zero trim.
- Number.prototype.toPrecision(n) — fixed vs exponential switching at the
  e<-6 / exponent ≥ precision thresholds, with rounding-bump re-normalisation
  (99.5 → 2 digits → "1.0e+2").
- RegExp.prototype.compile(pattern, flags) — recompiles in place (source/
  flags/global/ignoreCase/multiline/lastIndex reset), returns the object
  (JScript semantics).
- Date: getUTCFullYear/getUTCMonth/getUTCDate/getUTCDay/getUTCHours/
  getUTCMinutes/getUTCSeconds/getUTCMilliseconds; setFullYear/setMonth/
  setDate/setHours/setMinutes/setSeconds/setMilliseconds (current-component
  preservation + ECMAScript overflow normalization + two-digit year rule);
  toDateString ("Sat Jan 01 2000") and toTimeString ("HH:mm:ss GMT±hhmm").
  getYear() stays year-1900 — Y2K verified: new Date(2000,0,1).getYear()
  === 100, getFullYear() === 2000.
- String.fromCharCode verified present (unchanged).

7. Host objects (§12):
- window.open(url, name, features) — 3-arg form; features parsed-shape
  accepted, window features beyond opening documented-ignored; returns a
  window-like facade {name, closed, length, opener, close(), focus()}.
- window.opener — null by default.
- window.frames[] + window.length — live count/index/named lookup over the
  document's frame/iframe elements (entries are the frame ELEMENT wrappers —
  documented limitation: the real per-frame window objects live in the shell
  (BrowserCanvas.FrameView / Form1.CreateFrameContext) and are not reachable
  from DomBindings without editing files I don't own).
- navigator.plugins[] + navigator.mimeTypes[] — era-flavoured static tables
  per persona (IE5: Shockwave Flash SWFLASH.OCX, Acrobat Control PDF.PDF.1,
  NetShow, WMP; NS4.7: Shockwave Flash NPSWF32.DLL, Netscape Default Plug-in
  NPNUL32.DLL, Acrobat NPPDF32.DLL, LiveAudio, QuickTime, RealPlayer G2) with
  name/filename/description/mimeTypes per plugin, mimeTypes sample entries
  (type/suffixes/description/enabledPlugin back-references) and
  plugins.refresh() no-op. IE3/NS3 personas keep the empty 1996 lists.
- window.onerror now FIRES from every script error path (uncaught throws,
  interpreter errors, parse errors, event-handler and timer exceptions)
  BEFORE the error is surfaced; returning true suppresses the status-bar/
  console report (era semantics); handler exceptions cannot loop the
  reporter.
- window.onerror/onunload/onload slot seeding unchanged.

Documented limitations (deliberate, per mission instructions):
- offset*/client* geometry uses the element's LayoutBox when one exists,
  else resolved-style authored lengths; the layout engine never assigns
  DomElement.Box (boxes are reachable only via LayoutBox.Element from a
  layout root the bindings do not hold) — full box-based offsets need a
  Phase C wiring of the layout root into DocumentBindingsState.
- element scrollTop/scrollLeft writes do not scroll clipped content (no
  per-element scrolling in the engine); value stored for readback + repaint.
- window.pageXOffset/pageYOffset are read-only 0 (shell scroll offset not
  reachable from DomBindings).
- window.showModalDialog does not fetch the dialog page; returns undefined.
- window.open facade: no real cross-window scripting (opener window object
  of a script-opened window is not wired back by the shell).
- window.frames entries are frame ELEMENT wrappers, not cross-frame window
  objects.
- layer.load(url, width) records src/width and reflows; no re-fetch.
- layer "document" is the shared parent document (one-DOM engine).
- Bubbling covers ELEMENT ancestors only (document/window-level handlers are
  not in the chain).
- window.handleEvent builtin is a redispatch stub; the capture model calls a
  script-assigned handleEvent only.

Testing:
- 32 new [Fact]s appended to tests/RetroTests/JsEngineTests.cs covering:
  DOM1 (createTextNode + nodeValue writes, replaceChild, cloneNode deep/
  shallow, removeAttribute incl. handler removal, attributes NamedNodeMap,
  hasChildNodes/hasAttributes/ownerDocument), IE5 (attachEvent ordering +
  detach, returnValue=false cancel, cancelBubble, bubbling, offsetWidth=250/
  clientWidth=240 on a styled div via StyleResolver, currentStyle colour +
  fontSize, insertAdjacentText, uniqueID stable+distinct, readyState +
  onreadystatechange once, showModalDialog, pixel*/pos*, scrollTop/Left
  readback, window.open facade + opener, frames/length, window.onerror fire
  + suppress), NS4 (persona switch: layers present / all+getElementById
  absent / NS UA + appName; layer left write moves the element + moveTo +
  reflow hook + visibility + clip + document + identity; captureEvents/
  releaseEvents + Event constants + window capture firing; e.target/which/
  pageX/pageY/modifiers on synthetic mouse + key events), ES3 (toFixed ×7,
  toExponential/toPrecision ×9, Date Y2K getYear===100 + UTC getters +
  setters + toDateString/toTimeString, RegExp.compile), personas (default
  IE5 UA/appName/javaEnabled, Retro96 union, per-persona plugin tables).
- Persona-switching tests use BrowserRuntime.Apply(new UserSettings{
  EngineMode = ... }) with try/finally restore (new UserSettings() = IE5
  default).
- LegacyScriptEngineProbeIsExposed updated: default persona now reports
  JScript 5.0 build 6325 (was 1.0).

Verification:
- RetroTests: 270/270 green (baseline 238 + 32 new) on .NET 11 RC1.
- Main retro96 project (WinForms shell) builds clean against the changed
  engine (CallHandler gained an optional parameter only — source compatible
  with BrowserCanvas/Form1 call sites).
- tests/JsPageTests fails to compile, but that failure PRE-DATES this task
  (verified via git stash: `using Retro96.Plugins` references in DomBindings
  are not compiled into that project's csproj) — owned by the plugin-bridge
  task, not touched here.

Stage Summary:
- The JS/DOM layer now exposes the full 1999 checklist surface: DOM Level 1
  Core mutation/inspection, the IE5 DHTML object model (geometry, currentStyle,
  pixel*/pos*, uniqueID, readyState, attachEvent with real bubbling and
  returnValue/cancelBubble cancellation), the NS4 layer + capture event
  models gated per persona, the ES3/JScript 5.0 language surface, and the
  era host objects (plugins, opener, frames, onerror, showModalDialog).
- Remaining 1999 JS/DOM-adjacent work lives in other agents' files: window/
  body onresize firing (Form1), real cross-frame window objects (Form1/
  BrowserCanvas), layout-root wiring for box-based offsets (Phase C), and
  the html-websites 1999 QA rewrite (Task 7).

---
Task ID: 5
Agent: css (Wave 2)
Task: CSS1 → CSS2 upgrade (W3C Recommendation, May 1998) — parse + store +
resolve + select + cascade only (layout enforcement is the integrator's job).

Files touched (STRICT ownership respected):
- Modified: retro96/Engine/Css/{CssParser.cs, CssSelector.cs, ComputedStyle.cs,
  StyleResolver.cs}, tests/RetroTests/CssParserTests.cs
- NOT touched: Engine/Html, Engine/Js, Engine/Layout, Engine/Render,
  Engine/Network, Form1.cs, BrowserCanvas.cs, RetroTests.csproj.

Work Log:

1. SELECTORS (checklist §8):
- :first-child — element is the first ELEMENT child of its parent (text/
  comment siblings do not count; PreviousElementSibling reused).
- :lang(xx) — walks self + ancestors for the lang (or xml:lang) attribute,
  case-insensitive hyphen-separated prefix match (:lang(en) matches "en"
  and "en-US", not "enx"). Works in both CssParser.ParseSelectorPart and
  CssSelector.ParseSelector (querySelector path).
- :before / :after / ::before / ::after — the single-colon spellings now
  classify as PseudoElement (they used to parse as a pseudo-class that
  never matched); declarations route to two new FirstLineStyle-like slots
  on ComputedStyle: GeneratedBefore / GeneratedAfter (StyleResolver.
  ApplyPseudoStyle, same clone-apply shape as FirstLineStyle).
  Bonus fix: CssSelector.ParseOneSelector flushed BETWEEN the two colons
  of "::before", emitting a stray empty pseudo-class part (never matched,
  and skewed specificity) — "::" is now one token.
- :link/:visited + :active co-existence verified (they are independent
  pseudo-class checks) and locked by test (CSS2 §5.11.2 change).
- Modern extras (~ sibling, ^= $= *= ~= |= attribute operators) kept and
  locked by a regression test (the union mode wants them).

2. VALUES AND RULES:
- 'inherit' keyword on EVERY property: Apply() gained a parentStyle
  parameter (plumbed from ComputedStyle.Inherit + all three StyleResolver
  call tiers + ApplyPseudoStyle); IsInheritToken intercept routes to a
  new ApplyInherit switch that copies the parent's COMPUTED value —
  including non-inherited properties (border-width: inherit pulls the
  parent's width) and full shorthand groups (border: inherit copies all
  12 sub-values). CssParser.ExpandShorthand passes 'inherit' through
  UNEXPANDED so shorthands inherit as a unit. ComputedStyle-level list of
  ~60 cases (all properties + shorthands).
- @import: the media descriptor after the URL is no longer discarded —
  CssImportRule gained `Media` (IReadOnlyList<string>?, null = all media)
  + `AppliesTo(mediaType)` helper. INTEGRATOR NOTE: Form1.
  ExpandCssImportsAsync (outside CSS ownership) must consult
  import.AppliesTo("screen") before fetching/merging a sheet — print-only
  imports are currently still imported.
- <style media=…> attribute: honored in StyleResolver (Resolve +
  ReadAuthorRules via MediaAppliesToScreen) — non-screen sheets are
  skipped, comma lists containing screen/all apply. Implemented in MY
  files because DomNode.Attrs already carries the attribute; the HTML
  agent should NOT duplicate this in HtmlParser.
- @media: comma lists like "screen, print" apply (verified; print-only
  blocks remain parsed-but-not-applied). Locked by test.
- System colors / #RRGGBBAA / hsl(): still unsupported (out of 1999
  checklist scope, unchanged).

3. PROPERTIES (parse + store + resolve; layout/paint hooks listed in the
   integrator notes below):
- min-width, max-width, min-height, max-height + Min/MaxWidth/HeightPercent
  (lengths now, percentages kept raw for layout like width/height; 'none'
  on max-* = null).
- vertical-align: <length> values (signed, em resolves against parent
  font size) — new ComputedStyle.VerticalAlignLength.
- cursor: all CSS2 keywords + comma URI lists (first url() stored in
  CursorUri, trailing keyword as fallback) + 'hand' → Pointer (IE alias,
  checklist §14a). CursorValue enum; cursor is INHERITED (wired into
  ComputedStyle.Inherit).
- outline / outline-color / outline-style / outline-width: OutlineWidth,
  OutlineStyle (BorderStyleValue), OutlineColor + OutlineColorInvert (the
  CSS2 initial 'invert'); compound shorthand parses in any order.
- border-collapse (Separate|Collapse), border-spacing (one or two
  lengths → BorderSpacingX/Y), table-layout (Auto|Fixed), caption-side
  (Top|Bottom|Left|Right), empty-cells (Show|Hide).
- font-size-adjust (number|none → float?), font-stretch (keyword
  validated, raw string), text-shadow (raw string, parse-level only),
  direction (Ltr/Rtl, inherited), unicode-bidi (raw keyword).
- font shorthand system fonts: caption|icon|menu|message-box|small-caption|
  status-bar → MS Sans Serif 13/13/13/14/11/12px, normal style/variant/
  weight — implemented in BOTH paths (CssParser.ExpandFont + ComputedStyle.
  ParseFontShorthand).
- layer-background-color / layer-background-image (NS4, checklist §10) —
  stored as aliases into BackgroundColor/BackgroundImage so paint works.
- content: parsed into ComputedStyle.Content = List<ContentToken> (record
  (Type, Text)): "string", "attr", "uri", "counter", "counters",
  open/close/no-open/no-close-quote keywords, unknown identifiers kept.
  none/normal → null.
- quotes: List<QuotePair> (open, close) — pairs of quoted strings, odd
  count/none → null; INHERITED (wired into ComputedStyle.Inherit).
- counter-reset / counter-increment: List<CounterAction> (name, value;
  defaults 0 / 1), none → null.
- list-style: an Apply-level shorthand handler was added (the parser's
  inherit passthrough can now hand "list-style" to ComputedStyle whole).
- display: inline-table, run-in, compact added (enum + parser); unknown
  values fall back to Inline (the CSS2 initial value — already the
  ComputedStyle default and the UA switch's fallback).

4. VERIFIED + LOCKED BY TESTS:
- rgb() clamping (ints and percentages, CSS2 §4.3.1 "colour values are
  clipped").
- Specificity: attribute selectors and pseudo-classes in the (c) bucket,
  pseudo-elements in (d), universal/combinators 0 — plus a cascade proof
  ([href] beats a { }).

Testing:
- 27 new [Fact]s appended to tests/RetroTests/CssParserTests.cs (the
  existing Check.That/Check.Done + ParseAndResolve harness style).
- Full suite: RetroTests 297/297 GREEN (baseline 270 + 27 CSS2 tests,
  incl. the 32 JS/DOM tests untouched). VbsTests 54/54 still green.
  Full Retro96.sln build clean, 0 warnings in the engine project.

INTEGRATOR NOTES (Phase C — layout/render/shell work NOT done here):
- LayoutEngine must CLAMP box widths/heights to the new ComputedStyle
  fields: MinWidth/MaxWidth/MinHeight/MaxHeight (float? px) +
  Min/MaxWidth/HeightPercent (float?, resolve against the containing
  block exactly like WidthPercent). Min null = 0; Max null = none.
- vertical-align lengths: InlineLayout should consume
  ComputedStyle.VerticalAlignLength (signed px, in addition to the
  existing VerticalAlignPercent) when shifting inline boxes.
- Generated content: render ComputedStyle.GeneratedBefore / GeneratedAfter
  (full ComputedStyle clones; their .Content token list carries the
  content: value — "string" tokens are literal text, "attr" resolves
  against the element's attribute, "counter"/"counters" need counter
  plumbing, quote keywords need the Quotes list). No layout was wired.
- outline: nothing paints yet — Renderer could draw OutlineWidth/Style/
  Color(Invert) as an out-of-flow ring if the checklist wants it.
- cursor: the shell (BrowserCanvas) should map ComputedStyle.Cursor +
  CursorUri to the actual pointer when hovering; engine only stores.
- Tables: BorderCollapse/BorderSpacingX-Y/TableLayout/CaptionSide/
  EmptyCells are stored only — TableLayout.cs keeps its HTML-attribute
  border/spacing model for now.
- @import media: Form1.ExpandCssImportsAsync must skip imports whose
  AppliesTo("screen") is false (see note above).
- direction/unicode-bidi stored for future BDO/bidi work; text-shadow is
  raw storage only (nobody shipped it in 1999).

---
Task ID: 4
Agent: html (HTML 4.01 parser upgrade)
Task: Upgrade the HTML layer from 3.2 to 4.0/4.01 (W3C Dec 1997 / Dec 1999) per checklist §4-§7, §14 — parser semantics, concealment tags, implied tbody, minimized attrs, entities, DOCTYPE flavours.

Files touched (strict ownership respected):
- retro96/Engine/Html/HtmlEntities.cs
- retro96/Engine/Html/HtmlTokenizer.cs
- retro96/Engine/Html/HtmlParser.cs
- tests/RetroTests/HtmlParserTests.cs

Work Log:
1. ENTITIES (§4) — HtmlEntities.cs: completed the HTML 4.01 named-entity
   appendix on top of the existing core/Latin-1/symbols table: full Greek
   block (Alpha–Omega, alpha–omega, sigmaf, thetasym U+03D1, upsih U+03D2,
   piv U+03D6), ensp/emsp/thinsp (U+2002/3/9), zwnj/zwj/lrm/rlm
   (U+200C–200F), sbquo/bdquo/lsaquo/rsaquo, fnof, weierp, alefsym,
   lArr/uArr/rArr/dArr/hArr (U+21D0–21D4), nsub U+2284 — ~64 new entries;
   the previously present mdash/ndash/lsquo/rsquo/ldquo/rdquo/bull/hellip/
   trade/dagger/Dagger/permil/prime/Prime/oline/frasl/euro/laquo/raquo/copy
   were verified. Longest-prefix unterminated matching and numeric decimal/
   hex/Win-1252-C1 remap untouched (verified no corpus page uses a raw
   new-entity-name prefix — no collision).
2. TOKENIZER (HtmlTokenizer.cs):
   - Minimized attributes now expand to name="name" per the HTML 4.01 SGML
     declaration: <option selected> → selected="selected" (was "").
     Every engine consumer was audited — all use HasAttr presence checks
     (Renderer, FormSubmitter, BrowserCanvas, DomBindings checked/selected/
     disabled/multiple/nowrap), and no test asserted "" — zero fallout.
     An explicit `value=` still yields "".
   - New raw-text elements: COMMENT (IE5), NOEMBED, NOLAYER, XML (IE5 data
     islands) — contents swallowed until the matching end tag (or EOF
     synthetic end tag), never parsed as markup.
   - NOSCRIPT is raw text ONLY while scripting is enabled: Tokenize gained a
     `Tokenize(html, bool scriptingEnabled)` overload (the one-arg form
     delegates with false — PluginContentTransformPolicy and other callers
     unchanged).
3. PARSER (HtmlParser.cs):
   - Scripting gate: Parse now computes ScriptingEnabled = onScript != null
     && BrowserRuntime.ScriptingEnabled (Form1 only hands over an executor
     when the runtime allows scripting; the parser now also honours the
     runtime toggle directly). The tokenizer flag and the document.write
     splice path both use the same value. When scripting is DISABLED,
     <noscript> content parses as ordinary markup and renders (verified by
     test with BrowserRuntime.Apply toggle + try/finally restore).
   - OPTGROUP (§14): implied end before the next optgroup (groups are
     select children, never nested); an open OPTION also closes through
     stray inline elements (PopUpTo w/ select+optgroup stops); OPTION label
     attr accepted (generic attr storage). No select content-model
     foster-parenting exists, so optgroup children stay options.
   - IMPLIED TBODY (§4 error recovery): a <tr> (or <td>/<th> via the
     implied-<tr> synthesis) landing while the insertion point is <table>
     itself now opens a synthesized <tbody> row group; subsequent rows nest
     inside it. Rows after a CLOSED group start a fresh implied tbody.
     Companion fix: a thead/tbody/tfoot/caption/colgroup start tag implies
     the end of an OPEN row group (CloseOpenRowGroup) — without it,
     <table><tr>…<tfoot> nested tfoot inside the implied tbody and
     TableLayout's row-group bucketing would drop the footer rows.
     Layout-neutrality verified: TableLayout.BuildRowList buckets both
     loose rows and row-group children into the same body stream.
   - KEYGEN + SERVER added to parser IsVoidElement (parse and ignore, no
     content, following content stays a sibling).
   - HandleText: comment/noembed/nolayer content is DROPPED entirely
     (parser-level display:none equivalent — nothing can ever render or
     leak into InnerText); noscript/xml raw text is kept verbatim as a text
     child (noscript subtree is hidden by StyleResolver when
     doc.ScriptingEnabled; xml payload is the document.all(id).innerHTML
     surface).
   - DetermineQuirksMode: HTML 4.0/4.01 now distinguishes DTD flavours —
     Strict (with or without system id) → "strict"; Transitional/Loose/
     Frameset → "quirks" (period-browser behaviour). DomDoctype node keeps
     the full raw text incl. public identifier.
   - ABBR/ACRONYM/Q/LABEL/LEGEND/BDO/RUBY/RT/RP/SPAN/INS/DEL: no parser
     switch needed — the default InsertElementNormally path already yields
     transparent inline boxes with all attrs preserved (dir/lang/xml:lang/
     for/title/cite/label wrap-and-associate structure). All covered by
     new tests.
4. TESTS — HtmlParserTests.cs: +18 [Fact]s, 297 → 315 total, all green
   (full suite re-run twice; whole Retro96.sln also builds clean):
   IeCommentElementContentsNeverRender, NoembedAndNolayerContentsNeverRender,
   NoscriptConcealedWhenScriptingEnabledRenderedWhenDisabled (BrowserRuntime
   toggle + restore pattern, mirrors the Form1 executor wiring),
   XmlDataIslandKeptAsRawTextChild, KeygenAndServerParseAsVoidElements,
   ImpliedTbodyWrapsRowsWrittenDirectlyInTable, RowGroupStartClosesOpen-
   RowGroupInsteadOfNesting, MinimizedAttributesExpandToNameEqualsName,
   LangDirTabindexAccesskeyRoundTripThroughParser, BdoDirAttrPreserved,
   NewHtml401InlineElementsParse (abbr/acronym/q/span/ins/del/label/legend/
   fieldset), RubyRtRpParseAsInlineElements, OptgroupImpliedEndsKeepOptions-
   InTheirGroups, Html401GreekEntitiesDecode, Html401SpecialAndSymbol-
   EntitiesDecode, Doctype401StrictTransitionalFramesetRecorded,
   PlaintextAndXmpAreRawText, TextareaWrapAndBasefontColorPreserved.
   Changed assertions in PRE-EXISTING tests: NONE (the minimized-attr fix
   broke no existing assertion; one new-test assertion was inverted during
   authoring and fixed — xmp entities stay literal).
   Behaviour note: DOCTYPE-containing documents now report
   quirks for 4.01 Transitional/Frameset where the old code said strict —
   zero consumers of QuirksMode exist, so no test impact.

INTEGRATOR NOTES (StyleResolver.ApplyUaDefaults / LayoutEngine /
DomBindings / shell — all OUTSIDE this task's file ownership):
- StyleResolver ApplyUaDefaults display entries still needed:
  • comment, noembed, nolayer → DisplayValue.None (contents are already
    dropped by the parser, so this only conceals the empty elements
    themselves; also worth adding them to LayoutEngine.IsNonVisualTag and
    the DomNode.InnerText skip list alongside style/script/noscript for
    JS-visible innerText parity).
  • xml → DisplayValue.None (data island element: payload IS kept as a raw
    text child for document.all(id).innerHTML — without this UA default
    the XML source renders as visible text!).
  • ruby/rt/rp → inline (ruby annotation rendering — rt smaller font,
    rp hidden when ruby supported — is the renderer's call).
  • abbr/acronym → inline (presentational default is plain inline; tooltip
    from title attr optional), q → inline + synthesized quote marks
    („/” or “/” per Quotes — parser keeps cite attr), bdo → inline with
    dir=rtl → Unicode bidi (needs direction/unicode-bidi wiring the CSS2
    task already stored), legend → inline styled as the fieldset caption
    (bold, inset into the fieldset border).
  • optgroup → display:none is already in LayoutEngine.IsNonVisualTag, but
    select rendering (InlineLayout.RenderSelect lists only <option>
    children) needs group-label rows + option indentation, and options
    inside an optgroup with label attr should show the label text.
  • label → inline; label[for=id] association + wrapped-label association
    are DOM-derivable at click time (for → document-wide id lookup on the
    label's OwnerDocument; wrapped → first labelable descendant
    input/select/textarea/button) — no parser wiring was added because
    DomElement cannot gain new fields under this task's ownership; the
    attrs (for) and descendant structure are guaranteed by tests.
  • keygen → DisplayValue.None (void, no rendering; form submission of the
    challenge is out of 1999 scope).
  • LAYER/ILAYER: attrs (src/name/above/below/visibility/clip/bgcolor/
    left/top/z-index) are preserved generically by the parser; the
    integrator owns absolute positioning + display:block-ish defaults.
- DomBindings.cs VoidElements HashSet (JS serialization) must be synced
  with the parser's IsVoidElement: add "keygen" and "server".
- DomBindings innerHTML/outerHTML serializers may want comment/xml to
  emit their raw text children verbatim (like script/style) if IE5 DOM
  parity for those elements is desired.
- LABEL→control focus routing (click on label focuses/activates the
  control) and accesskey handling are BrowserCanvas/shell surfaces; the
  parser guarantees tabindex/accesskey/for attrs round-trip.
- Select rendering of OPTGROUP (labels + indentation) is LayoutEngine/
  InlineLayout territory per the task split.

Stage Summary:
- HTML layer is at 4.01 parse fidelity: full entity appendix, 4.01 DTD
  flavour recording, implied tbody, optgroup recovery, minimized-attr
  expansion, and period concealment semantics for comment/noembed/
  nolayer/noscript-on/xml islands/keygen/server.
- RetroTests 315/315 green (baseline 297 + 18 new); Retro96.sln builds
  clean; no existing assertion needed changing.

---
Task ID: 9
Agent: layout-integrator
Task: Layout + render integration of the 1999 CSS2/HTML4.01 features (wire
ComputedStyle fields the CSS agent added, the HTML agent's optgroup trees,
and the JS agent's DomElement→LayoutBox geometry need into the layout and
render layers).

Files touched (strict ownership respected):
- retro96/Engine/Dom/DomNode.cs           (+7 lines — ONLY the LayoutBox
  property; nothing else in the file changed)
- retro96/Engine/Layout/LayoutEngine.cs   (+ ~230 lines)
- retro96/Engine/Layout/InlineLayout.cs   (+ ~70 lines)
- retro96/Engine/Layout/TableLayout.cs    (+ ~110 lines)
- retro96/Engine/Render/Renderer.cs       (+ ~140 lines)
- tests/RetroTests/EngineRegressionTests.cs (+14 [Fact]s, 315 → 329 total)
NOT touched: Engine/Css, Engine/Js, Engine/Html, Engine/Network,
Engine/Forms, RetroTests.csproj, Form1.cs, BrowserCanvas.cs, UserSettings.cs,
any other test file.

Work Log:

1. DomElement ↔ LayoutBox wiring (unblocks real offsetWidth/offsetHeight):
- DomElement gained the canonical `public LayoutBox? LayoutBox { get;
  internal set; }` (ONLY change in DomNode.cs). The layout engine ALSO
  populates the pre-existing (previously never-assigned) `DomElement.Box`
  slot, because DomBindings.OffsetMetrics/ClientMetrics read `.Box` —
  assigning it is what actually makes JS offsetWidth/offsetHeight/
  clientWidth real instead of style-fallback estimates. Both stay in sync
  until DomBindings migrates to `.LayoutBox` (note for main agent).
- LayoutEngine.BuildLayoutTree now clears every element's Box/LayoutBox at
  the top of each pass (full reassign — layout rebuilds trees), then
  GenerateBoxes publishes the PRINCIPAL box at construction (only there —
  text-run fragments/first-letter splits keep the element as Element but
  never overwrite the reference). BuildBodyBox, CreateSpacerBox and
  LayoutFrameset (+ nested framesets and frame boxes) publish through the
  shared PublishBox helper.
- Freshness: re-layout publishes a fresh box; an element whose box is
  DROPPED (display:none) goes back to null. Documented limitation: an
  element fully DETACHED from the document between passes keeps its last
  box (the clear walk only reaches document descendants; the JS wrappers
  keep detached elements reachable, but nothing re-clears them — era-simple).
- JS-level effect verified: offsetWidth now returns the real border-box
  width (220 for width:220px+padding+border under the default IE5 persona),
  clientWidth the border-box minus borders. The pre-layout STYLE fallback
  in DomBindings (used when no layout ran, e.g. the pre-existing
  JsEngineTests.OffsetGeometryFromStyledDiv) still computes 250
  (authored+padding+border) and is persona-blind — that pre-existing test
  needed NO change because it never runs layout.

2. IE5 box-model quirk (checklist §9 — gated on BrowserRuntime.UsesIe5BoxModel,
   i.e. ON in the default IE5 persona):
- Authored CSS pixel width/height now interpret the authored value as the
  BORDER box in GenerateBoxes: content = authored − padding − border
  (AuthoredContentExtent helper). Height likewise.
- img quirk implemented (cheap): for an img with an authored CSS width/
  height the box's padding is dropped entirely, so the img lays out as
  margin+border+width+border+margin exactly.
- NOT double-subtracted (verified): percentage CSS widths (StyleWidthPercent
  path already resolves border-box-ish in ResolveAutoWidth — its formula is
  model-neutral), HTML width/height ATTRIBUTES (px attr path unchanged,
  % attr path already subtracts chrome), auto widths (auto stretch yields
  border-box = container − margins in both models — unchanged).
- min/max clamping interplays with the quirk: in IE5 mode min/max clamp the
  BORDER-box extent and convert back; in W3C mode they clamp the content
  extent (spec semantics).
- CHANGED PERSONA BEHAVIOUR, NOT A REGRESSION: the default persona is IE5,
  so every existing layout test now runs under the quirks model. Audit of
  the suite found NO existing assertion that encodes CSS-px-width content-box
  expectations together with padding/border (the two NamedBug border-shading
  tests probe INSIDE the authored border strip — pass under both models;
  JsEngineTests.OffsetGeometryFromStyledDiv never lays out — see above).
  Zero existing assertions changed; baseline stayed 315/315 green.

3. CSS2 min/max-width/height clamping:
- ClampWidthToBounds / ClampHeightToBounds: resolve px + percent bounds
  against the containing block (percent like width %), clamp after width
  resolution. Applied (a) in GenerateBoxes for extents the box already
  carries (authored CSS / HTML attr / natural image size — covers inline
  replaced boxes that never reach ResolveAutoWidth) and (b) at the END of
  ResolveAutoWidth (block layout — after the auto stretch; covers block +
  absolute paths since LayoutBlockChildren routes absolute children through
  LayoutBlock) and after LayoutBlockChildren for heights (auto heights
  clamp only after content, so a min-height floor is never mistaken for an
  authored height and never caps taller content — locked by test).
- The clamp is idempotent (GenerateBoxes + block re-clamp agree).
- The anonymous-block width in LayoutBlockChildren needs no IE5 variant
  (anonymous boxes never carry padding/border; documented in the code).
- Tables/cells: cell widths come from the table grid — CSS min/max on table
  cells is not applied (documented gap).

4. Generated content (:before/:after):
- LayoutEngine.GenerateBoxes emits the GeneratedBefore/GeneratedAfter
  content as synthetic INLINE TEXT RUNS at the element's start/end (works
  for flattened inline wrappers like <q>/<span> AND for block elements —
  the runs land in the element's child list and flow through
  NormaliseAndAttach/InlineLayout like any text). The run keeps the element
  as its Element (ancestry/hit-testing) and carries the pseudo-element's
  ComputedStyle as StyleOverride — the same mechanism first-letter
  fragments use — so `:before { content:"x"; color:red }` paints red and
  measures with the pseudo style (MeasureBox/PaintContent both consult
  StyleOverride).
- Renderable tokens: "string" → literal text; attr(x) → the element's
  attribute value. counter()/counters(), the open/close-quote keywords and
  url() are SKIPPED (no counter registry / quote-depth tracking / generated
  images — documented simplifications). content:none/normal already resolve
  to a null token list (nothing emitted).
- Table elements: generated runs land in the table's children and are
  ignored by BuildRowList (no crash; no rendering — acceptable).
- Q note for the main agent: the StyleResolver UA seed for <q>
  (q:before{content:'“'} etc.) is NOT in yet (case "q": break as of this
  writing). The moment it is seeded, <q> renders quotes through this
  mechanism with zero further layout changes. My test uses author rules
  (#t:before{content:"["}, :after{content:attr(suffix)}) so it is
  independent of the UA default.

5. Outline painting (Renderer):
- PaintOutline runs right after PaintBorder in PaintBox. Solid stroke ring
  around the border box with a fixed 2px offset (outline-offset is CSS2.1 —
  the era engines used a fixed gap), stroke width = OutlineWidth, colour =
  OutlineColor; OutlineColorInvert ('invert', the CSS2 initial) → BLACK
  (true pixel inversion out of scope — documented). Dotted/dashed outline
  styles fall back to solid (documented). Out-of-flow by construction:
  never affects layout.

6. OPTGROUP rendering in selects:
- New public SelectRowModel (InlineLayout.cs): builds the row list from the
  select's DIRECT children — optgroup → a non-selectable header row
  (LABEL attr, else the group's direct text content) + its option rows
  indented by SelectRowModel.GroupIndent (12px); loose options → plain
  rows. Shared by: Renderer.PaintSelect (listbox mode renders headers
  bold-ish — weight-700 of the control font — with no selection band and
  no hit-target, options indented, the navy selection band also indents),
  InlineLayout.ControlNaturalSize (natural select width now includes the
  group label width + option indent), and — intended for the shell (see
  notes) — listbox click hit-testing.
- The dropdown (closed face) path still shows the selected OPTION text
  (group membership irrelevant on the closed face).
- Scroll math (maxScroll, scrollbar) now counts ROWS (headers included).

7. border-collapse / border-spacing (TableLayout):
- TableLayout reads the table's ComputedStyle: BorderCollapse == collapse →
  cellSpacingX/Y forced to 0 AND the era shared-edge approximation for cell
  borders: each shared edge is drawn ONCE by the cell on its LEADING side
  (left/top borders always survive; right/bottom borders only on the grid
  boundary — last column / last spanned row). No border width conflict
  resolution (the leading cell's border wins — documented simplification
  vs. the spec's "wider border wins").
- Separate mode: authored CSS border-spacing wins (BorderSpacingX/Y — a
  stored 0 is indistinguishable from "not authored" because ComputedStyle
  has no authored flag for it, so a lone `border-spacing: 0` falls back to
  the attribute default — documented limitation); else the NN CELLSPACING
  attribute (default 2). The single spacing value was split into X and Y
  (border-spacing: 5px 10px works; horizontal uses X everywhere, vertical
  uses Y everywhere — grid top, row advance, rowspan spans, caption gap,
  total height).
- empty-cells:hide only applies in the SEPARATE model (a collapsed table
  keeps its grid — see 9).

8. table-layout: fixed (TableLayout):
- Trigger: ComputedStyle.TableLayout == Fixed AND the table has an authored
  width — HTML attr, CSS px width, or CSS percent width. (CSS width on the
  table is honoured as the BORDER-BOX target only in the fixed algorithm;
  the auto algorithm keeps its attribute/shrink-to-fit behaviour — a CSS
  pixel width on an AUTO table remains a documented gap.)
- BuildFixedColumns: <col width> (span-aware, incl. <colgroup> wrappers)
  and first-row cell widths (HTML attr or authored CSS px; a colspan cell
  divides its width evenly across its span) pin columns; everything
  unspecified splits the remaining inner width EQUALLY. Content-based
  min/pref measurement still runs but is deliberately ignored (no
  content-based auto sizing).

9. caption-side + empty-cells:
- IsCaptionAtBottom: CSS caption-side wins over the legacy ALIGN=bottom
  attribute; caption-side: left/right falls back to TOP (side captions are
  not laid out — documented).
- ShouldConcealEmptyCell (Renderer): a fully empty cell (no child boxes)
  paints neither background nor border when empty-cells:hide. The property
  is read from the cell OR the owning TABLE (the common authoring position
  — empty-cells is not wired into ComputedStyle.Inherit, so a table-level
  declaration never reaches the cells' computed styles); collapse overrides
  (shared grid lines stay).

10. vertical-align <length> (InlineLayout.FlushLine):
- ComputedStyle.VerticalAlignLength shifts the baseline placement (px,
  positive = raise — box.Y -= length), stacked after the super/sub and
  percent adjustments, mirroring the existing keyword/percent paths.

11. Marquee script events (checklist §11):
- New `internal static Action<DomElement, string>? Renderer.MarqueeEventHook`
  (shell-installed bridge; the render layer cannot call the interpreter).
  Invoked with DOM-0 handler spellings: "onstart" — fired once per marquee
  epoch (the first animation query of the element, i.e. the actual start of
  the scroll, any behavior); "onbounce" — alternate mode, once per
  direction reversal (turnaround counter, not per paint frame);
  "onfinish" — slide mode, exactly once when the single traversal reaches
  the resting edge. Exceptions in the hook are caught + logged (a faulty
  hook can never break painting). Note for the main agent: wire
  JsInterpreter.FireEvent into this hook from BrowserCanvas/Form1.

12. Cursor + bidi: Cursor/CursorUri remain stored-only — the shell exposes
    NO cursor-setting hook reachable from the Renderer (BrowserCanvas is
    outside this task's ownership) → skipped, see notes. direction/
    unicode-bidi stay store-only per the CSS agent's notes (BDO/rtl needs a
    real text shaper — out of 1999 scope here).

Testing (all appended to tests/RetroTests/EngineRegressionTests.cs, using
the existing LayoutHarness patterns):
- 14 new [Fact]s: LayoutBoxWiringPublishesPrincipalBox (publication +
  fresh-reassign + display:none stale-clear), Ie5BoxModelPersonaTreats-
  AuthoredWidthAsBorderBox (IE5 border-box incl. padding+border + the img
  padding-less quirk + union-persona W3C 230px — BrowserRuntime.Apply in
  try/finally with new UserSettings() restore), OffsetWidthReadsTheReal-
  LayoutBox (JS-level offsetWidth/clientWidth via PageHarness + layout),
  MinMaxWidthHeightClampBlockSizes (max/min/percent + heights),
  MinMaxConstraintsNeverOverrideLargerResolvedSizes (auto width beats a
  smaller min-width; min-height never caps taller content),
  GeneratedBeforeAfterContentFlowsAsTextRuns (string + attr tokens, run
  order, block + inline elements), OutlinePaintsRingAroundBorderBox (pixel
  probes: authored red ring 2px out, control clean, invert→black),
  OptgroupRowsRenderAsHeadersWithIndentedOptions (row model + rendered
  indent geometry: grouped option ≥10px right of the header label, loose
  option aligned), BorderCollapseForcesZeroCellSpacing (gap 2 default /
  0 collapse / 9 authored border-spacing), TableLayoutFixedDistributesBy-
  SpecifiedColumnsOnly (100/200 split, colgroup 80/320, content ignored),
  VerticalAlignLengthShiftsBaselineByPixels (±8px → 16px apart),
  CaptionSideBottomPlacesCaptionBelowTheGrid, EmptyCellsHideSuppresses-
  EmptyCellPainting (pixel probes hide vs show), MarqueeEventHookFires-
  StartBounceFinish (synthetic clock through GetMarqueeTranslationX:
  onstart ×2, onbounce ×1, onfinish ×1, hook restored in finally).
- Full suite: RetroTests 329/329 GREEN (baseline 315 + 14 new; baseline
  verified green BEFORE any change and re-verified after every stage).
  Whole Retro96.sln builds clean (0 warnings). VbsTests untouched.
- Changed assertions in PRE-EXISTING tests: NONE (the IE5 persona quirk is
  ON by default for every test and broke nothing — see 2 for the audit).
  One NEW-test assertion was tuned during authoring (empty-cells "show"
  probe threshold 128-grey).

Simplifications (documented in code comments):
- border-collapse: collapse = leading-edge border model (no width conflict
  resolution); vertical row seams come from the following row's top border.
- border-spacing authored-0 indistinguishable from unset (no authored flag
  on ComputedStyle — outside my ownership to add).
- outline: solid-only rendering; invert → black; fixed 2px offset.
- generated content: counters/quotes/url skipped; table-level :before/:after
  content dropped by the row/cell grid.
- CSS min/max not applied to table cells (grid owns cell widths); CSS pixel
  width on an AUTO (non-fixed) table still ignored.
- caption-side left/right → top fallback.
- Detached DOM elements keep their last box until GC (full reassign only
  clears document descendants).
- img IE5 quirk: padding zeroed only when a CSS width/height is authored.

NOTES FOR THE MAIN AGENT (files outside my ownership):
1. BrowserCanvas.SelectListBoxOption (~:9596) still maps clicks to the FLAT
   option list with row math `index = scroll + (y-contentY)/rowH` — with
   optgroup headers rendered, the row indexes shift and clicks can select
   the wrong option (and must SKIP header rows). Consume
   Engine.Layout.SelectRowModel.Build(select) there: walk its rows with the
   same rowH math, ignore IsGroupHeader rows, map Option rows. Same for
   GetSelectVisibleRows/scroll bounds (rows.Count, not options.Count) and
   ShowSelectDropdown (~:9985) if group labels should appear in the
   dropdown popup (the WinForms ListBox would need non-selectable group
   items or owner-draw).
2. Renderer.MarqueeEventHook is ready to install: from BrowserCanvas/Form1
   set `Renderer.MarqueeEventHook = (el, name) => _jsInterpreter?.FireEvent(el,
   name);` (FireEvent accepts the DOM-0 "onstart"/"onbounce"/"onfinish"
   spellings used by inline attributes). The hook fires from the paint/
   animation thread — marshal to the UI thread if needed.
3. Cursor: ComputedStyle.Cursor/CursorUri are still unconsumed. A
   Func<DomElement?, CursorValue/string?>-style resolver on Renderer (or a
   BrowserCanvas hover handler walking HitTester boxes and reading
   element.Style.Cursor) would map CSS cursor → the WinForms pointer; the
   engine side needs nothing further.
4. StyleResolver UA seeds still open (your file): q:before/:after quote
   content (mechanism ready — see 4), ruby/rt/rp + legend + label +
   abbr/acronym display entries, optgroup→display:none is already covered
   by LayoutEngine.IsNonVisualTag, comment/noembed/nolayer/xml → none.
5. DomBindings offset*/client* should eventually migrate from the legacy
   DomElement.Box to DomElement.LayoutBox (both are kept in sync by the
   layout engine; Box has a public setter, LayoutBox is internal-set).
6. The pre-layout offsetWidth fallback in DomBindings is persona-blind
   (reports W3C arithmetic even in IE5 mode); once a layout has run the
   real box reflects the persona. Optionally gate the fallback on
   BrowserRuntime.UsesIe5BoxModel for parity.

Stage Summary:
- The layout/render layers now consume the CSS2 computed-style surface:
  quirks-mode box model per persona, min/max clamping, generated content,
  outlines, optgroup rendering, collapsed borders + border-spacing, fixed
  table layout, caption-side, empty-cells, vertical-align lengths, and a
  marquee script-event bridge. DomElement→LayoutBox wiring makes JS
  geometry real. RetroTests 329/329 green; full solution builds clean.

---
Task ID: 4-b/9-b/10/11 (Phase C integration + 1999 suite + fixes)
Agent: main
Task: shell wiring, harness migration, html-websites 1999 rewrite, cross-cutting fixes

Work Log:
- StyleResolver UA defaults: ruby/rt (half-size inline), rp/xml/comment/noembed/nolayer/
  keygen/server display:none, Q UA-generated quote marks (content tokens), legend block
  inset, body margins persona-gated (IE 10/15, NS 8)
- DomBindings.VoidElements synced (keygen, server); navigator personas for IE5/NS4.7
  (appName/appVersion/appMinorVersion) + navigator.javaEnabled()
- HtmlParser: script language version gating per persona (NS4.7 ceiling 1.3, NS3 1.1);
  fixed gating digit index bug (language[12] not [11])
- ENGINE BUG FIXED: CssParser's type/id/class/pseudo scanners did not stop at '[' —
  attribute selectors glued into type names and never matched in stylesheets (the
  ParseSelector path worked; the stylesheet path didn't). Repaired + test literal that
  had been corrupted to "ref]" restored to "a[href]"
- ENGINE FIXED: ES3 null/undefined property access now throws a catchable TypeError
  ("'x' is null or not an object") — JsEngineTests contract updated to era behaviour
- ENGINE FIXED: layer clip object cached per wrapper (NS4 clip writes now persist);
  offsetParent falls back to BODY (IE5 contract); nodeName/tagName uppercase (DOM1 HTML)
- Form1: @import media descriptors honored (print-only sheets skipped), <link media=...>
  filtered, HTTP Refresh header handled like meta refresh
- BrowserCanvas: VbsSession.Terminate() on page teardown (Class_Terminate at unload),
  Renderer.MarqueeEventHook wired (onstart/onbounce/onfinish fire through FireEvent on
  the UI thread), SelectListBoxOption optgroup-aware (SelectRowModel, headers skipped,
  range selection in option space)
- InlineLayout: dropped leading/collapsed spaces anchored to container origin
  (was stale 0,0,0,0 rects — LayoutLab hygiene checks now pass)
- Harness migration: JsPageTests/LayoutLab/VisualDiff net8.0 → net11.0 + SkiaSharp
  4.153.1, full engine compile sets synced, stubs extended (canvas stub surface,
  DebugLog members), upstream-broken FromImage→FromBitmap fixed in EngineRig
- JsPageTests repaired (upstream expectations didn't match the testdata pages):
  Acme rollover tests rewritten to the real page contracts + new NS4.7-persona pass
- tests/html-websites REWRITTEN as the 1999 suite: 20 pages (html401, broken-html,
  forms, tables, frames+3 children, css2-selectors, css2-boxmodel, dom-level1,
  dom-ie5-dhtml, dom-ns4-layers, events, es3, vbscript5, ie5-extras, ns4-extras,
  images-media, http11, acid1, y2k) + test-index + README.txt + TEST-MAP.md + generated
  audio fixtures (tada.wav, theme.mid); kept java/ + javascript-basic.html (test deps)
- tests/RetroTests/Retro99SuiteTests.cs: 34 headless checks per §17 (parse→style→layout
  per page, script contracts, persona switches, HTTP/1.1 keep-alive + 304 loopback test,
  Acid1 ink, Y2K contracts, language gating)

Stage Summary:
- FINAL: solution builds; RetroTests 363/363, VbsTests 54/54, JsPageTests 28/28,
  LayoutLab ALL PASS — all on .NET 11 RC1 on Linux
- README.md updated to the 1999 story
