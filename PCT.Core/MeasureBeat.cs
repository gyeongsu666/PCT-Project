namespace PCT.Core;

/// <summary>
/// 하나의 박(beat) 슬롯. 마디 번호·박 번호 + 그 박 안에 속하는 음표 이벤트 목록을 갖는다.
/// 예) 4/4박자 1마디 2박: MeasureNumber=1, BeatNumber=2, Notes=[8분음표A, 8분음표B]
/// </summary>
public class MeasureBeat
{
    public int MeasureNumber { get; set; } = 1;   // 1-based
    public int BeatNumber    { get; set; } = 1;   // 1-based 정수 박 번호
    public List<NoteGroup> Notes { get; set; } = new();

    /// <summary>이 마디가 시작 도돌이표(‖:)를 가지는가.</summary>
    public bool RepeatStart { get; set; } = false;
    /// <summary>이 마디가 끝 도돌이표(:‖)를 가지는가.</summary>
    public bool RepeatEnd   { get; set; } = false;

    /// <summary>이 마디에 박자표가 바뀌어 표시해야 하면 분자(>0), 아니면 0.</summary>
    public int TimeSigNum { get; set; } = 0;
    /// <summary>박자표 분모 (TimeSigNum>0일 때 의미).</summary>
    public int TimeSigDen { get; set; } = 0;
}
