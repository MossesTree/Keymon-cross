using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;

namespace Keymon
{
    // WPF는 App.xaml이 자동으로 진입점을 만들어 주지만,
    // Avalonia는 일반 콘솔 앱처럼 Main을 직접 씁니다.
    // 덕분에 Windows/macOS/Linux에서 완전히 같은 방식으로 시작됩니다.
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Avalonia 디자이너와 테스트가 찾아 쓰는 규약 이름이라 이름을 바꾸면 안 됩니다.
        public static AppBuilder BuildAvaloniaApp()
        {
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()   // Windows→Win32, macOS→AppKit 백엔드를 자동 선택
                .WithInterFont()
                .LogToTrace();

            // Avalonia.Native는 시작 시 NSApplication의 activationPolicy를 직접
            // 설정하므로, Info.plist의 LSUIElement 값은 macOS Dock 표시 여부에
            // 아무 영향이 없습니다. build-mac.sh가 이미 써둔 LSUIElement 값을
            // 그대로 읽어 ShowInDock에 반영해, 빌드 플래그 하나로 계속 제어되게 합니다.
            if (OperatingSystem.IsMacOS())
                builder = builder.With(new MacOSPlatformOptions { ShowInDock = !IsMenuBarOnly() });

            return builder;
        }

        private static bool IsMenuBarOnly()
        {
            try
            {
                string plistPath = Path.Combine(AppContext.BaseDirectory, "..", "Info.plist");
                string text = File.ReadAllText(plistPath);
                var match = Regex.Match(text, @"<key>LSUIElement</key>\s*<(true|false)\s*/>");
                return match.Success && match.Groups[1].Value == "true";
            }
            catch
            {
                return false;
            }
        }
    }
}
