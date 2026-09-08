#!/usr/bin/env python3
"""Generate all four package channels from verified release API metadata."""
import argparse
import json
import pathlib
import re

REPOSITORY = "https://github.com/AlsoBeltrix/PowerShell-Token-Killer"


def generate(metadata, output, allow_draft=False):
    version = metadata["tag_name"].removeprefix("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?", version):
        raise ValueError("Invalid release version")
    if metadata["draft"] and not allow_draft:
        raise ValueError("Package publication requires a published release")
    assets = {a["name"]: a for a in metadata["assets"]}

    def asset(rid):
        suffix = "zip" if rid.startswith("win-") else "tar.gz"
        name = f"ptk-{version}-{rid}.{suffix}"
        item = assets[name]
        url = f"{REPOSITORY}/releases/download/v{version}/{name}"
        draft_url = (allow_draft and metadata["draft"] and re.fullmatch(
            re.escape(REPOSITORY) + r"/releases/download/untagged-[a-z0-9]+/" + re.escape(name),
            item["browser_download_url"]))
        if (item["browser_download_url"] != url and not draft_url) or not re.fullmatch(r"sha256:[0-9a-f]{64}", item["digest"] or ""):
            raise ValueError(f"Unexpected asset URL/digest: {name}")
        return url, item["digest"][7:]

    def write(relative, text):
        path = output / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    mac_url, mac_hash = asset("osx-arm64")
    x64_url, x64_hash = asset("linux-x64")
    arm_url, arm_hash = asset("linux-arm64")
    formula = f'''class Ptk < Formula
  desc "Warm PowerShell MCP sessions with compact, recoverable output"
  homepage "{REPOSITORY}"
  version "{version}"
  license "Apache-2.0"

  depends_on "rtk"

  on_macos do
    depends_on arch: :arm64
    on_arm do
      url "{mac_url}"
      sha256 "{mac_hash}"
    end
  end

  on_linux do
    on_arm do
      url "{arm_url}"
      sha256 "{arm_hash}"
    end
    on_intel do
      url "{x64_url}"
      sha256 "{x64_hash}"
    end
  end

  # This release includes native signed executables and embedded runtime
  # libraries. Repackaging a bottle is unnecessary and can alter signatures.
  def install
    libexec.install Dir["*"]
    (libexec/"PACKAGE-HOME").write("#{{opt_libexec}}\\n")
    bin.install_symlink libexec/"bin/ptk"
  end

  def caveats
    <<~EOS
      Register selected harnesses as your ordinary user:
        ptk init --agent codex
      Inspect the package and dependency with ptk doctor.
      Close PTK sessions using this package before upgrading or removing it.
      Remove harness integration with ptk uninstall --agent codex before uninstalling.
    EOS
  end

  test do
    assert_match version.to_s, shell_output("#{{bin}}/ptk version")
    assert_match "RTK:", shell_output("#{{bin}}/ptk doctor")
    assert_match "Choose --agent", shell_output("#{{bin}}/ptk init 2>&1", 64)
  end
end
'''
    write("homebrew/Formula/ptk.rb", formula)
    scoop = {
        "version": version,
        "description": "Warm PowerShell MCP sessions with compact, recoverable output",
        "homepage": REPOSITORY,
        "license": "Apache-2.0",
        "depends": "main/rtk",
        "architecture": {arch: {"url": asset(rid)[0], "hash": asset(rid)[1]}
                         for arch, rid in (("64bit", "win-x64"), ("arm64", "win-arm64"))},
        "bin": [["bin\\ptk.exe", "ptk"]],
        "post_install": '[IO.File]::WriteAllText((Join-Path $dir "PACKAGE-HOME"), (Join-Path (Split-Path $dir) "current"))',
        "notes": ["Run ptk init --agent codex (or claude,kimi,grok,agy) as your ordinary user.",
                  "Close sessions using this package before upgrade/removal. Run ptk uninstall --agent codex before scoop uninstall ptk."],
        "checkver": {"url": REPOSITORY.replace("https://github.com/", "https://api.github.com/repos/") + "/releases",
                     "jsonpath": "$[0].tag_name", "regex": "v(.+)"},
        "autoupdate": {"architecture": {
            arch: {"url": f"{REPOSITORY}/releases/download/v$version/ptk-$version-{rid}.zip"}
            for arch, rid in (("64bit", "win-x64"), ("arm64", "win-arm64"))},
            "hash": {"url": f"{REPOSITORY}/releases/download/v$version/SHA256SUMS"}},
    }
    write("scoop/bucket/ptk.json", json.dumps(scoop, indent=4) + "\n")
    identifier = "roethlar.PowerShellTokenKiller"
    winget_path = f"winget/manifests/r/roethlar/PowerShellTokenKiller/{version}"
    header = f"PackageIdentifier: {identifier}\nPackageVersion: {version}\n"
    installer = header + '''InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: bin\\ptk.exe
  PortableCommandAlias: ptk
UpgradeBehavior: uninstallPrevious
Commands:
- ptk
Dependencies:
  PackageDependencies:
  - PackageIdentifier: rtk-ai.rtk
Installers:
'''
    for architecture in ("x64", "arm64"):
        url, digest = asset("win-" + architecture)
        installer += f"- Architecture: {architecture}\n  InstallerUrl: {url}\n  InstallerSha256: {digest.upper()}\n"
    installer += "ManifestType: installer\nManifestVersion: 1.12.0\n"
    write(f"{winget_path}/{identifier}.installer.yaml", installer)
    locale = header + f'''PackageLocale: en-US
Publisher: PowerShell Token Killer contributors
PublisherUrl: {REPOSITORY}
PublisherSupportUrl: {REPOSITORY}/issues
PackageName: PowerShell Token Killer
PackageUrl: {REPOSITORY}
License: Apache-2.0
LicenseUrl: {REPOSITORY}/blob/master/LICENSE
ShortDescription: Warm PowerShell MCP sessions with compact, recoverable output
Description: |-
  PTK embeds PowerShell and runs isolated warm sessions for MCP agent harnesses.
  Install files with the package manager, then run ptk init --agent codex to
  register a selected harness. A separate PowerShell installation is not required.
Tags:
- mcp
- powershell
- ai
ReleaseNotesUrl: {REPOSITORY}/releases/tag/v{version}
InstallationNotes: Run ptk init --agent codex (or claude,kimi,grok,agy) as your ordinary user. Close sessions before upgrade/removal and run ptk uninstall --agent codex before uninstalling the package.
ManifestType: defaultLocale
ManifestVersion: 1.12.0
'''
    write(f"{winget_path}/{identifier}.locale.en-US.yaml", locale)
    write(f"{winget_path}/{identifier}.yaml", header + "DefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.12.0\n")
    pkgver = version.replace("-", "").replace("rc.", "rc")
    pkgbuild = f'''# Maintainer: PowerShell Token Killer contributors
pkgname=ptk-bin
pkgver={pkgver}
pkgrel=1
pkgdesc='Warm PowerShell MCP sessions with compact, recoverable output'
arch=('x86_64' 'aarch64')
url='{REPOSITORY}'
license=('Apache-2.0')
depends=('gcc-libs' 'glibc' 'icu' 'openssl' 'krb5' 'zlib' 'rtk')
provides=('ptk')
conflicts=('ptk')
options=('!strip' '!debug')
source_x86_64=('ptk-{version}-linux-x64.tar.gz::{x64_url}')
sha256sums_x86_64=('{x64_hash}')
source_aarch64=('ptk-{version}-linux-arm64.tar.gz::{arm_url}')
sha256sums_aarch64=('{arm_hash}')

package() {{
  install -d "$pkgdir/usr/lib/ptk" "$pkgdir/usr/bin"
  cp -a "$srcdir/bin" "$srcdir/scripts" "$srcdir/src" "$pkgdir/usr/lib/ptk/"
  install -m644 "$srcdir/VERSION" "$srcdir/BUILD-PROVENANCE.json" "$srcdir/LICENSE" "$srcdir/README.md" "$pkgdir/usr/lib/ptk/"
  ln -s /usr/lib/ptk/bin/ptk "$pkgdir/usr/bin/ptk"
}}
'''
    write("aur/PKGBUILD", pkgbuild)
    srcinfo = f'''pkgbase = ptk-bin
\tpkgdesc = Warm PowerShell MCP sessions with compact, recoverable output
\tpkgver = {pkgver}
\tpkgrel = 1
\turl = {REPOSITORY}
\tarch = x86_64
\tarch = aarch64
\tlicense = Apache-2.0
'''
    for dependency in ("gcc-libs", "glibc", "icu", "openssl", "krb5", "zlib", "rtk"):
        srcinfo += f"\tdepends = {dependency}\n"
    srcinfo += f'''\tprovides = ptk
\tconflicts = ptk
\toptions = !strip
\toptions = !debug
\tsource_x86_64 = ptk-{version}-linux-x64.tar.gz::{x64_url}
\tsha256sums_x86_64 = {x64_hash}
\tsource_aarch64 = ptk-{version}-linux-arm64.tar.gz::{arm_url}
\tsha256sums_aarch64 = {arm_hash}

pkgname = ptk-bin
'''
    write("aur/.SRCINFO", srcinfo)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("metadata", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--allow-draft", action="store_true", help="Generate validation fixtures only; never publish these")
    args = parser.parse_args()
    generate(json.loads(args.metadata.read_text()), args.output, args.allow_draft)
