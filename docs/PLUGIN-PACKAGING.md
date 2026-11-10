# Closed-module packaging (the private-repo contract)

This document addresses the maintainers of the closed (paid) plugin
modules — the private side of the open-core split (`docs/LICENSES.md`).
It fixes the repository layout conventions, the signed-artifact CI
recipe, and the license-key issuance shape. The consumer side — the
validator, the entitlement gate, the activation dialog — already exists
here (T-132); nothing below changes the app.

## Repository layout

A closed module works like every other plugin until packaging: same
manifest attribute, same `ZettaTrade.Plugins.Abstractions` reference,
same SDK contract as `docs/PLUGINS.md` describes. What differs:

- The repository is named `zettatrade-plugin-<feature>` (all lowercase,
  the feature in kebab-case) — one repo per module.
- Its **LICENSE is proprietary**, not MIT: the agreement for the module's
  source and artifacts, written once, in the private repo.
- An **EULA source file** (the end-user agreement, English text) travels
  inside the artifact as `<pluginId>.EULA.txt` — see below.
- No MIT wording anywhere in the private repo (the header banners,
  the README, the CI definitions stay the module's own).

Everything below the name — branches, CI runners — is the module's own
business; only the packaging shape and the key issuance are fixed here.

## The plugin id binds everything

The plugin id in the manifest attribute is the key the whole flow hangs
on: the entitlement gate matches it against the protected list, the
license key carries it in its claims, the activation dialog takes it as
the user's paste, the EULA file is found by it. Choose it once, keep it
stable, and let it type-check the packaging: the artifact's folder name,
its package id and the `EULA.txt` prefix must equal the manifest id.

## The EULA file: `<pluginId>.EULA.txt` next to the DLL

The activation dialog reads the agreement from the app's plugins
directory: the plain file `<pluginId>.EULA.txt` (no subdirectories — the
id is the file name, path traversal is refused) sitting next to the
artifact the customer unpacked there. Convention:

- the artifact ships the file at its root, named `<pluginId>.EULA.txt`;
- a module without an agreement ships no such file, and the activation
  dialog runs without the consent step;
- the acceptance persists per plugin id under the customer's settings
  (key `EulaAccepted.{pluginId}`), so the text surfaces at the first
  activation only — the app re-surfaces it after the file changes only
  if the plugin id itself changes.

The private repo keeps the EULA source; CI stamps the packaged copy.

## The signed artifact recipe (private CI)

One artifact type today — a zip of the plugin's build output (the DLL,
the manifest-carrying assembly, the `*.deps.json`, the EULA file). A
NuGet package form may follow with the same rules. The recipe, fixed by acceptance:

1. Pin the .NET SDK and `ZettaTrade.Plugins.Abstractions` versions in
   the CI definition (`*-global.json` + pinned package reference).
2. `dotnet build -c Release` + `dotnet test -c Release` — the gate must
   stay green on the private side too.
3. Produce the artifact and a detached SHA-256 checksum file; the
   release notes record the **Abstractions version the artifact
   builds against** (the version tracking: an artifact is meaningful
   only against an SDK contract it compiled on).
4. Sign: the zip and its checksum carry an Authenticode signature of the
   issuer's code-signing certificate, fed through the CI secret store —
   the certificate never appears in a job log, repo or cache; the
   checksum file lists each file's hash, so a customer-side verification
   needs no trust service.
5. Store the artifact only in the module's private release channel.

The public app never consumes the private keys; the customer unpacks the
artifact into the plugins directory and activates by key.

## License-key issuance (the `zt1` shape)

A license key is one line of text:

```
zt1.<base64url(payload)>.<base64url(signature)>
```

- **payload** — the claims document, JSON of the house shape (camelCase,
  `JsonSerializerDefaults.Web`):
  `{ "product": "ZettaTrade", "pluginId": "<the manifest id>",
  "subject": "<the licensee reference>", "issuedAtUtc": "<ISO 8601>",
  "expiresAtUtc": "<ISO 8601>" }` — the product constant is
  `ZettaTrade`, and a different product or plugin id refuses.
- **signature** — RSA-SHA256 (PKCS#1 padding) over the exact payload
  bytes as serialized into the token. Sign the bytes you emit: a
  re-serialized, semantically identical document breaks the signature.

A minimal issuer step (the shapes are the app's own contracts):

```cs
var payload = JsonSerializer.SerializeToUtf8Bytes(
    new LicenseKeyPayload(LicenseContract.Product, pluginId, subject, issued, expires),
    LicenseJson.Options);

var token = LicenseToken.Encode(
    payload,
    rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
```

Key discipline:

- the **private key** stays in the issuer's secret store, used by
  nothing on a customer machine; the public SPKI half ships as a PEM
  the operator configures into the app's settings
  (`Licensing/VerificationKey`) — the validator refuses everything
  while it stays unconfigured or unparsable;
- no private key, certificate or issued key enters a repo, a test, a
  log or an issue in either world; the app's test suite runs on its own
  generated test keys;
- expiry is the offline revocation: validation is local, no phone-home,
  so a subscription model issues short-lived keys (days to weeks) and a
  perpetual one long-lived — the issuer picks the window, the app only
  honors it;
- replacing the issuer key pair means a new PEM (a settings update)
  plus re-issued keys; old keys refuse with `Tampered` against the new
  PEM — the rotation is a silent upgrade only when the operator re-keys
  along with it.

## Activation, once more, end to end

The customer unpacks the signed artifact into the plugins directory;
the loader refuses every protected candidate whose entitlement does not
hold (a misformulated protected list protects everything, never
nothing). The Help menu's activation dialog takes the plugin id and the
key; validation is local and offline — settings and compute only, files
only; a valid verdict stores the key, every other verdict — malformed,
tampered, expired, wrong product or plugin id, missing, unconfigured —
refuses loudly with nothing stored and the module stays off. The EULA
surfaces at the first activation; the accept tick is the only path
through.
