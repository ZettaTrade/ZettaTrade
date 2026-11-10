# Licenses (the open-core split at a glance)

ZettaTrade is open core: the everything-you-need-to-run app is MIT and
lives here; the closed (paid) modules live in private repositories and
ship as signed artifacts that this app activates with a locally validated
license key. One table answers "what is what":

| Thing | Where it lives | License | How it reaches a customer |
| --- | --- | --- | --- |
| The app (UI, core, infrastructure, charting, AI) | this repository | MIT | public releases |
| The plugin SDK (`ZettaTrade.Plugins.Abstractions`, the loader, the fixtures) | this repository | MIT | public releases |
| The licensing surface (validator, gate, activation dialog, EULA reader) | this repository | MIT | public releases |
| A closed module's code, manifest, UI, packaging | its private repo (`zettatrade-plugin-<feature>`) | proprietary | signed artifact, private distribution |
| A closed module's EULA text | inside its signed artifact (`<pluginId>.EULA.txt`) | — | travels with the artifact |
| License keys for a closed module | issued offline by the module's issuer | — | to the customer only |
| Signing keys (module artifacts, license private keys) | the module's issuer secret store | — | never |

## The licensing surface is MIT, the entitlements are not

The mechanism — the `zt1` token, the RSA-SHA256 validator, the
entitlement gate the loader consults before every load, the activation
dialog, the EULA reader — is part of the MIT core, documented in
`docs/PLUGIN-PACKAGING.md`. What the mechanism protects is not: closed
modules' code, their proprietary LICENSE and EULA texts, their private
signing keys and their issued license keys never land in a public
repository, public beta, or public issue.

## What must never land in this repository

- any closed module's source, manifest, resources or build output;
- any proprietary LICENSE or EULA wording (public references stay
  generic: "a proprietary LICENSE+EULA pair" — the words themselves live
  in the private repo and the artifact);
- any private key material (signing certificates, license issuer keys)
  or a real issued license key;
- any machine-literal that identifies a private repository, CI vendor or
  customer (`docs/TASKS*.md` portability rule applies to these files too).

The EULA acceptance fact and the stored license key live on the
customer's machine — under the app's own settings and log files — and
nowhere public.

## Not a legal document

This page describes the engineering split (what ships where, what
activates how). Legal terms for a closed module are that module's
proprietary LICENSE file, packaged with it, and its EULA, surfaced by the
activation dialog at the first activation and accepted per plugin id.
Third-party code enters through NuGet under its own licenses; the
dependency graph stays inspectable in the lock files.
