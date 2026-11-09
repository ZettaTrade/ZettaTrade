# ZettaTrade — accessibility audit (canvas widgets, naming, focus, contrast)

This document is created by **T-110** (accessibility pass). It records what the pass changed,
what the shipped tests certify, and — plainly — what still is not covered. Everything a
screen reader or the keyboard meets in this build is named through the localization catalog
(10 `A11y` keys in both `Strings.resx` and `Strings.ru.resx`; `scripts/check-resx.ps1` verifies
the full 563/563 parity), so switching language changes nothing structurally.

## What canvas widgets now serve to assistive tech

Avalonia's base `Control` serves the automation-none peer: a Skia drawn surface would be
fully invisible to the UIA tree. Every canvas widget now overrides `OnCreateAutomationPeer`
with a small nested peer class deriving from `ControlAutomationPeer`, whose name composes
from the same state the drawing itself reads. All serve `AutomationControlType.Custom`
(a custom-drawn canvas names itself by what it serves).

| Widget | Served peer | Name source (keys) |
|---|---|---|
| `CandlestickChart` | `CandlestickChartAutomationPeer` | empty → `Chart.A11yNoSeries`; main series → `Chart.A11ySeries`; with compare layers → `Chart.A11yWithCompare` |
| `OrderBookWidget` | `OrderBookWidgetAutomationPeer` | empty → `OrderBook.Title`; fed book → `OrderBook.A11yWithSymbol` |
| `DepthChartWidget` | `DepthChartWidgetAutomationPeer` | empty → `OrderBook.DepthTitle`; fed book → `OrderBook.A11yDepthWithSymbol` (stays distinguishable from the book pane) |
| `TradeTapeWidget` | `TradeTapeAutomationPeer` | empty → `Tape.Title`; first trade → `Tape.A11yWithSymbol` |
| `HeatmapWidget` | `HeatmapWidgetAutomationPeer` | `Heatmap.Title` |
| `EquityCurveChart` | `EquityCurveChartAutomationPeer` | the window-attached `AutomationProperties.Name` (the base `GetNameCore` route) — `Backtest.A11yEquity` on the backtest window, `Stats.A11yEquity` on the trade-stats window |

Name composition lives in one pure file, `src/ZettaTrade.UI/Charting/WidgetAccessibilityNames.cs`
(compositors + template resolver); the peers call it, the tests pin it without a dispatcher.

## Icon-only controls

- The four title-bar strip buttons (minimize, maximize, restore, close) carry real automation
  names via `{loc:Localized Chrome.*}` in `src/Livery/Themes/Controls.axaml` — an icon-only
  button's name never relies on its tooltip.
- The palette's query box is named `Chart.A11ySymbolPicker`; the AI chat input is named
  `AI.Chat.InputName` (the hotkey focus route lands there) — both in `MainWindow.axaml`.

## Focus indicators

The shared Livery control themes now carry visible keyboard-focus styles for the reachable
families — `Button:focus`, `ToggleButton:focus`, `TabItem:focus`, `MenuItem:focus`, and the
title bar's `strip-icon:focus` — borrowed from the palette's on-state accent (`Highlight`),
so the indicator follows the theme the user picked.

## Contrast against the shipped palettes

`src/Livery/Themes/Contrast.cs` implements the WCAG relative-luminance/ratio math; the pins in
`ContrastTests` walk **the real theme files in the repo** — the audit cannot pass on a stale
fixture. Required pairs per theme: the text-bearing roles at 4.5:1 (WCAG AA for body text:
primary/secondary text on background and surface, text over the accent fill, title-bar strip,
error line, on-state accent text) and the data-bearing signal family at 3.0:1 (Buy, Sell,
CandleUp, CandleDown). Four colors moved to conform:

| Theme | Role | Was → Now |
|---|---|---|
| `themes/light.json` | `TextSecondary` | `#6c757d` → `#5f6672` |
| `themes/light.json` | `Buy`, `CandleUp` | `#22c55e` → `#15803d` |
| `themes/light.json` | `Highlight` | `#b8860b` → `#8a6100` |
| `themes/trading-pro.json` | `Accent` (+ mirror in `previewColors`) | `#10b981` → `#047857` |

All shipped built-in themes conform (the facts fail the moment a palette degrades).

## Keyboard reachability

- Standard tab order comes from Avalonia for the views' focusable controls; the Livery focus
  styles above make that order visible.
- The palette's suggestion rows are `Focusable=False` **by design**: the palette is a
  keyboard-first command surface — the query box holds focus, arrow keys walk the list,
  Enter executes — and non-focusable rows keep the tab ring one hop long instead of one hop
  per suggestion.

## What the shipped tests certify

| Fixture | Level | Facts |
|---|---|---|
| `tests/ZettaTrade.UI.Tests/Charting/WidgetAccessibilityNamesTests.cs` | pure name composition | 8 — every key resolves, compare lists compose, equity rides the attached-name route |
| `tests/ZettaTrade.UI.Tests/Themes/ContrastTests.cs` | WCAG math + real theme files | 4 — math edges ×3, all-themes-conformance ×1 |
| `tests/ZettaTrade.App.Tests/Views/SkiaWidgetAutomationPeerTests.cs` | live peers on the dispatcher | 7 — real `ControlAutomationPeer`-derived peers (not the none-default), correct name in empty/loaded/compare/fed states, custom control type |
| `tests/ZettaTrade.App.Tests/Views/AccessibilityPinsTests.cs` | source pins + catalog | 5 — icon-only names, focus selectors, picker/chat names, both equity windows, all 10 A11y keys resolving in en **and** ru |

Both full suites run green (UI.Tests 310, App.Tests 1345 at the audit instant).

## Recorded gaps (honest, forward-looking)

- No real screen-reader session (NVDA/Narrator over the live UIA tree) was recorded during
  this pass; statics pin the wiring, a human walk-through should confirm pronunciation before
  beta.
- Cell-level navigation inside dense canvases (walking 1000+ book levels or a tape row by
  row) is out of scope: the widgets advertise what they contain and their symbol/title, not a
  synthetic grid. A future pass could flatten a coarse summary level per widget.
- A dedicated focus-order golden fixture was not shipped; keyboard reachability is certified
  by the source pins and the built-in tab order, not by an ordered snapshot test.
