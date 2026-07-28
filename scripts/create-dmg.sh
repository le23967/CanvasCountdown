#!/bin/bash
#
# Packages the app built by build-release.sh into a distributable DMG.
#
# The staging folder holds exactly two things: the app, and a symlink to
# /Applications so the volume window explains its own installation.

set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
DIST="$ROOT/dist"
APP_NAME="CanvasCountdown"
APP="$DIST/$APP_NAME.app"
VOLUME_NAME="Canvas Countdown"

if [ ! -d "$APP" ]; then
    echo "!! $APP not found. Run scripts/build-release.sh first." >&2
    exit 1
fi

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' \
    "$APP/Contents/Info.plist")"
DMG="$DIST/$APP_NAME-$VERSION.dmg"
STAGE="$DIST/dmg-staging"

echo "==> Staging $APP_NAME $VERSION"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
ditto "$APP" "$STAGE/$APP_NAME.app"
ln -s /Applications "$STAGE/Applications"

echo "==> Building the image"
hdiutil create \
    -volname "$VOLUME_NAME" \
    -srcfolder "$STAGE" \
    -ov \
    -format UDZO \
    "$DMG" \
    | sed 's/^/    /'

rm -rf "$STAGE"

echo "==> Verifying the image"
hdiutil verify "$DMG" | sed 's/^/    /'

MOUNT="$(mktemp -d)"
hdiutil attach "$DMG" -nobrowse -readonly -mountpoint "$MOUNT" >/dev/null
trap 'hdiutil detach "$MOUNT" >/dev/null 2>&1 || true; rmdir "$MOUNT" 2>/dev/null || true' EXIT

echo "    contents:"
ls -la "$MOUNT" | sed 's/^/      /'
[ -d "$MOUNT/$APP_NAME.app" ] || { echo "!! app missing from the image" >&2; exit 1; }
[ -L "$MOUNT/Applications" ] || { echo "!! Applications symlink missing" >&2; exit 1; }
echo "    mounted app version: $(/usr/libexec/PlistBuddy -c \
    'Print :CFBundleShortVersionString' "$MOUNT/$APP_NAME.app/Contents/Info.plist")"

# Nothing but the app and the symlink belongs on the volume.
UNEXPECTED="$(ls -A "$MOUNT" | grep -v -E "^($APP_NAME\.app|Applications|\.background|\.fseventsd|\.DS_Store|\.Trashes|\.VolumeIcon\.icns)$" || true)"
if [ -n "$UNEXPECTED" ]; then
    echo "!! unexpected items on the volume:" >&2
    echo "$UNEXPECTED" >&2
    exit 1
fi

hdiutil detach "$MOUNT" >/dev/null
rmdir "$MOUNT" 2>/dev/null || true
trap - EXIT

echo
echo "==> $DMG"
echo "    size:   $(du -h "$DMG" | cut -f1) ($(stat -f%z "$DMG") bytes)"
echo "    sha256: $(shasum -a 256 "$DMG" | cut -d' ' -f1)"
