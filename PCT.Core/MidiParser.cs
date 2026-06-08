using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PCT.Core;

/// <summary>
/// MIDI(.mid) 파일을 MeasureBeat 목록으로 변환한다.
/// MIDI에는 타브(줄/프렛) 정보가 없으므로 음높이만 추출해 박자 격자에 양자화한다.
/// 이후 TabConverter가 운지를 재계산한다(원본 보존 불가).
/// </summary>
public class MidiParser
{
    /// <summary>Parse() 호출 후 템포 (4분음표/분). 기본값 120.</summary>
    public double Bpm { get; private set; } = 120;

    /// <summary>Parse() 호출 후 1박의 크기 (4분음표 단위). 4/4 → 1.0, 6/8 → 0.5.</summary>
    public double BeatSizeInQN { get; private set; } = 1.0;

    /// <summary>Parse() 호출 후 박자표 분자. 기본값 4.</summary>
    public int BeatsPerMeasure { get; private set; } = 4;

    /// <summary>Parse() 호출 후 박자표 분모. 기본값 4.</summary>
    public int BeatType { get; private set; } = 4;

    /// <summary>양자화 격자 (4분음표 단위). 0.25 = 16분음표. 핑거스타일은 0.125 권장.</summary>
    public double Grid { get; set; } = 0.25;

    /// <summary>
    /// 화음으로 묶을 동시 시작 허용 오차 (4분음표 단위). 기본 0.06.
    /// raw 시작이 이 값 이내인 음표만 화음으로 묶어, 빠른 연속음이 가짜 화음이 되는 것을 막는다.
    /// (어쿠스틱 기타 스트럼의 ~30~50ms 퍼짐은 흡수, 16분음표 간격(0.25)은 분리)
    /// </summary>
    public double ChordTolerance { get; set; } = 0.06;

    public List<MeasureBeat> Parse(Stream stream)
    {
        var midiFile = MidiFile.Read(stream);

        short tpqn = (midiFile.TimeDivision as TicksPerQuarterNoteTimeDivision)?.TicksPerQuarterNote ?? 480;
        if (tpqn <= 0) tpqn = 480;

        var tempoMap = midiFile.GetTempoMap();

        // 템포 (첫 변화값, 없으면 기본 120)
        double mpqn = 500000.0;
        var firstTempo = tempoMap.GetTempoChanges().FirstOrDefault();
        if (firstTempo != null) mpqn = firstTempo.Value.MicrosecondsPerQuarterNote;
        if (mpqn <= 0) mpqn = 500000.0;
        Bpm = 60_000_000.0 / mpqn;

        // 박자표 (첫 변화값, 없으면 4/4)
        var firstTs = tempoMap.GetTimeSignatureChanges().FirstOrDefault();
        if (firstTs != null)
        {
            BeatsPerMeasure = firstTs.Value.Numerator;
            BeatType        = firstTs.Value.Denominator;
        }
        else { BeatsPerMeasure = 4; BeatType = 4; }
        if (BeatsPerMeasure <= 0) BeatsPerMeasure = 4;
        if (BeatType <= 0)        BeatType = 4;
        BeatSizeInQN = 4.0 / BeatType;

        double measureLengthQN = BeatsPerMeasure * BeatSizeInQN;
        double grid     = Grid > 0 ? Grid : 0.25;
        double chordTol = ChordTolerance >= 0 ? ChordTolerance : 0.0;

        // raw 음표 수집 (드럼 제외), 시작시각 순 정렬
        var raw = midiFile.GetNotes()
            .Where(n => (byte)n.Channel != 9)                 // 드럼 채널(10번, 0-based 9) 제외
            .Select(n => (
                start: (double)n.Time   / tpqn,
                dur:   (double)n.Length / tpqn,
                midi:  (int)(byte)n.NoteNumber))
            .OrderBy(x => x.start)
            .ToList();

        // 동시 시작(±chordTol)인 음표만 화음으로 클러스터링.
        // 클러스터의 '첫 음표' 기준으로 비교해 느린 드리프트가 무한 연쇄되지 않게 한다.
        var clusters = new List<(double start, List<(int midi, double dur)> notes)>();
        foreach (var r in raw)
        {
            if (clusters.Count > 0 && r.start - clusters[^1].start <= chordTol + 1e-9)
                clusters[^1].notes.Add((r.midi, r.dur));
            else
                clusters.Add((r.start, new List<(int midi, double dur)> { (r.midi, r.dur) }));
        }

        // 각 클러스터를 격자에 양자화해 (마디, 박)에 배치
        var beats = new List<MeasureBeat>();
        MeasureBeat currentBeat = null;
        foreach (var cluster in clusters)
        {
            double qStart        = Math.Round(cluster.start / grid) * grid;
            int    measureIndex  = measureLengthQN > 0 ? (int)(qStart / measureLengthQN + 1e-9) : 0;
            int    measureNumber = measureIndex + 1;
            double withinQN      = qStart - measureIndex * measureLengthQN;
            int    beatNumber    = (int)(withinQN / BeatSizeInQN + 1e-9) + 1;

            // 같은 (마디,박)이면 같은 MeasureBeat에 별개 NoteGroup으로 순차 배치된다.
            if (currentBeat == null ||
                currentBeat.MeasureNumber != measureNumber ||
                currentBeat.BeatNumber    != beatNumber)
            {
                currentBeat = new MeasureBeat { MeasureNumber = measureNumber, BeatNumber = beatNumber };
                beats.Add(currentBeat);
            }

            double durQN = Math.Max(grid, Math.Round(cluster.notes.Max(x => x.dur) / grid) * grid);  // 화음 길이는 최장음
            var ng = new NoteGroup { DurationInQN = durQN };
            foreach (var (midi, _) in cluster.notes.OrderBy(x => x.midi))
            {
                var (step, alter, octave) = MidiToPitch(midi);
                ng.Notes.Add(new Note { Step = step, Alter = alter, Octave = octave });
            }
            currentBeat.Notes.Add(ng);
        }

        return beats;
    }

    // MIDI 음 번호 → (음이름, 반음, 옥타브). 올림표(#) 기준.
    private static (string step, int alter, int octave) MidiToPitch(int midi)
    {
        int octave = midi / 12 - 1;
        int semi   = ((midi % 12) + 12) % 12;
        return semi switch
        {
            0  => ("C", 0, octave),
            1  => ("C", 1, octave),
            2  => ("D", 0, octave),
            3  => ("D", 1, octave),
            4  => ("E", 0, octave),
            5  => ("F", 0, octave),
            6  => ("F", 1, octave),
            7  => ("G", 0, octave),
            8  => ("G", 1, octave),
            9  => ("A", 0, octave),
            10 => ("A", 1, octave),
            11 => ("B", 0, octave),
            _  => ("C", 0, octave),
        };
    }
}
