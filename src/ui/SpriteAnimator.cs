using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace Keymon
{
    // 스프라이트 프레임을 넘기는 상태 기계입니다.
    //
    // 원본 keymon에서는 이 로직이 TrayIconManager와 MainWindow에
    // 글자 하나까지 똑같이 두 벌 복사돼 있었습니다.
    // 이식하면서 한 곳으로 모았습니다. 동작(프레임 순서, 24틱 정지, 전환 시점)은 동일합니다.
    //
    // 재생 규칙:
    //   - 0번 프레임은 '쉬는 자세'라서 24틱 동안 붙잡아 둡니다.
    //   - 한 사이클을 다 돌면, 목표 상태가 바뀌었을 때만 다음 그룹으로 갈아탑니다.
    //     (동작 중간에 갑자기 다른 애니메이션으로 튀지 않게 하기 위함)
    //   - 대기 상태에서는 1번 그룹의 0번 프레임에서 완전히 멈춥니다.
    public sealed class SpriteAnimator
    {
        private const int IdleHoldTicks = 24;

        private readonly Dictionary<int, List<Bitmap>> _groups;

        private int _currentGroup = 1;
        private int _targetGroup = 1;
        private int _frameIndex = 0;
        private int _holdCounter = 0;

        public SpriteAnimator(Dictionary<int, List<Bitmap>> groups)
        {
            _groups = groups;
        }

        public bool IsStandby { get; set; }

        // 집중 상태(0~4)가 바뀌면 호출합니다. 즉시 갈아타지 않고
        // 현재 사이클이 끝나는 시점에 자연스럽게 전환됩니다.
        public void SetState(int focusState) => _targetGroup = SpriteLibrary.GroupForState(focusState);

        // 매 틱마다 호출하면 지금 보여줘야 할 프레임을 돌려줍니다.
        // 보여줄 것이 없으면 null (호출자는 화면을 그대로 둡니다).
        public Bitmap? NextFrame()
        {
            if (IsStandby)
            {
                _currentGroup = 1;
                _targetGroup = 1;
                _frameIndex = 0;
                _holdCounter = 0;

                return _groups.TryGetValue(1, out var idleSet) && idleSet.Count > 0 ? idleSet[0] : null;
            }

            if (!_groups.TryGetValue(_currentGroup, out var set) || set.Count == 0) return null;

            Bitmap? frame = _frameIndex < set.Count ? set[_frameIndex] : null;

            if (_frameIndex == 0)
            {
                _holdCounter++;
                if (_holdCounter < IdleHoldTicks) return frame; // 아직 쉬는 자세 유지
                _holdCounter = 0;
            }

            _frameIndex++;

            if (_frameIndex >= set.Count)
            {
                if (_currentGroup != _targetGroup)
                {
                    _currentGroup = _targetGroup;
                    _frameIndex = 0;
                    _holdCounter = IdleHoldTicks; // 전환 직후에는 기다리지 않고 바로 재생
                }
                else
                {
                    _frameIndex = 1; // 0번(쉬는 자세)은 건너뛰고 반복
                }
            }

            return frame;
        }
    }
}
