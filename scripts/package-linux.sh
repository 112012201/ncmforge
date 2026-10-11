#!/usr/bin/env bash
#
# Builds the Linux distribution formats from an already-published folder.
#
# Inputs (environment):
#   PUBLISHED_DIR  folder produced by `dotnet publish` (required)
#   VERSION        version without a leading v, e.g. 1.0.0-rc2   (required)
#   ARCH           deb/rpm architecture: amd64 or arm64          (required)
#   OUT_DIR        where the packages are written                (default: dist-linux)
#   ICON           path to a 256x256 PNG for the desktop entry   (optional)
#   SRC_TARBALL    prebuilt source tarball to leave alone        (optional)
#
# Produces, in OUT_DIR:
#   ncmforge_<debian-version>_<arch>.deb
#   ncmforge-<rpm-version>-1.<rpm-arch>.rpm
#   ncmforge_<snap-version>_<arch>.snap
#   NcmForge-<version>-linux-<arch>.tar.gz
#
# Each format is built only when its tool is present, so the same script can be run on a developer
# machine that has some of them and in CI where all of them are installed.

set -euo pipefail

PUBLISHED_DIR="${PUBLISHED_DIR:?PUBLISHED_DIR is required}"
VERSION="${VERSION:?VERSION is required}"
ARCH="${ARCH:?ARCH is required}"
OUT_DIR="${OUT_DIR:-dist-linux}"
ICON="${ICON:-}"

if [ ! -d "$PUBLISHED_DIR" ]; then
  echo "published directory not found: $PUBLISHED_DIR" >&2
  exit 1
fi

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
PUBLISHED_DIR="$(cd "$PUBLISHED_DIR" && pwd)"

echo "packaging NcmForge $VERSION for $ARCH"
echo "  from : $PUBLISHED_DIR"
echo "  to   : $OUT_DIR"

# ---------------------------------------------------------------------------
# Version spellings
#
# A pre-release is "1.0.0-rc2" or "1.2.4152-alpha-rc5" upstream. Both Debian and RPM need it spelled
# so the pre-release sorts BEFORE the release it precedes, which is what "~" does - otherwise the
# candidate looks newer than the release and package managers refuse to upgrade to it.
#
# The two differ on one point. RPM separates Version from Release with "-", so a hyphen is ILLEGAL
# anywhere in its Version field ("Illegal char '-' (0x2d)"), and the suffix's own hyphens have to
# become "_". Debian accepts hyphens in the upstream part as long as a revision follows, which it
# always does here.
#
# Splitting on the FIRST hyphen puts the "~" immediately after the numeric core, so it governs the
# whole comparison rather than only the tail.
# ---------------------------------------------------------------------------
case "$VERSION" in
  *-*)
    VERSION_CORE="${VERSION%%-*}"      # 1.2.4152
    VERSION_SUFFIX="${VERSION#*-}"     # alpha-rc5
    DEB_VERSION="${VERSION_CORE}~${VERSION_SUFFIX}"
    RPM_VERSION="${VERSION_CORE}~${VERSION_SUFFIX//-/_}"
    ;;
  *)
    DEB_VERSION="$VERSION"
    RPM_VERSION="$VERSION"
    ;;
esac

# Debian revision, always 1 here: the packaging itself has not been revised.
DEB_FULL_VERSION="${DEB_VERSION}-1"

case "$ARCH" in
  amd64) RPM_ARCH="x86_64"; SNAP_ARCH="amd64" ;;
  arm64) RPM_ARCH="aarch64"; SNAP_ARCH="arm64" ;;
  *) echo "unsupported ARCH: $ARCH" >&2; exit 1 ;;
esac

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

SUMMARY="NetEase Cloud Music .ncm converter"
DESCRIPTION="Decrypts NetEase Cloud Music .ncm containers and writes tagged MP3, FLAC, AAC, ALAC, WavPack and Matroska output, including AVS3-P3 (Audio Vivid) immersive tracks."

