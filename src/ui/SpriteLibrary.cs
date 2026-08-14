using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Keymon.Platform;

namespace Keymon
{
    // 픽셀 고양이 스프라이트를 한 번만 읽어서 캐싱해 두는 저장소입니다.
    //
    // 원본 keymon은 실행 파일 위치에서 "..\..\..\Assets\..." 처럼
    // 역슬래시가 박힌 상대 경로로 PNG를 읽었습니다. 이 경로는
    //   - macOS/Linux에서 경로 구분자가 달라 깨지고,
    //   - 배포본(.app 번들, publish 폴더)에서는 상위 3단계가 존재하지 않아 또 깨집니다.
    //
    // 그래서 PNG를 어셈블리 리소스로 내장하고 avares:// URI로 읽도록 바꿨습니다.
    // 파일 시스템을 전혀 건드리지 않으므로 어느 OS, 어느 배포 형태에서도 동일하게 동작합니다.
    public static class SpriteLibrary
    {
        private const int AnimationGroupCount = 5;   // 집중 상태 5단계
        private const int OverlayFrameCount = 12;    // 오버레이 창 스프라이트 프레임 수
        private const int TrayFrameCount = 8;        // 트레이 아이콘 프레임 수
        private const int TrayIconSize = 32;         // 트레이용으로 축소할 정사각 크기(px)

        private static Dictionary<int, List<Bitmap>>? _overlayFrames;
        private static Dictionary<int, List<Bitmap>>? _trayFrames;

        // 오버레이 창에 띄우는 큰 스프라이트 (Assets/Anim{1..5}/{1..12}.png)
        public static Dictionary<int, List<Bitmap>> OverlayFrames =>
            _overlayFrames ??= Load("Anim", OverlayFrameCount, null);

        // 트레이/메뉴바 아이콘용 작은 스프라이트 (Assets/TrayAsset{1..5}/{1..8}.png)
        public static Dictionary<int, List<Bitmap>> TrayFrames =>
            _trayFrames ??= Load("TrayAsset", TrayFrameCount, TrayIconSize);

        private static Dictionary<int, List<Bitmap>> Load(string folderPrefix, int frameCount, int? scaleTo)
        {
            var groups = new Dictionary<int, List<Bitmap>>();

            for (int animNum = 1; animNum <= AnimationGroupCount; animNum++)
            {
                var frames = new List<Bitmap>();

                for (int i = 1; i <= frameCount; i++)
                {
                    var uri = new Uri($"avares://Keymon/Assets/{folderPrefix}{animNum}/{i}.png");
                    try
                    {
                        using var stream = AssetLoader.Open(uri);
                        var bitmap = new Bitmap(stream);

                        if (scaleTo is int size)
                        {
                            // 픽셀 아트라서 보간 없이(NearestNeighbor) 줄여야 뭉개지지 않습니다.
                            var scaled = bitmap.CreateScaledBitmap(
                                new PixelSize(size, size), BitmapInterpolationMode.None);
                            bitmap.Dispose();
                            bitmap = scaled;
                        }

                        frames.Add(bitmap);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"스프라이트 로드 실패 {uri}: {ex.Message}");
                    }
                }

                if (frames.Count > 0) groups[animNum] = frames;
            }

            if (groups.Count == 0)
                Log.Warn($"'{folderPrefix}' 스프라이트를 하나도 찾지 못했습니다. Assets 폴더가 빌드에 포함됐는지 확인하세요.");

            return groups;
        }

        // 집중 상태(0~4)를 애니메이션 그룹 번호(1~5)로 바꿉니다.
        public static int GroupForState(int focusState) => focusState switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 5,
            _ => 1
        };
    }
}
