#!/bin/bash
# Cuts a release: Apple silicon build (MLX speech models need it), notarize, disk image, GitHub release at smanke-org/InnerVoice.
#
# One command rather than four remembered ones: a release published by hand without the
# fixed-name disk image once broke the README's download link (and here the updater, which
# looks for InnerVoice.dmg) for everyone.
#
# Usage: ./release.sh [version]          RELEASE_NOTES="…" ./release.sh 0.1.1
#   With no argument, releases whatever version Info.plist.in already carries.
#   Versions are A.B.C with C from 0 to 9: after 1.1.9 comes 1.2.0, never 1.1.10.
set -euo pipefail

cd "$(dirname "$0")"

PLIST="Info.plist.in"
REPO="smanke-org/InnerVoice"

# Check the tree before touching anything: setting the version first would trip this guard
# with the script's own edit.
if [ -n "$(git status --porcelain)" ]; then
  echo "Working tree is dirty. Commit before releasing so the tag matches what shipped." >&2
  exit 1
fi

if [ $# -ge 1 ]; then
  VERSION="$1"
else
  VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${PLIST}")
fi
if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]$ ]]; then
  echo "Version ${VERSION} isn't A.B.C with C from 0 to 9 (after x.y.9 comes x.(y+1).0)." >&2
  exit 1
fi
TAG="v${VERSION}"
# The beta label goes in the titles only: a GitHub pre-release is skipped by the
# releases/latest lookups the updater and the README's download link rely on.
STAGE=$(/usr/libexec/PlistBuddy -c "Print :InnerVoiceReleaseStage" "${PLIST}" 2>/dev/null || true)

# Other sessions release too: compare with what is actually published.
LATEST=$(gh release list --repo "${REPO}" --limit 1 --json tagName --jq '.[0].tagName' 2>/dev/null || true)
echo "==> Latest published release: ${LATEST:-none}"
if gh release view "${TAG}" --repo "${REPO}" >/dev/null 2>&1; then
  echo "Release ${TAG} already exists. Bump the version first." >&2
  exit 1
fi

CURRENT=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${PLIST}")
if [ "${VERSION}" != "${CURRENT}" ]; then
  echo "==> Setting version to ${VERSION}"
  /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${VERSION}" "${PLIST}"
  # Commit it, so the tag points at a commit carrying the version that shipped.
  git add "${PLIST}"
  git commit -q -m "Bump to ${VERSION}"
  git push -q origin HEAD
fi

# Only a Developer ID build can be notarized; never fall back to another identity here.
IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 -o '"Developer ID Application: [^"]*"' | tr -d '"' || true)
if [ -z "${IDENTITY}" ]; then
  echo "No Developer ID Application identity in the keychain." >&2
  exit 1
fi

echo "==> Building ${VERSION} (Apple silicon) with ${IDENTITY}"
# build.sh deletes the product first and refuses a binary the build didn't write, so the
# bundle is provably this build. BUILD_NUMBER makes CFBundleVersion the version too.
SIGN_IDENTITY="${IDENTITY}" BUILD_NUMBER="${VERSION}" ./build.sh
APP=".build/app/InnerVoice.app"
BUILT=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${APP}/Contents/Info.plist")
[ "${BUILT}" = "${VERSION}" ] || { echo "Built ${BUILT}, expected ${VERSION}." >&2; exit 1; }
ARCHS=$(lipo -archs "${APP}/Contents/MacOS/InnerVoice")
case "${ARCHS}" in
  arm64) echo "    architectures: ${ARCHS}" ;;
  *) echo "The binary isn't Apple silicon (${ARCHS})." >&2; exit 1 ;;
esac
[ -f "${APP}/Contents/Resources/mlx-swift_Cmlx.bundle/Contents/Resources/default.metallib" ] || { echo "The MLX Metal library is missing." >&2; exit 1; }

./notarize.sh
REQUIRE_NOTARIZED=1 ./make_dmg.sh

VERSIONED=".build/app/InnerVoice-${VERSION}.dmg"
STABLE=".build/app/InnerVoice.dmg"
for f in "${VERSIONED}" "${STABLE}"; do
  [ -f "$f" ] || { echo "Missing ${f}." >&2; exit 1; }
  xcrun stapler validate "$f" >/dev/null || { echo "${f} has no stapled ticket." >&2; exit 1; }
done

echo "==> Tagging ${TAG}"
git tag -a "${TAG}" -m "InnerVoice ${VERSION}${STAGE:+ ${STAGE}}"
git push origin "${TAG}"

echo "==> Publishing ${TAG}"
# Both assets go up: the version-stamped one to pin to, and the fixed-name one the updater
# and the README's permanent download link use.
gh release create "${TAG}" "${VERSIONED}" "${STABLE}" --repo "${REPO}" \
  --title "InnerVoice ${VERSION}${STAGE:+ ${STAGE}}" \
  --notes "${RELEASE_NOTES:-See the commit history for what changed in ${VERSION}.}"

echo "==> Verifying the permanent download link"
# GitHub takes a moment to point the latest-release redirect at a new asset, so this
# retries rather than reporting a failure that would have resolved on its own.
URL="https://github.com/${REPO}/releases/latest/download/InnerVoice.dmg"
for attempt in 1 2 3 4 5 6; do
  CODE=$(curl -sL -o /dev/null -w "%{http_code}" "${URL}")
  if [ "${CODE}" = "200" ]; then
    echo "    ${URL} -> 200"
    echo
    echo "Released ${TAG}."
    exit 0
  fi
  echo "    attempt ${attempt}: ${CODE}, waiting..."
  sleep 10
done

echo "The permanent download link is still not resolving (${CODE}). Check the release assets." >&2
exit 1
