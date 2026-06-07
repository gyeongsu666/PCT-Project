namespace PCT.Core;

/// <summary>
/// 타브 변환 결과의 하나의 박 슬롯.
/// MeasureBeat 와 1:1 대응하며, 각 음표 이벤트는 TabPositionGroup 으로 변환된다.
/// </summary>
public class TabMeasureBeat
{
    public int MeasureNumber { get; set; } = 1;
    public int BeatNumber    { get; set; } = 1;
    public List<TabPositionGroup> Notes { get; set; } = new();

    /// <summary>이 마디가 시작 도돌이표(‖:)를 가지는가.</summary>
    public bool RepeatStart { get; set; } = false;
    /// <summary>이 마디가 끝 도돌이표(:‖)를 가지는가.</summary>
    public bool RepeatEnd   { get; set; } = false;
}
