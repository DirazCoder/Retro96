# Retro96 → 1999 QA map

One page per checklist section of the 1996→1999 upgrade (see
`upload/retro99-upgrade-checklist.md` §4–§16), plus the §17 test-plan
specials. `test-index.html` links everything.

| Checklist § | Area | Page | Headless assertions |
| --- | --- | --- | --- |
| 4 | HTML 4.01 elements | `html/html401-full.html` | Retro99SuiteTests: parse + tree + concealment + entities |
| 4/17 | Error recovery | `html/broken-html-1999.html` | Retro99SuiteTests: recovery rules hold on tag soup |
| 5 | Forms 4.01 | `forms/forms-1999.html` | Retro99SuiteTests: control tree + attrs |
| 6 | Tables | `tables/tables-1999.html` | Retro99SuiteTests: row groups, CSS2 table props |
| 7 | Frames/iframe/object | `frames/frames-1999.html` (+ 4 children) | Retro99SuiteTests: frameset contract, iframe box |
| 8 | CSS2 selectors | `css/css2-selectors.html` | Retro99SuiteTests: selector outcomes |
| 9 | Layout/box model | `css/css2-boxmodel.html` | Retro99SuiteTests: z-order, min/max, persona widths |
| 10 | DOM Level 1 | `dom/dom-level1.html` | Retro99SuiteTests: script contracts |
| 10 | IE5 DHTML | `dom/dom-ie5-dhtml.html` | Retro99SuiteTests: all/innerHTML/offset contracts |
| 10 | NS4 layers | `dom/dom-ns4-layers.html` | Retro99SuiteTests: NS4.7 persona layer contracts |
| 11 | Events | `events/events-1999.html` | Retro99SuiteTests: attachEvent, returnValue, capture |
| 12 | JS/JScript | `javascript/es3-1999.html` | Retro99SuiteTests: ES3 report + version gating |
| 13 | VBScript 5.0 | `vbscript/vbscript5.html` | VbsTests (54) + Vbs50Tests coverage |
| 14a | IE5 extras | `ie/ie5-extras.html` | Retro99SuiteTests: marquee/legend/ruby/xml island |
| 14b | NS4 extras | `netscape/ns4-extras.html` | Retro99SuiteTests: layer/multicol/spacer parse |
| 15 | Images/media | `images/images-media.html` | Retro99SuiteTests: decode + lowsrc + maps |
| 16 | HTTP/1.1 | `network/http11-page.html` | Retro99SuiteTests: HttpClient contract loopback |
| 17 | Acid1 | `acid1.html` | Retro99SuiteTests: mosaic renders, no seams = boxes exist |
| 17 | Y2K | `y2k.html` | Retro99SuiteTests: getYear=100/fullYear=2000/cookie |
| — | Regression (1996) | `testdata/` + `javascript/javascript-basic.html` + `java/` | existing EngineRegressionTests / JsEngineTests / JavaEngineTests |