# Writes a small launcher script, used wherever a binary lives outside PATH (/usr/lib, a snap).
make_launcher() {
  local target="$1" binary="$2"
  cat > "$target" <<EOF
#!/bin/sh
exec "$binary" "\$@"
EOF
  chmod 0755 "$target"
}

# The freedesktop entry, shared by the deb and the snap.
write_desktop_file() {
  local target="$1"
  cat > "$target" <<'EOF'
[Desktop Entry]
Type=Application
Name=NcmForge
Name[zh_CN]=NcmForge 网易云音乐 NCM 转换器
Comment=Convert NetEase Cloud Music .ncm files
Comment[zh_CN]=网易云音乐 NCM 解密与转码
Exec=ncmforge-gui %F
Icon=ncmforge
Terminal=false
Categories=AudioVideo;Audio;Utility;
MimeType=application/octet-stream;
StartupWMClass=NcmForge
EOF
}

# A stable copy with the legal files, reused by every format.
APP_ROOT="$STAGE/app"
mkdir -p "$APP_ROOT"
cp -a "$PUBLISHED_DIR"/. "$APP_ROOT"/

if [ ! -f "$APP_ROOT/ncmforge" ]; then
  echo "warning: $APP_ROOT/ncmforge is missing - the CLI may not have been published" >&2
fi

# ---------------------------------------------------------------------------
# 1. Plain binary tarball
#
# SKIP_TARBALL=1 is for the release pipeline, where the build job has already produced the tarball
# under its own rid name. Deleting the duplicate afterwards is fiddly and left an orphaned checksum
# behind once, so the script simply does not make it.
# ---------------------------------------------------------------------------
if [ "${SKIP_TARBALL:-0}" != "1" ]; then
  TARBALL="NcmForge-$VERSION-linux-$ARCH.tar.gz"
  echo "==> $TARBALL"
  tar -czf "$OUT_DIR/$TARBALL" -C "$APP_ROOT" .
else
  echo "==> skipping the binary tarball (SKIP_TARBALL=1; the build job already made it)"
fi

# ---------------------------------------------------------------------------
# 2. Debian package
# ---------------------------------------------------------------------------
if command -v dpkg-deb >/dev/null 2>&1; then
  DEB_ROOT="$STAGE/deb"
  mkdir -p "$DEB_ROOT/DEBIAN" \
           "$DEB_ROOT/usr/lib/ncmforge" \
           "$DEB_ROOT/usr/bin" \
           "$DEB_ROOT/usr/share/applications" \
           "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps" \
           "$DEB_ROOT/usr/share/doc/ncmforge"

  cp -a "$APP_ROOT"/. "$DEB_ROOT/usr/lib/ncmforge/"

  # A GUI binary in /usr/lib is not on PATH, so both frontends get a launcher in /usr/bin.
  ln -sf /usr/lib/ncmforge/ncmforge "$DEB_ROOT/usr/bin/ncmforge"
  make_launcher "$DEB_ROOT/usr/bin/ncmforge-gui" /usr/lib/ncmforge/NcmForge

  write_desktop_file "$DEB_ROOT/usr/share/applications/ncmforge.desktop"
  if [ -n "$ICON" ] && [ -f "$ICON" ]; then
    cp "$ICON" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/ncmforge.png"
  fi

  cp "$APP_ROOT/LICENSE" "$DEB_ROOT/usr/share/doc/ncmforge/copyright" 2>/dev/null || true
  for legal in DISCLAIMER.md THIRD-PARTY-NOTICES.md; do
    [ -f "$APP_ROOT/$legal" ] && cp "$APP_ROOT/$legal" "$DEB_ROOT/usr/share/doc/ncmforge/"
  done

  # Installed size in KiB, as the control field expects.
  INSTALLED_SIZE="$(du -sk "$DEB_ROOT/usr" | cut -f1)"

  cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: ncmforge
Version: $DEB_FULL_VERSION
Section: sound
Priority: optional
Architecture: $ARCH
Installed-Size: $INSTALLED_SIZE
Maintainer: NcmForge contributors <1422769173@qq.com>
Homepage: https://github.com/112012201/ncmforge
Description: $SUMMARY
 $DESCRIPTION
 .
 Both a command line tool (ncmforge) and a graphical interface (ncmforge-gui) are installed.
