using System;
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
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UsePlatformDetect()   // Windows→Win32, macOS→AppKit 백엔드를 자동 선택
            .WithInterFont()
            .LogToTrace();
    }
}
