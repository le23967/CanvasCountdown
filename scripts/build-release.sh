#!/bin/bash
#
# Builds Canvas Countdown for distribution from a clean state.
#
# Everything it produces lands in dist/, which Git ignores: no build output
# belongs in the repository. Run create-dmg.sh afterwards to package it.

set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
DIST="$ROOT/dist"
DERIVED="$DIST/DerivedData"
APP_NAME="CanvasCountdown"
SCHEME="CanvasCountdown"

echo "==> Regenerating the project from project.yml"
if command -v xcodegen >/dev/null 2>&1; then
    xcodegen generate
else
    echo "    xcodegen not installed; using the checked-in project as-is"
fi

echo "==> Clearing $DIST"
rm -rf "$DIST"
mkdir -p "$DERIVED"

echo "==> Clean Release build"
xcodebuild \
    -project "$APP_NAME.xcodeproj" \
    -scheme "$SCHEME" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED" \
    -configuration Release \
    clean build \
    | grep -E '^(warning|error|\*\*)' || true

APP="$DERIVED/Build/Products/Release/$APP_NAME.app"
if [ ! -d "$APP" ]; then
    echo "!! Release build produced no app bundle" >&2
    exit 1
fi

echo "==> Copying the app to dist/"
# ditto rather than cp: it preserves the bundle's metadata and signature.
ditto "$APP" "$DIST/$APP_NAME.app"
APP="$DIST/$APP_NAME.app"

echo "==> Bundle metadata"
PLIST="$APP/Contents/Info.plist"
for key in CFBundleDisplayName CFBundleIdentifier CFBundleShortVersionString \
           CFBundleVersion LSMinimumSystemVersion; do
    printf '    %-28s %s\n' "$key" \
        "$(/usr/libexec/PlistBuddy -c "Print :$key" "$PLIST")"
done

echo "==> Architectures"
# One download has to run on both kinds of Mac. Xcode builds both by default,
# which is exactly why this is worth checking: a setting changed by accident
# would produce a build that silently refuses to open on half the machines it
# was published for, and nothing else here would notice.
ARCHS_BUILT="$(lipo -archs "$APP/Contents/MacOS/$APP_NAME")"
echo "    $ARCHS_BUILT"
for arch in arm64 x86_64; do
    case " $ARCHS_BUILT " in
        *" $arch "*) ;;
        *)
            echo "!! $arch missing: this build would not run on every supported Mac" >&2
            exit 1
            ;;
    esac
done
echo "    Apple silicon and Intel are both covered"

echo "==> Signature"
codesign --verify --strict --verbose=2 "$APP" 2>&1 | sed 's/^/    /'
codesign -dv "$APP" 2>&1 | grep -E 'Identifier|Signature|TeamIdentifier|flags' | sed 's/^/    /'

echo "==> Checking the bundle carries nothing private"
if find "$APP" -type f \( -name '*.ics' -o -name '*.xctest' -o -name '*.log' \) | grep -q .; then
    echo "!! Unexpected fixture, test bundle or log inside the app" >&2
    find "$APP" -type f \( -name '*.ics' -o -name '*.xctest' -o -name '*.log' \) >&2
    exit 1
fi
echo "    no fixtures, test bundles or logs in the bundle"
echo "    files: $(find "$APP" -type f | wc -l | tr -d ' '), size: $(du -sh "$APP" | cut -f1)"

echo
echo "Built $APP"
