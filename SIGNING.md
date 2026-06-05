# Code signing the installer and Driver Picker

The default `F1SimHubLive-Installer.exe` and `F1SimHubLive-Picker.exe` are unsigned. When end-users run them, Windows SmartScreen shows the familiar blue "Windows protected your PC" dialog and they have to click *More info → Run anyway*. That's safe, but it's a friction point — especially for non-technical users.

This document captures the options to remove that warning, ranked by cost and resulting UX.

> **v1.1.0+ note:** The repo now ships TWO executables that end users touch directly — the installer AND the Driver Picker (extracted from the installer to `C:\Program Files (x86)\SimHub\F1SimHubLive-Picker.exe` and launched from the Start Menu every race). Both should be signed once you set up any of the options below. They can share the **same certificate / Trusted Signing profile** — see [Signing both binaries](#signing-both-binaries) at the bottom of this doc.

---

## Option 1 — Do nothing (current state)

**Cost:** $0
**UX:** SmartScreen "Unknown publisher" warning on first run. User clicks *More info → Run anyway*. After ~1,000 downloads the file may build organic reputation and the warning disappears on its own, but it's slow.

**Best for:** Hobby projects, internal/private use, "I trust the link" friends.

---

## Option 2 — Standard OV (Organization Validation) code signing certificate

**Cost:** roughly **$70–$300/year**, vendors include:
- [Sectigo / Comodo](https://sectigo.com/ssl-certificates-tls/code-signing) (cheapest, often via resellers like SSL.com or SSL2BUY)
- [DigiCert](https://www.digicert.com/signing/code-signing-certificates)
- [GlobalSign](https://www.globalsign.com/code-signing-certificate)
- [SSL.com](https://www.ssl.com/certificates/code-signing/) (~$170/yr standard)

**UX:** Replaces "Unknown publisher" with your name on the UAC prompt. **SmartScreen still warns** until your specific certificate accumulates "reputation" (Microsoft heuristic — typically hundreds-to-thousands of downloads on a given binary or hash).

**Validation requirement:** Vendor verifies you exist as a legal entity (DBA / LLC / individual). Takes 1–5 business days.

**Best for:** Mature side projects that need branding, but you can tolerate a few weeks of SmartScreen warnings while reputation builds.

---

## Option 3 — EV (Extended Validation) code signing certificate ⭐ **best UX**

**Cost:** **$200–$500/year** (or more for 2–3 year terms with a discount). Vendors:
- [SSL.com EV Code Signing](https://www.ssl.com/certificates/ev-code-signing/) (~$270/yr, often discounted to ~$200)
- [Sectigo EV Code Signing](https://sectigo.com/ssl-certificates-tls/ev-code-signing)
- [DigiCert EV](https://www.digicert.com/signing/code-signing-certificates) (premium)
- [Certum (Poland)](https://shop.certum.eu/data-safety/code-signing-certificates.html) — historically cheapest EV at ~$90–150/yr, popular with indie devs

**UX:** **Instant Microsoft SmartScreen reputation.** No warning on first run, ever. UAC prompt shows your verified company/individual name in blue (not yellow). This is what Adobe / VLC / OBS use.

**Validation:** Longer process (2–10 business days). Vendor verifies legal entity AND issues the private key on a **hardware token** (HSM USB key) shipped to you — you can never extract or copy the key, which is the whole point.

**Cloud signing alternative:** SSL.com, DigiCert, and Sectigo all now offer **cloud-based EV signing** (no hardware token) where the key lives in their HSM and you sign via API. Slightly more expensive but vastly more convenient — especially for CI/CD.

**Best for:** Anything you want to distribute widely. The smoothest possible UX. **This is what I'd recommend for F1SimHubLive** if you want zero-friction downloads.

---

## Option 4 — Microsoft Trusted Signing (Azure Code Signing)

**Cost:** ~$10/month ($120/yr) per "certificate profile" — Microsoft's own managed signing service launched in 2024.

**UX:** Equivalent to EV — full SmartScreen trust from day one.

**Limitations:**
- Requires an Azure subscription.
- Identity verification via Microsoft Entra (more involved than commercial CAs).
- Currently US/Canada/UK only for individual identity (organizations more broadly available).
- The certificate it issues is short-lived (~3 days) and re-issued automatically — so timestamping is mandatory.

**Best for:** Devs already in the Microsoft/Azure ecosystem. Significantly cheaper than EV from commercial CAs and same UX.

Docs: <https://learn.microsoft.com/en-us/azure/trusted-signing/>

---

## Option 5 — Free self-signed certificate

**Cost:** $0
**UX:** Worse than unsigned. Windows will show "Untrusted publisher" with red warning unless the user manually installs your root cert in their Trusted Root store.

**Best for:** Only useful for internal testing / closed user groups where everyone explicitly trusts the cert.

---

## How to actually sign once you have a cert

Whichever option you pick, the signing step is the same. Microsoft ships `signtool.exe` in the Windows SDK (already on most dev boxes; otherwise install the [Windows 10 SDK](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/)).

```powershell
# After dotnet publish:
$exe = "installer\publish\F1SimHubLive-Installer.exe"

# OV / EV with .pfx file (Option 2 or some Option 3 cloud setups):
signtool.exe sign `
    /f "path\to\your\cert.pfx" /p "your-pfx-password" `
    /tr "http://timestamp.sectigo.com" /td sha256 `
    /fd sha256 `
    /d "F1SimHubLive Installer" `
    /du "https://github.com/vicslive/F1SimHubLive" `
    $exe

# EV with hardware token (Option 3 traditional):
# Plug in the token, then:
signtool.exe sign `
    /sha1 "<thumbprint-of-cert-on-token>" `
    /tr "http://timestamp.digicert.com" /td sha256 `
    /fd sha256 `
    /d "F1SimHubLive Installer" `
    /du "https://github.com/vicslive/F1SimHubLive" `
    $exe

# Microsoft Trusted Signing (Option 4) — uses Trusted Signing client tool, not signtool /f:
# Install via NuGet `Microsoft.Trusted.Signing.Client`, then dotnet sign /tsa <endpoint> /tsacc <account>...
```

Verify:

```powershell
signtool.exe verify /pa /v $exe
# Or:
Get-AuthenticodeSignature $exe | Format-List
```

The `/tr` (timestamp server) URL is critical — without timestamping, your signature becomes invalid the moment the cert expires. **Always timestamp.**

---

## Automating it in CI

Once we add a GitHub Actions workflow (`.github/workflows/build-release.yml`) that publishes the installer on each tag, the signing step can be:

1. **GitHub-hosted runner**: store the .pfx (or token credentials) as a GitHub Secret, run `signtool` on the runner.
2. **Trusted Signing**: use the [`azure/trusted-signing-action`](https://github.com/Azure/trusted-signing-action) — no secrets stored locally, signing happens in Azure.
3. **SSL.com eSigner cloud**: their [`code-sign-action`](https://github.com/SSLcom/esigner-codesign) wraps the cloud signing API.

---

## My recommendation for this project

**For now:** keep it unsigned. The download is free, the audience is technical (sim-racers willing to install SimHub plugins), and the SmartScreen warning is one click.

**When you want to grow it:** go with **Option 4 (Microsoft Trusted Signing)** at $120/yr — same UX as a $400 EV cert with no hardware token nonsense, runs in CI, and you can sign as either "Victor de Souza" (individual) or a separate hobby-LLC if you set one up. Cleanest path.

**If you want absolute polish today:** Option 3 EV via Certum (~$90–150/yr) — cheapest EV on the market, ships a hardware token, instant SmartScreen reputation.

Either of those, paired with a GitHub Actions release workflow, gives you a zero-friction download experience and a sustainable release process.

---

## Microsoft employee path (recommended for this repo)

If the maintainer (vicslive) is also a Microsoft employee, the $120/yr Trusted Signing bill is almost certainly covered by an existing benefit — **not** by an employee discount, but by your monthly **Visual Studio Enterprise Azure Dev/Test credit** ($150/month). Net cost: **$0**.

### The path

1. **Confirm your Visual Studio Enterprise benefit** at <https://my.visualstudio.com> with your `@microsoft.com` account.
2. **Activate the monthly Azure credit** from that portal — it provisions an Azure subscription bound to the credit with the "Visual Studio Enterprise Subscribers" offer.
3. **Create a dedicated Azure subscription** for personal OSS (e.g. `vicslive-personal-oss`).
4. **Stand up Trusted Signing Basic** ($9.99/mo, 5,000 signatures/mo, 1 certificate profile) in that subscription. Trusted Signing is on the Dev/Test eligible service list — billed against your credit.
5. **Verify identity as Individual (Public Trust)** — government ID + utility bill. ~2–10 business days. The certificate will read your verified legal name (e.g. "Victor de Souza"). No company or LLC required.
6. **Wire up federated identity** from your personal GitHub (`vicslive/F1SimHubLive`) to the Microsoft-tenant Azure subscription — this is the documented OSPO-supported pattern, no policy issue with cross-account linking.

### Policy / OSPO notes

- Personal OSS lives on **personal GitHub** (here: `vicslive`). This is documented Microsoft guidance.
- Linking that personal GitHub to your `@microsoft.com` account through GitHub Enterprise SSO is a separate thing — it doesn't move the repo's ownership or licensing.
- Azure billing can flow through your Microsoft-tenant credits regardless of where the GitHub repo lives, as long as the Azure resources themselves are in your subscription.
- For questions, the internal alias is `aka.ms/opensource` (OSPO).

### What's NOT covered as an employee

- There is no "MS employee SKU" for Trusted Signing itself — the public price applies. The $0 net cost comes from your VS Enterprise credit absorbing the charge.
- Linking accounts does not unlock paid Azure services automatically — you have to activate the credit and put resources in the right subscription.
- Trusted Signing identity verification is the same paperwork regardless of employment.

### ⚠️ SFI (Secure Future Initiative) gotcha — read this first

As of **2025–2026**, Microsoft's **Secure Future Initiative (SFI)** has deactivated personal Azure subscriptions that were linked to a `@microsoft.com` corporate identity. The practical impact for the employee path above:

- Any pre-existing personal subscription you connected to your corporate account may be **disabled** by SFI controls.
- The VS Enterprise portal may still report your credit as "in use" because the disabled subscription is technically still bound to it — which **blocks you from activating a new subscription on the same credit yourself**.
- You cannot resolve this through the portal. You need **Microsoft Azure billing support** to:
  1. Detach the credit from the disabled/orphaned subscription.
  2. Re-issue a new subscription under a clean personal identity (e.g. `vicminds@outlook.com`) that draws from the same VS Enterprise credit on your `@microsoft.com` account.

**How to unblock:** open a billing support case via the Azure portal or via your VS Enterprise portal. Reference the SFI deactivation as the cause; ask explicitly to re-issue the VS Enterprise Azure credit subscription under a non-corporate-linked account.

Until that case resolves, the cleanest interim is to **stay on the unsigned release** (Option 1). The CI workflow ships with signing wired but inert — you flip the secrets on later when the subscription + Trusted Signing account exist.

---

## CI release workflow

This repo ships [`.github/workflows/release.yml`](.github/workflows/release.yml) — a GitHub Actions workflow that, on every `v*.*.*` tag push:

1. Restores .NET 8.
2. Publishes the single-file `F1SimHubLive-Installer.exe`.
3. **If** Trusted Signing repo secrets are configured (see below), signs the .exe via [`azure/trusted-signing-action`](https://github.com/Azure/trusted-signing-action) using federated identity (no secrets in the repo besides config).
4. Computes a SHA-256 hash for the release notes.
5. Creates the GitHub Release with the (optionally signed) installer attached and an auto-generated release body.

If the signing secrets aren't set, the workflow skips signing gracefully and still publishes the unsigned installer. This means the workflow is **safe to commit today** — it'll just build unsigned until you flip the switch.

### Repo secrets to enable signing

Once your Trusted Signing account is provisioned and an Entra App Registration is set up with a federated credential bound to the `vicslive/F1SimHubLive` repo:

| Secret name | Value |
|---|---|
| `AZURE_TENANT_ID` | Your Microsoft-tenant Entra tenant ID (GUID) |
| `AZURE_CLIENT_ID` | Client ID of the Entra App Registration |
| `AZURE_TRUSTED_SIGNING_ENDPOINT` | Trusted Signing account region endpoint, e.g. `https://eus.codesigning.azure.net` |
| `AZURE_TRUSTED_SIGNING_ACCOUNT` | Trusted Signing account name |
| `AZURE_TRUSTED_SIGNING_PROFILE` | Certificate profile name (per-identity) |

Set them via `gh secret set NAME -R vicslive/F1SimHubLive --body "value"` or through the GitHub UI under **Settings → Secrets and variables → Actions**.

### Federated identity (no PFX / no token / no secrets to rotate)

The workflow uses `azure/login@v2` with `id-token: write` — GitHub mints a short-lived OIDC token, exchanges it for an Azure access token via your Entra App Registration's federated credential, and uses that to call Trusted Signing. **No long-lived secret is stored anywhere.** This is the recommended modern pattern.

Set up the federated credential in Entra once: App Registration → Certificates & secrets → Federated credentials → "Add credential" → choose "GitHub Actions deploying Azure resources" → org `vicslive`, repo `F1SimHubLive`, entity type **Environment**, name `release`. This pairs with `environment: release` on the release job in `release.yml` and avoids having to add a new federated credential per tag.

Grant the App Registration the **Artifact Signing Certificate Profile Signer** role on the Trusted Signing account. (Previously named "Trusted Signing Certificate Profile Signer" — Microsoft renamed it in 2025.)


---

## Signing both binaries

> **Added in v1.1.0** — the repo now ships two end-user executables (installer + picker). This section covers signing both with one account.

### Why both must be signed

| Binary | Where it ends up | When does SmartScreen see it? |
|---|---|---|
| F1SimHubLive-Installer.exe | User Downloads folder | At install time, on first double-click |
| F1SimHubLive-Picker.exe | `C:\Program Files (x86)\SimHub\F1SimHubLive-Picker.exe` (extracted by the installer) | Every time the user clicks the Start Menu shortcut to switch drivers |

If only the installer is signed, the install completes cleanly — but then the very first time the user picks the Start Menu shortcut, SmartScreen will gate the picker because **the .exe blob inside the installer's embedded resource was unsigned at embed time**. Signing the installer wrapper does not retroactively sign the resource it carries.

### Where the picker has to be signed

The picker exe **must be signed BEFORE the installer embeds it**. The build order is:

1. `dotnet publish picker/F1SimHubLive.Picker.csproj` → produces `picker/bin/Release/net8.0-windows/win-x64/publish/F1SimHubLive-Picker.exe`
2. **Sign the picker exe** at the path above.
3. `dotnet publish installer/F1SimHubLive.Installer.csproj` → the installer's `<PublishPicker>` MSBuild target sees the file already exists (`Condition="!Exists(...)"`), skips re-publishing, and embeds the SIGNED exe as the `F1SimHubLive-Picker.exe` resource.
4. **Sign the installer exe** at `installer/publish/F1SimHubLive-Installer.exe`.
5. End user downloads → installer is signed (no SmartScreen) → picker is extracted already-signed (no SmartScreen on Start Menu launch).

### Same Trusted Signing account, no extra cost

Trusted Signing bills **per signing account per month**, not per binary or per signature. The Basic SKU (~$10/mo) includes 5,000 signings/month — your two-binary release uses 2 of those. The same `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_TRUSTED_SIGNING_ENDPOINT`, `AZURE_TRUSTED_SIGNING_ACCOUNT`, and `AZURE_TRUSTED_SIGNING_PROFILE` secrets work for both binaries. The federated credential on your Entra App Registration covers both — Trusted Signing doesn't have per-binary scoping.

### CI workflow change to support both

In `.github/workflows/release.yml`, insert a picker publish + sign pair BEFORE the installer publish. The new steps mirror the existing `Sign installer` step but point at the picker's publish folder:

```yaml
- name: Publish picker
  run: dotnet publish picker/F1SimHubLive.Picker.csproj -c Release --nologo

- name: Sign picker with Trusted Signing
  if: steps.signcheck.outputs.configured == 'true'
  uses: azure/trusted-signing-action@v0.5.1
  with:
    endpoint: ${{ secrets.AZURE_TRUSTED_SIGNING_ENDPOINT }}
    trusted-signing-account-name: ${{ secrets.AZURE_TRUSTED_SIGNING_ACCOUNT }}
    certificate-profile-name: ${{ secrets.AZURE_TRUSTED_SIGNING_PROFILE }}
    files-folder: picker/bin/Release/net8.0-windows/win-x64/publish
    files-folder-filter: exe
    file-digest: SHA256
    timestamp-rfc3161: http://timestamp.acs.microsoft.com
    timestamp-digest: SHA256

# (existing) Publish installer — picks up the already-signed picker exe and embeds it
- name: Publish installer
  run: dotnet publish installer/F1SimHubLive.Installer.csproj -c Release --nologo

# (existing) Sign installer
- name: Sign installer with Trusted Signing
  ...
```

Net change: 14 lines. No Azure-side change required.

### Verifying the chain after signing

After a signed release:

```powershell
# Installer:
Get-AuthenticodeSignature 'F1SimHubLive-Installer.exe' | Format-List

# Picker (after install):
Get-AuthenticodeSignature 'C:\Program Files (x86)\SimHub\F1SimHubLive-Picker.exe' | Format-List
```

Both should show `Status: Valid` and `SignerCertificate.Subject: CN=Victor de Souza ...` (or whatever your verified Trusted Signing identity reads).