EOF

  # The CLI is the primary entry point; the GUI needs a graphical session.
  cat > "$DEB_ROOT/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications || true
fi
exit 0
EOF
  chmod 0755 "$DEB_ROOT/DEBIAN/postinst"

  chmod 0755 "$DEB_ROOT/usr/lib/ncmforge/ncmforge" "$DEB_ROOT/usr/lib/ncmforge/NcmForge" 2>/dev/null || true

  DEB_NAME="ncmforge_${DEB_FULL_VERSION}_${ARCH}.deb"
  echo "==> $DEB_NAME"
  dpkg-deb --root-owner-group --build "$DEB_ROOT" "$OUT_DIR/$DEB_NAME"
else
  echo "==> skipping .deb (dpkg-deb not installed)"
fi

# ---------------------------------------------------------------------------
# 3. RPM package
# ---------------------------------------------------------------------------
if command -v rpmbuild >/dev/null 2>&1; then
  RPM_TOP="$STAGE/rpmbuild"
  mkdir -p "$RPM_TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}

  # rpmbuild wants a source tarball whose top-level directory matches Name-Version.
  RPM_SRC_ROOT="$STAGE/ncmforge-$VERSION"
  mkdir -p "$RPM_SRC_ROOT"
  cp -a "$APP_ROOT"/. "$RPM_SRC_ROOT"/
  tar -czf "$RPM_TOP/SOURCES/ncmforge-$VERSION.tar.gz" -C "$STAGE" "ncmforge-$VERSION"

  cat > "$RPM_TOP/SPECS/ncmforge.spec" <<EOF
Name:           ncmforge
Version:        $RPM_VERSION
Release:        1%{?dist}
Summary:        $SUMMARY
License:        GPL-3.0-or-later
URL:            https://github.com/112012201/ncmforge
Source0:        ncmforge-$VERSION.tar.gz
AutoReqProv:    no

%description
$DESCRIPTION

Installs both a command line tool (ncmforge) and a graphical interface (ncmforge-gui).

%prep
%setup -q -n ncmforge-$VERSION

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}/usr/lib/ncmforge %{buildroot}/usr/bin
cp -a . %{buildroot}/usr/lib/ncmforge/
ln -sf /usr/lib/ncmforge/ncmforge %{buildroot}/usr/bin/ncmforge
cat > %{buildroot}/usr/bin/ncmforge-gui <<'LAUNCHER'
#!/bin/sh
exec /usr/lib/ncmforge/NcmForge "\$@"
LAUNCHER
chmod 0755 %{buildroot}/usr/bin/ncmforge-gui
mkdir -p %{buildroot}/usr/share/applications %{buildroot}/usr/share/icons/hicolor/256x256/apps %{buildroot}/usr/share/doc/ncmforge
cp %{buildroot}/usr/lib/ncmforge/LICENSE %{buildroot}/usr/share/doc/ncmforge/ 2>/dev/null || true
cp %{buildroot}/usr/lib/ncmforge/DISCLAIMER.md %{buildroot}/usr/share/doc/ncmforge/ 2>/dev/null || true
cp %{buildroot}/usr/lib/ncmforge/THIRD-PARTY-NOTICES.md %{buildroot}/usr/share/doc/ncmforge/ 2>/dev/null || true

%files
/usr/lib/ncmforge
/usr/bin/ncmforge
/usr/bin/ncmforge-gui
/usr/share/doc/ncmforge

