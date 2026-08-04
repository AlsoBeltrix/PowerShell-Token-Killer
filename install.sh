#!/bin/sh
# Installs PowerShell Token Killer (ptk) from GitHub Releases on macOS/Linux.
#
# Downloads the release asset for this platform, verifies it against the
# release's SHA256SUMS, lays it out under ~/.ptk, ensures RTK is available,
# and prints the MCP registration command. RTK is a required dependency: this
# installer never completes onto a machine where ptk would refuse to start.
#
#   curl -fsSL https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.sh | sh
#   sh install.sh --version 0.2.0
#   sh install.sh --uninstall [--purge]
#
# POSIX sh: the payload embeds its own PowerShell, so an installed pwsh is
# not a prerequisite for installing.
set -eu

PTK_REPO='AlsoBeltrix/PowerShell-Token-Killer'
RTK_REPO='rtk-ai/rtk'
PTK_HOME="${HOME}/.ptk"
RTK_MARKER="${PTK_HOME}/.ptk-installed-rtk"
# Everything the installer owns; anything else under ~/.ptk is user-owned.
PAYLOAD_ENTRIES='bin src scripts VERSION LICENSE README.md'

VERSION=''
UNINSTALL=0
PURGE=0

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --version) shift; [ $# -gt 0 ] || die '--version needs a value'; VERSION="$1" ;;
        --version=*) VERSION="${1#--version=}" ;;
        --uninstall) UNINSTALL=1 ;;
        --purge) PURGE=1 ;;
        -h|--help)
            sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
    shift
done

# ptk is a per-user tool and its warm runspace inherits the harness's
# privileges; an elevated install invites root-owned files.
[ "$(id -u)" -ne 0 ] || die 'Refusing to run as root: ptk installs per-user. Re-run as your normal user.'

detect_rid() {
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Linux) os_part='linux' ;;
        Darwin) os_part='osx' ;;
        *) die "Unsupported operating system: $os" ;;
    esac
    case "$arch" in
        x86_64|amd64) arch_part='x64' ;;
        arm64|aarch64) arch_part='arm64' ;;
        *) die "Unsupported architecture: $arch" ;;
    esac
    if [ "$os_part" = 'osx' ] && [ "$arch_part" = 'x64' ]; then
        die 'Intel macOS (osx-x64) is not a supported ptk platform. Supported: linux-x64, linux-arm64, osx-arm64.'
    fi
    printf '%s-%s' "$os_part" "$arch_part"
}

rtk_asset_for() {
    case "$1" in
        linux-x64) printf 'rtk-x86_64-unknown-linux-musl.tar.gz' ;;
        linux-arm64) printf 'rtk-aarch64-unknown-linux-gnu.tar.gz' ;;
        osx-arm64) printf 'rtk-aarch64-apple-darwin.tar.gz' ;;
        *) die "No rtk asset mapping for RID '$1'." ;;
    esac
}

download() {
    # $1 url, $2 destination
    info "  downloading $(basename "$2")"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$1" -o "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$2" "$1"
    else
        die 'neither curl nor wget is available'
    fi
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | cut -d' ' -f1
    else
        die 'neither sha256sum nor shasum is available to verify the download'
    fi
}

verify_checksum() {
    # $1 file, $2 sums file, $3 asset name
    expected="$(grep -F "$3" "$2" | head -n1 | cut -d' ' -f1)"
    [ -n "$expected" ] || die "no checksum entry for $3; refusing to install an unverified download"
    actual="$(sha256_of "$1")"
    if [ "$expected" != "$actual" ]; then
        die "checksum mismatch for $3
  expected $expected
  actual   $actual
Refusing to install an unverified download."
    fi
}

# A version banner only proves the image loaded; ptk depends on the rewriter
# answering, so probe that instead.
rtk_answers() {
    out="$("$1" hook check --agent ptk 'git status --short' 2>/dev/null)" || return 1
    [ -n "$out" ]
}

ensure_rtk() {
    # $1 rid
    if existing="$(command -v rtk 2>/dev/null)" && rtk_answers "$existing"; then
        info "rtk found on PATH: $existing"
        return 0
    fi

    info 'rtk not found; installing it alongside ptk (required dependency).'
    asset="$(rtk_asset_for "$1")"
    staging="$(mktemp -d)"
    # shellcheck disable=SC2064
    trap "rm -rf '$staging'" EXIT INT TERM

    download "https://github.com/${RTK_REPO}/releases/latest/download/${asset}" "${staging}/${asset}"
    download "https://github.com/${RTK_REPO}/releases/latest/download/checksums.txt" "${staging}/checksums.txt"
    verify_checksum "${staging}/${asset}" "${staging}/checksums.txt" "$asset"

    mkdir -p "${staging}/x"
    tar -xzf "${staging}/${asset}" -C "${staging}/x"
    found="$(find "${staging}/x" -type f -name rtk | head -n1)"
    [ -n "$found" ] || die 'rtk archive did not contain an rtk binary'

    mkdir -p "${PTK_HOME}/bin"
    cp "$found" "${PTK_HOME}/bin/rtk"
    chmod +x "${PTK_HOME}/bin/rtk"
    rtk_answers "${PTK_HOME}/bin/rtk" ||
        die "the installed rtk did not answer 'hook check'; ptk would refuse to start, so this install is being aborted"
    printf '%s' "${PTK_HOME}/bin/rtk" > "$RTK_MARKER"
    info "rtk installed: ${PTK_HOME}/bin/rtk"

    rm -rf "$staging"
    trap - EXIT INT TERM
}

