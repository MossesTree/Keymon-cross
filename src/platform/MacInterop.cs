using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Keymon.Platform
{
    // macOS 네이티브 API를 C#에서 직접 부르기 위한 P/Invoke 모음입니다.
    //
    // Windows에서 user32.dll을 [DllImport]로 부르는 것과 원리가 똑같습니다.
    // 다만 macOS의 UI 계층은 Objective-C 런타임 위에 있어서,
    // "함수를 부른다"가 아니라 "객체에 메시지를 보낸다(objc_msgSend)"는 형태를 씁니다.
    //
    //   [NSWorkspace sharedWorkspace]            → objc_msgSend(클래스, sel("sharedWorkspace"))
    //   [ws frontmostApplication]                → objc_msgSend(ws, sel("frontmostApplication"))
    //   [app processIdentifier]                  → objc_msgSend_int(app, sel("processIdentifier"))
    //
    // objc_msgSend는 가변 인자 함수라서, 반환 타입이 다르면 서로 다른 이름으로
    // 각각 선언해 주어야 합니다(arm64 호출 규약 때문). 그래서 EntryPoint를 씁니다.
    [SupportedOSPlatform("macos")]
    internal static class MacInterop
    {
        private const string ObjC = "/usr/lib/libobjc.dylib";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

        // ── Objective-C 런타임 ─────────────────────────────────────
        [DllImport(ObjC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
        private static extern IntPtr ObjcGetClass(string name);

        [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
        private static extern IntPtr SelRegisterName(string name);

        // 반환값이 객체 포인터(id)인 경우
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr MsgSendPtr(IntPtr receiver, IntPtr selector);

        // 반환값이 32비트 정수인 경우 (pid_t 등)
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern int MsgSendInt(IntPtr receiver, IntPtr selector);

        // ── dlopen: 프레임워크가 아직 로드되지 않았을 때 대비 ────────
        [DllImport("/usr/lib/libSystem.dylib", CharSet = CharSet.Ansi)]
        private static extern IntPtr dlopen(string path, int mode);

        private const int RtldLazy = 0x1;

        // ── CoreFoundation ─────────────────────────────────────────
        [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

        [DllImport(CoreFoundation)]
        private static extern bool CFBooleanGetValue(IntPtr boolean);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        private const uint KCFStringEncodingUtf8 = 0x08000100;

        // ── CoreGraphics: 로그인 세션 정보(화면 잠금 여부) ──────────
        [DllImport(CoreGraphics)]
        private static extern IntPtr CGSessionCopyCurrentDictionary();

        // ── IOKit: 입력 모니터링(Input Monitoring) 권한 ─────────────
        // macOS 10.15(Catalina)부터 전역 키보드 후킹에는 이 권한이 필요합니다.
        // 권한이 없으면 libuiohook의 이벤트 탭이 '조용히' 아무 이벤트도 주지 않습니다.
        [DllImport(IOKit)]
        private static extern int IOHIDCheckAccess(uint requestType);

        [DllImport(IOKit)]
        private static extern bool IOHIDRequestAccess(uint requestType);

        private const uint KIOHIDRequestTypeListenEvent = 1;
        private const int KIOHIDAccessTypeGranted = 0;

        private static bool _appKitLoaded;

        private static void EnsureAppKit()
        {
            if (_appKitLoaded) return;
            dlopen(AppKitPath, RtldLazy);
            _appKitLoaded = true;
        }

        // ────────────────────────────────────────────────────────────
        // 최전면 앱의 프로세스 ID를 돌려줍니다.
        //
        // Windows의 GetForegroundWindow()가 '창' 단위라면, macOS는 '앱' 단위입니다.
        // 같은 앱 안에서 창만 바꾸는 것은 전환으로 세지 않는데,
        // 이는 오히려 '컨텍스트 스위치'의 원래 의도(다른 작업으로 주의가 옮겨감)에
        // 더 가까운 측정이라 그대로 둡니다.
        // ────────────────────────────────────────────────────────────
        public static int GetFrontmostApplicationPid()
        {
            EnsureAppKit();

            IntPtr workspaceClass = ObjcGetClass("NSWorkspace");
            if (workspaceClass == IntPtr.Zero) return -1;

            IntPtr shared = MsgSendPtr(workspaceClass, SelRegisterName("sharedWorkspace"));
            if (shared == IntPtr.Zero) return -1;

            IntPtr app = MsgSendPtr(shared, SelRegisterName("frontmostApplication"));
            if (app == IntPtr.Zero) return -1;

            return MsgSendInt(app, SelRegisterName("processIdentifier"));
        }

        // 화면이 잠겨 있는지 확인합니다.
        // CGSessionCopyCurrentDictionary()가 준 딕셔너리에서
        // "CGSSessionScreenIsLocked" 키를 꺼내 봅니다. (없으면 잠기지 않은 상태)
        public static bool IsScreenLocked()
        {
            IntPtr session = CGSessionCopyCurrentDictionary();
            if (session == IntPtr.Zero) return false;

            IntPtr key = IntPtr.Zero;
            try
            {
                key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", KCFStringEncodingUtf8);
                if (key == IntPtr.Zero) return false;

                IntPtr value = CFDictionaryGetValue(session, key);
                // CFDictionaryGetValue는 소유권을 넘기지 않으므로 value는 해제하지 않습니다.
                return value != IntPtr.Zero && CFBooleanGetValue(value);
            }
            finally
            {
                if (key != IntPtr.Zero) CFRelease(key);
                CFRelease(session); // Copy로 받았으므로 해제 책임이 우리에게 있습니다.
            }
        }

        // 입력 모니터링 권한이 이미 허용되어 있는지 확인합니다.
        public static bool HasInputMonitoringAccess()
            => IOHIDCheckAccess(KIOHIDRequestTypeListenEvent) == KIOHIDAccessTypeGranted;

        // 시스템 권한 요청 팝업을 띄웁니다.
        // 사용자가 한 번 거부한 뒤에는 팝업이 다시 뜨지 않으므로,
        // 호출부에서 시스템 설정 화면을 함께 열어 주는 편이 좋습니다.
        public static bool RequestInputMonitoringAccess()
            => IOHIDRequestAccess(KIOHIDRequestTypeListenEvent);
    }
}
