#!/bin/bash
# Submits the built app to Apple for notarization, then staples the ticket
# so it launches cleanly on other Macs without a Gatekeeper warning.
#
# Notarization credentials are account-level, not per-app: any profile already
# stored for another of these apps works, and resolve_profile picks the first
# that answers. If none does, run this yourself once (it asks for your
# app-specific password and saves it in the keychain, so this script never
# handles the password):
#
#   xcrun notarytool store-credentials "DesktopBins" \
#     --apple-id "<your-apple-id>" \
#     --team-id "32CWL275JJ"
#
# The keychain profiles can't be read while the screen is locked.
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="Inner Voice"
APP_DIR=".build/app/${APP_NAME}.app"
ZIP_PATH=".build/app/InnerVoice-notarize.zip"

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

if [ -z "${PROFILE}" ]; then
  echo "No stored notarization credentials found (is the screen locked?)."
  echo "Run the store-credentials command in the header of this script first."
  exit 1
fi

# Only a Developer ID signature can be notarized; build.sh falls back to Apple Development
# or ad hoc when it finds none.
AUTHORITY=$(codesign -dvv "${APP_DIR}" 2>&1 | grep "^Authority=Developer ID Application" || true)
if [ -z "${AUTHORITY}" ]; then
  echo "The app isn't signed with a Developer ID Application certificate. Rebuild with ./build.sh --universal."
  exit 1
fi

# Notarization rejects anything signed without a secure timestamp.
# Note: no `grep -q` here — it exits on the first match, which SIGPIPEs
# codesign, and under `set -o pipefail` that reads as a failed check even
# when the timestamp is present.
TIMESTAMP_LINE=$(codesign -dvv "${APP_DIR}" 2>&1 | grep "^Timestamp=" || true)
if [ -z "${TIMESTAMP_LINE}" ]; then
  echo "Signature has no secure timestamp. Re-run ./build.sh (it signs with --timestamp)."
  exit 1
fi
echo "Secure ${TIMESTAMP_LINE}"

echo "Zipping bundle for submission..."
rm -f "${ZIP_PATH}"
# ditto preserves the bundle structure and symlinks (llama.framework has both); plain zip does not.
ditto -c -k --keepParent "${APP_DIR}" "${ZIP_PATH}"

echo "Submitting to Apple with profile ${PROFILE} (this usually takes a few minutes)..."
xcrun notarytool submit "${ZIP_PATH}" --keychain-profile "${PROFILE}" --wait

echo "Stapling the ticket to the app..."
xcrun stapler staple "${APP_DIR}"
rm -f "${ZIP_PATH}"

echo "Verifying with Gatekeeper..."
spctl -a -vv "${APP_DIR}"

echo
echo "Done: ${APP_DIR} is notarized and stapled. Next: ./make_dmg.sh"