assert_not_running() {
    if pgrep -f "${PTK_HOME}/bin/PtkMcpServer" >/dev/null 2>&1; then
        die "ptk is running from ${PTK_HOME}. Close the harness session, then re-run."
    fi
}

uninstall_ptk() {
    assert_not_running

    if command -v claude >/dev/null 2>&1; then
        claude mcp remove --scope user ptk >/dev/null 2>&1 || true
        info 'Removed Claude Code registration (user scope).'
    fi

    # Only ever remove an rtk this installer placed.
    if [ -f "$RTK_MARKER" ]; then
        ours="$(cat "$RTK_MARKER")"
        if [ -n "$ours" ] && [ -f "$ours" ]; then
            rm -f "$ours"
            info "Removed the rtk this installer placed: $ours"
        fi
        rm -f "$RTK_MARKER"
    fi

    for entry in $PAYLOAD_ENTRIES; do
        rm -rf "${PTK_HOME:?}/${entry}"
    done

    if [ "$PURGE" -eq 1 ]; then
        rm -rf "${PTK_HOME:?}"
        info "Purged ${PTK_HOME}"
    elif [ -d "$PTK_HOME" ] && [ -z "$(ls -A "$PTK_HOME" 2>/dev/null)" ]; then
        rmdir "$PTK_HOME"
    fi
    info 'ptk uninstalled.'
}

install_ptk() {
    rid="$(detect_rid)"
    assert_not_running
    info "Installing ptk for ${rid} into ${PTK_HOME}"

    if [ -n "$VERSION" ]; then
        tag="$VERSION"
        case "$tag" in v*) ;; *) tag="v${tag}" ;; esac
    else
        tag="$(
            if command -v curl >/dev/null 2>&1; then
                curl -fsSL "https://api.github.com/repos/${PTK_REPO}/releases/latest"
            else
                wget -qO- "https://api.github.com/repos/${PTK_REPO}/releases/latest"
            fi | grep '"tag_name"' | head -n1 | cut -d'"' -f4
        )"
        [ -n "$tag" ] || die 'could not determine the latest release tag'
    fi
    number="${tag#v}"
    asset="ptk-${number}-${rid}.tar.gz"
    base="https://github.com/${PTK_REPO}/releases/download/${tag}"
    info "Release ${tag}"

    staging="$(mktemp -d)"
    backup="$(mktemp -d)"
    # shellcheck disable=SC2064
    trap "rm -rf '$staging' '$backup'" EXIT INT TERM

    download "${base}/${asset}" "${staging}/${asset}"
    download "${base}/SHA256SUMS" "${staging}/SHA256SUMS"
    verify_checksum "${staging}/${asset}" "${staging}/SHA256SUMS" "$asset"
    info '  checksum verified'

    mkdir -p "${staging}/payload"
    tar -xzf "${staging}/${asset}" -C "${staging}/payload"
    [ -f "${staging}/payload/bin/PtkMcpServer" ] ||
        die 'the downloaded payload has no bin/PtkMcpServer; refusing to activate an incomplete install'

    # Snapshot the prior payload so a failure part-way through restores it.
    for entry in $PAYLOAD_ENTRIES; do
        [ -e "${PTK_HOME}/${entry}" ] && cp -R "${PTK_HOME}/${entry}" "${backup}/" || true
    done

    restore_backup() {
        printf 'install failed; restoring the previous payload\n' >&2
        for e in $PAYLOAD_ENTRIES; do
            rm -rf "${PTK_HOME:?}/${e}"
            [ -e "${backup}/${e}" ] && cp -R "${backup}/${e}" "${PTK_HOME}/" || true
        done
    }

    mkdir -p "$PTK_HOME"
    for entry in $PAYLOAD_ENTRIES; do
        rm -rf "${PTK_HOME:?}/${entry}"
        if [ -e "${staging}/payload/${entry}" ]; then
            cp -R "${staging}/payload/${entry}" "${PTK_HOME}/" || { restore_backup; exit 1; }
        fi
    done
    # This installer becomes the uninstall entry point, so it lives inside the
    # payload it manages.
    mkdir -p "${PTK_HOME}/scripts"
    cp "$0" "${PTK_HOME}/scripts/install.sh" 2>/dev/null || true
    chmod +x "${PTK_HOME}/bin/PtkMcpServer"
    [ -f "${PTK_HOME}/bin/PtkWorkerBroker" ] && chmod +x "${PTK_HOME}/bin/PtkWorkerBroker" || true

    # RTK before registration: a machine without it gets a server that refuses
    # to start, which is not a successful install.
    ensure_rtk "$rid" || { restore_backup; exit 1; }

    if command -v claude >/dev/null 2>&1; then
        claude mcp remove --scope user ptk >/dev/null 2>&1 || true
        if claude mcp add --scope user ptk "${PTK_HOME}/bin/PtkMcpServer"; then
            info 'Registered with Claude Code (user scope).'
        else
            info "Register manually: claude mcp add --scope user ptk \"${PTK_HOME}/bin/PtkMcpServer\""
        fi
    else
        info ''
        info 'Register ptk with your MCP harness using:'
        info "  claude mcp add --scope user ptk \"${PTK_HOME}/bin/PtkMcpServer\""
    fi

    rm -rf "$staging" "$backup"
    trap - EXIT INT TERM

    info ''
    info "ptk ${number} installed to ${PTK_HOME}"
    info 'Start a new harness session to pick it up.'
}

if [ "$UNINSTALL" -eq 1 ]; then
    uninstall_ptk
else
    install_ptk
fi
