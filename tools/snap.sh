#!/bin/zsh
# Snapshots of every page, popover, sheet and Settings tab in light and dark, on the sample book.
# Usage: tools/snap.sh            all pages
#        APP_SNAPSHOT_PAGES=manuscript,cast tools/snap.sh
set -u
app="${0:A:h:h}/.build/app/Inner Voice.app/Contents/MacOS/InnerVoice"
shots=/tmp/InnerVoice-shots
rm -rf ~/Library/Saved\ Application\ State/com.smanke.InnerVoice.savedState $shots
APP_SNAPSHOT=$shots "$app"
