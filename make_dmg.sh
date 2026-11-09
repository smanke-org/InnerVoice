#!/bin/bash
# Builds a distributable .dmg containing the app, with the usual
# drag-to-Applications layout: .build/app/InnerVoice-<version>.dmg, plus the
# same image as .build/app/InnerVoice.dmg (the fixed name the updater and the
# README's download link use).
#
# Run ./build.sh --universal and ./notarize.sh first: the app inside should
# already carry a stapled notarization ticket, otherwise anyone who downloads
# the image gets a Gatekeeper block on first launch. This script then signs,
# notarizes and staples the .dmg itself, so the download is clean too.
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="InnerVoice"
APP_DIR=".build/app/${APP_NAME}.app"
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${APP_DIR}/Contents/Info.plist")
DMG_PATH=".build/app/InnerVoice-${VERSION}.dmg"
STABLE_PATH=".build/app/InnerVoice.dmg"

resolve_profile() {
  for candidate in "${NOTARY_PROFILE:-}" "InnerVoice" "DesktopBins" "StayMounted" "NetworkToggle" "DesktopBinsWidget"; do
    [ -z "${candidate}" ] && continue
    if xcrun notarytool history --keychain-profile "${candidate}" >/dev/null 2>&1; then
      echo "${candidate}"
      return 0
    fi
  done
  return 1
}

PROFILE=$(resolve_profile || echo "")

if [ ! -d "${APP_DIR}" ]; then
  echo "No app bundle at ${APP_DIR} — run ./build.sh --universal first."
  exit 1
fi

# Warn rather than fail: a DMG of an unstapled app is still useful locally.
if ! xcrun stapler validate "${APP_DIR}" >/dev/null 2>&1; then
  echo "WARNING: the app has no stapled notarization ticket."
  echo "         Run ./notarize.sh first, or downloads will be blocked by Gatekeeper."
fi

SIGN_IDENTITY="${CODESIGN_IDENTITY:-}"
if [ -z "${SIGN_IDENTITY}" ]; then
  SIGN_IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null \
    | grep "Developer ID Application" | head -n 1 | sed -E 's/.*"(.*)".*/\1/' || true)
fi

echo "Staging disk image contents..."
STAGING=$(mktemp -d)
trap 'rm -rf "${STAGING}"' EXIT
cp -R "${APP_DIR}" "${STAGING}/"
# The familiar drag-the-app-onto-Applications install gesture.
ln -s /Applications "${STAGING}/Applications"

# The window layout — 600x400, 128px icons, app on the left and Applications on the
# right — ships as a captured .DS_Store rather than by driving Finder at release time.
# Finder scripting is too unreliable to sit in a release path (on macOS 27 it silently
# ignores a background picture set that way), and a fixture makes every build identical.
# Capture it once with:
#   tools/capture_dmg_layout.sh ".build/app/InnerVoice.app" "InnerVoice" Resources/dmg/DS_Store
if [ -f "Resources/dmg/DS_Store" ]; then
  cp "Resources/dmg/DS_Store" "${STAGING}/.DS_Store"
else
  echo "NOTE: no Resources/dmg/DS_Store yet; the image opens with Finder's default layout."
fi

echo "Creating ${DMG_PATH}..."
rm -f "${DMG_PATH}" "${STABLE_PATH}"
# hdiutil, not `diskutil image create from`: on macOS 27 diskutil silently leaves the
# staging folder's .DS_Store out of the image, so the window layout above never shipped
# (Inner Voice 0.1.0's first upload). An APFS volume in LZFSE (ULFO) keeps it; HFS+ zlib
# images of app folders came out corrupt on macOS 27.0.1. The updater still mounts it
# with `diskutil image attach`. hdiutil prints a deprecation warning; it is harmless.
hdiutil create -quiet \
  -srcfolder "${STAGING}" \
  -volname "${APP_NAME}" \
  -fs APFS \
  -format ULFO \
  "${DMG_PATH}" 2>/dev/null
# The layout must be in the image: check before anything is signed or published.
LAYOUT_MOUNT=$(hdiutil attach "${DMG_PATH}" -nobrowse -noautoopen -readonly 2>/dev/null | awk -F'\t' '/\/Volumes\//{print $NF}')
HAS_LAYOUT=no
[ -n "${LAYOUT_MOUNT}" ] && [ -f "${LAYOUT_MOUNT}/.DS_Store" ] && HAS_LAYOUT=yes
[ -n "${LAYOUT_MOUNT}" ] && hdiutil detach "${LAYOUT_MOUNT}" -quiet || true
if [ -f "Resources/dmg/DS_Store" ] && [ "${HAS_LAYOUT}" != "yes" ]; then
  echo "The disk image lost its window layout (.DS_Store); not signing or publishing it." >&2
  exit 1
fi
# Never sign or publish an unreadable image (hdiutil's zlib images of HFS+ volumes came out
# corrupt on macOS 27.0.1; this APFS one isn't, but the check costs nothing). No grep -q:
# under pipefail its early exit reads as a failure.
INFO=$(hdiutil imageinfo "${DMG_PATH}" 2>/dev/null | grep "Format Description" || true)
if [ -z "${INFO}" ]; then
  echo "The disk image came out unreadable; not signing or publishing it." >&2
  exit 1
fi

if [ -n "${SIGN_IDENTITY}" ]; then
  echo "Signing the disk image with: ${SIGN_IDENTITY}"
  codesign --force --timestamp --sign "${SIGN_IDENTITY}" "${DMG_PATH}"

  if [ -n "${PROFILE}" ]; then
    echo "Notarizing the disk image with profile ${PROFILE} (a few minutes)..."
    xcrun notarytool submit "${DMG_PATH}" --keychain-profile "${PROFILE}" --wait
    xcrun stapler staple "${DMG_PATH}"
  elif [ -n "${REQUIRE_NOTARIZED:-}" ]; then
    # release.sh sets this: an un-notarized image must never be published.
    echo "No notarization profile answers (is the screen locked?); refusing to continue." >&2
    exit 1
  else
    echo "WARNING: no notarization profile answers — skipping notarization of the image."
  fi
else
  if [ -n "${REQUIRE_NOTARIZED:-}" ]; then
    echo "No Developer ID identity found; refusing to continue." >&2
    exit 1
  fi
  echo "WARNING: no Developer ID identity found; the image is unsigned."
fi

# A second copy under a name that never changes. The README's download link uses
# GitHub's releases/latest/download/<name> redirect, which can only resolve against an
# asset whose name is identical in every release, and the updater looks for it first.
# Copied after notarization so it carries the same stapled ticket.
cp "${DMG_PATH}" "${STABLE_PATH}"

echo
echo "Verifying..."
spctl -a -t open --context context:primary-signature -vv "${DMG_PATH}" 2>&1 || true
ls -lh "${DMG_PATH}" | awk '{print "Size: "$5}'
echo "Done: ${DMG_PATH}"
echo "      ${STABLE_PATH} (same image, fixed name for the updater and the permanent download link)"
