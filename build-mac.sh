#!/usr/bin/env bash
#
# KEYMON.app 번들을 만드는 스크립트입니다. (macOS에서 실행)
#
#   ./build-mac.sh                   # 현재 맥 아키텍처에 맞춰 빌드 (자동 감지)
#   ./build-mac.sh --arch osx-x64    # 인텔 맥용으로 강제
#   ./build-mac.sh --menubar-only    # Dock 아이콘 없이 메뉴바에만 살게 함
#   ./build-mac.sh --framework-dependent   # .NET 런타임 설치 필요 (용량 작음)
#
# 왜 그냥 dotnet run이 아니라 .app 번들이 필요한가?
#   macOS는 '입력 모니터링' 권한을 앱 단위(번들 ID + 코드 서명)로 기억합니다.
#   dotnet run으로 띄우면 매번 다른 실행 파일로 인식돼 권한이 유지되지 않습니다.
#   .app으로 묶고 서명해 두면 한 번만 허용하면 계속 유지됩니다.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/src/Keymon.Cross.csproj"
OUT_DIR="$SCRIPT_DIR/dist"
APP_NAME="KEYMON"
BUNDLE_ID="com.keymon.app"
VERSION="1.0.0"

RID=""
MENUBAR_ONLY="false"
SELF_CONTAINED="true"

while [[ $# -gt 0 ]]; do
	case "$1" in
		--arch) RID="$2"; shift 2 ;;
		--menubar-only) MENUBAR_ONLY="true"; shift ;;
		--framework-dependent) SELF_CONTAINED="false"; shift ;;
		-h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}"; exit 0 ;;
		*) echo "알 수 없는 옵션: $1" >&2; exit 1 ;;
	esac
done

if [[ -z "$RID" ]]; then
	case "$(uname -m)" in
		arm64)  RID="osx-arm64" ;;   # Apple Silicon (M1~)
		x86_64) RID="osx-x64" ;;     # Intel
		*) echo "지원하지 않는 아키텍처: $(uname -m)" >&2; exit 1 ;;
	esac
fi

APP_DIR="$OUT_DIR/$APP_NAME.app"
MACOS_DIR="$APP_DIR/Contents/MacOS"
RESOURCES_DIR="$APP_DIR/Contents/Resources"

echo "▶ 대상 아키텍처 : $RID"
echo "▶ 자체 포함 배포 : $SELF_CONTAINED"
echo "▶ 메뉴바 전용    : $MENUBAR_ONLY"

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

echo "▶ 빌드 중..."
dotnet publish "$PROJECT" \
	-c Release \
	-r "$RID" \
	--self-contained "$SELF_CONTAINED" \
	-o "$MACOS_DIR"

# publish가 만든 .pdb는 배포본에 필요 없습니다.
rm -f "$MACOS_DIR"/*.pdb

chmod +x "$MACOS_DIR/Keymon"

echo "▶ Info.plist 작성..."
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleName</key>
	<string>$APP_NAME</string>
	<key>CFBundleDisplayName</key>
	<string>$APP_NAME</string>
	<key>CFBundleIdentifier</key>
	<string>$BUNDLE_ID</string>
	<key>CFBundleExecutable</key>
	<string>Keymon</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>LSMinimumSystemVersion</key>
	<string>11.0</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<!-- true면 Dock에 뜨지 않고 메뉴바 아이콘으로만 존재합니다. -->
	<key>LSUIElement</key>
	<$( [[ "$MENUBAR_ONLY" == "true" ]] && echo "true" || echo "false" )/>
</dict>
</plist>
PLIST

echo "▶ 애드혹 코드 서명..."
# 서명이 없으면 macOS가 매 실행마다 다른 앱으로 취급해서
# 입력 모니터링 권한이 유지되지 않습니다. 로컬 애드혹(-) 서명이면 충분합니다.
codesign --force --deep --sign - "$APP_DIR"

echo ""
echo "✅ 완료: $APP_DIR"
echo ""
echo "다음 순서로 실행하세요."
echo "  1) open \"$OUT_DIR\"  → KEYMON.app 을 응용 프로그램 폴더로 끌어다 놓기"
echo "  2) KEYMON.app 실행 → 권한 요청 팝업에서 '허용'"
echo "  3) 시스템 설정 → 개인정보 보호 및 보안 → 입력 모니터링 → KEYMON 켜기"
echo "  4) KEYMON을 완전히 종료했다가 다시 실행 (권한은 재시작해야 적용됩니다)"
