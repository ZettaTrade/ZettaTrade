# Writing ZettaTrade plugins (the SDK contract)

ZettaTrade loads out-of-tree plugin assemblies through one blessed path
(T-131): an assembly dropped into the plugins directory gets a manifest
scan at startup, and every plugin that passes the gates registers its
extensions with the shell. This document is the contract those plugins
compile against.

## The contract assembly

Plugins reference `ZettaTrade.Plugins.Abstractions` and nothing else.
The assembly is the versioned contract: it carries the manifest, the
loader entry type, the services window and the UI extension descriptors,
and it is SemVer-governed — a breaking change on any public member bumps
the major version, and a pinned baseline test
(`tests/ZettaTrade.Infrastructure.Tests/Plugins/PluginContractBaselineTests.cs`)
refuses an accidental break before any plugin author's build does.

## The manifest: one attribute per plugin class

Plugin identity lives in code:

```cs
[ZettaTradePlugin("acme.equity", "ACME Equity Panel", "1.4.2", "acme.orders")]
public sealed class EquityPanelPlugin : IZettaTradePlugin { ... }
```

- The **plugin id** is the stable key — unique across every loaded
  plugin, and the name other plugins declare as a dependency. Duplicate
  ids refuse every carrier loudly; there is no first-wins race.
- The **display name** is a developer-facing English literal; anything an
  end user sees rides resx keys under the plugin's own prefix (see the
  descriptors below).
- The **version** is SemVer 2 — `major.minor.patch`, optional
  `-prerelease`, optional `+build`. Anything else refuses.
- The **dependencies** name plugin ids that must be loaded first. A
  declared id nothing produces refuses the dependent loudly; a cycle
  refuses every member; the survivors start +dependence-first+.

An entry implementing `IZettaTradePlugin` without the attribute has no
identity and is refused loudly.

## The lifecycle: one-time, async, cancellable

The loader instantiates the plugin once (a parameterless constructor is
the contract), calls `ConfigureServices(IServiceCollection)` so the
plugin's own registrations land in a plugin-owned DI layer, and then runs
`InitializeAsync(IPluginContext, CancellationToken)` once — the app's
`IAsyncInitializable` canon: await the startup work, store nothing that
withers, touch no UI thread, honor the token.

The context joins the plugin's own layer automatically. Never register
`IPluginContext` yourself; consume it instead.

## The services window: bounded, null-honest

`IPluginContext.GetService<T>()` resolves the plugin's own layer first
and the host's capabilities behind it: market-data adapters, settings,
risk and confirmation, localization, logging. A missing capability
answers `null` — **rejected-not-defaulted**: the plugin decides what an
absent adapter or risk manager means for it, and refuses the path rather
than improvising a substitute. There is no way to smuggle the app's raw
service provider inside.

## UI extensions: panels, toolbar items, settings sections

A plugin registers descriptors through its own DI layer, one per surface:

- `PluginPanelDescriptor(id, titleResxKey, widgetFactory)` — the shell
  renders what the factory builds when it chooses to host the panel.
- `PluginToolbarItemDescriptor(id, labelResxKey)`.
- `PluginSettingsSectionDescriptor(id, titleResxKey)`.

The `*ResxKey` names live under the plugin's own module prefix and ship
in the PLUGIN's localization resources (en and ru both); the app's keys
are never borrowed. The loader collects every registration into the
per-plugin extension registry, each stamped with its owning plugin's id.

## The trading-safety obligations (non-negotiable)

Any order path inside a plugin goes through `IRiskManager` and
`IConfirmationService` exactly like every other order path — no
exceptions, no bypass. Plugins that read positions or stream prices never
improvise a default when the symbol or the side is unparsable: the error
refuses. Secrets reach plugins only through the app's secret-store
mechanisms; a plugin never assumes keys from a file, a command line or an
environment variable.

## The platform rules

- Nothing writes to stdout except the MCP protocol messages; plugin logs
  ride the host's file logs (the context's logger factory).
- The plugins directory never executes plugin code before the scan —
  assemblies that are not SDK plugins (indicators, strategies) share the
  directory and are handled by their own loaders.
- All user-facing strings are localized through the plugin's own resx
  files, en and ru, through its module-prefixed keys.

## Where the loader looks

The blessed path is `<DataDirectory>/plugins` (typically
`%LOCALAPPDATA%\ZettaTrade\data\plugins`). Every top-level `*.dll`
arrives; garbage files warn and skip; assemblies whose types read only
partially are tolerated per the app's own plugin-catalog canon (broken
neighbors never abort the scan).

Every verdict lands in the `PluginLoadReport`: what loaded, what was
refused, sorted, with a reason per refusal — visible, re-derivable, no
silent half-alive plugins.