%changelog
* $(LC_ALL=C date '+%a %b %d %Y') NcmForge contributors <1422769173@qq.com> - $RPM_VERSION-1
- Automated build
EOF

  echo "==> ncmforge-$RPM_VERSION-1.$RPM_ARCH.rpm"
  # The package architecture is taken from _target_cpu, and BuildArch is deliberately ABSENT from
  # the spec above. A BuildArch field makes rpmbuild first ask whether the host is able to build
  # that architecture, which for aarch64 on an x86_64 runner is false, and it then aborts with
  # "No compatible architectures found for build" even when --target is supplied. This package only
  # repackages already-published output, so there is no compilation step for that check to guard.
  #
  # A failure here is reported but not fatal: one architecture's rpm is not worth denying everyone
  # the deb, the snap and the tarball.
  if rpmbuild --define "_topdir $RPM_TOP" \
              --define "_binary_payload w2.xzdio" \
              --define "_target_cpu $RPM_ARCH" \
              --define "_target_os linux" \
              --target "$RPM_ARCH" \
              -bb "$RPM_TOP/SPECS/ncmforge.spec" > "$STAGE/rpm-$ARCH.log" 2>&1; then
    find "$RPM_TOP/RPMS" -name '*.rpm' -exec cp {} "$OUT_DIR/" \;
  else
    echo "warning: rpmbuild failed for $RPM_ARCH - no .rpm will be published for this architecture" >&2
    tail -n 25 "$STAGE/rpm-$ARCH.log" >&2
    # Also surface it on the run summary: a step that reports success while quietly dropping an
    # artifact is how the aarch64 rpm went missing once already.
    echo "::warning title=rpmbuild failed for $RPM_ARCH::no .rpm will be published for $RPM_ARCH"
  fi
else
  echo "==> skipping .rpm (rpmbuild not installed)"
fi

# ---------------------------------------------------------------------------
# 4. Snap
#
# Built with mksquashfs rather than snapcraft: a snap is a squashfs image with a meta/snap.yaml,
# and snapcraft would drag in LXD or need destructive mode on a matching host. This is faster,
# deterministic, and works on any runner that has squashfs-tools.
# ---------------------------------------------------------------------------
if command -v mksquashfs >/dev/null 2>&1; then
  SNAP_ROOT="$STAGE/snap"
  mkdir -p "$SNAP_ROOT/meta/gui"
  cp -a "$APP_ROOT"/. "$SNAP_ROOT"/

  chmod 0755 "$SNAP_ROOT/ncmforge" "$SNAP_ROOT/NcmForge" 2>/dev/null || true

  cat > "$SNAP_ROOT/meta/snap.yaml" <<EOF
name: ncmforge
base: core22
version: '$VERSION'
summary: $SUMMARY
description: |
  $DESCRIPTION

  Installs both a command line tool (ncmforge) and a graphical interface (NcmForge).

  Audio Vivid (AVS3-P3) conversion needs an ffmpeg built with the av3a decoder placed in
  tools/ffmpeg/ inside the snap, or available on PATH.

grade: stable
confinement: classic
architectures:
  - $SNAP_ARCH

apps:
  ncmforge:
    command: ncmforge
  ncmforge-gui:
    command: NcmForge
EOF

  if [ -n "$ICON" ] && [ -f "$ICON" ]; then
    cp "$ICON" "$SNAP_ROOT/meta/gui/ncmforge.png"
  fi
  write_desktop_file "$SNAP_ROOT/meta/gui/ncmforge.desktop"

  SNAP_NAME="ncmforge_${VERSION}_${SNAP_ARCH}.snap"
  echo "==> $SNAP_NAME"
  # -noappend so repeated runs do not grow the image; -all-root because snap files must be root-owned.
  if ! mksquashfs "$SNAP_ROOT" "$OUT_DIR/$SNAP_NAME" -noappend -all-root -comp xz -quiet; then
    echo "warning: mksquashfs failed - no .snap will be published for this architecture" >&2
    rm -f "$OUT_DIR/$SNAP_NAME"
  fi
else
  echo "==> skipping .snap (mksquashfs not installed)"
fi

# ---------------------------------------------------------------------------
# Checksums for everything produced here
# ---------------------------------------------------------------------------
( cd "$OUT_DIR" && for f in *; do
    case "$f" in
      *.sha256) continue ;;
    esac
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$f" > "$f.sha256"; else shasum -a 256 "$f" > "$f.sha256"; fi
  done )

echo
echo "produced:"
ls -la "$OUT_DIR"
