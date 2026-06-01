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
}
