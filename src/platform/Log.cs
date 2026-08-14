using System;
using System.Diagnostics;

namespace Keymon.Platform
{
    // 네이티브 인터롭은 실패해도 앱이 죽으면 안 되므로,
    // 예외를 삼키는 자리마다 남길 최소한의 로그 창구를 둡니다.
    // 콘솔(터미널 실행 시)과 디버거 출력 양쪽에 찍습니다.
    public static class Log
    {
        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        private static void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Debug.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
