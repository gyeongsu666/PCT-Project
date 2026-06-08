using System.Xml;

namespace PCT.Core;

public class MusicXmlParser
{
    /// <summary>Parse() 호출 후 악보에서 읽은 템포 (4분음표/분). 기본값 120.</summary>
    public double Bpm { get; private set; } = 120;

    /// <summary>Parse() 호출 후 1박의 크기 (4분음표 단위). 4/4 → 1.0, 6/8 → 0.5.</summary>
    public double BeatSizeInQN { get; private set; } = 1.0;

    /// <summary>Parse() 호출 후 박자표 분자 (4/4 → 4, 6/8 → 6). 기본값 4.</summary>
    public int BeatsPerMeasure { get; private set; } = 4;

    /// <summary>Parse() 호출 후 박자표 분모 (4/4 → 4, 6/8 → 8). 기본값 4.</summary>
    public int BeatType { get; private set; } = 4;

    public List<MeasureBeat> Parse(Stream stream)
    {
        Bpm = 120; BeatSizeInQN = 1.0;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    public List<MeasureBeat> ParseFile(string filePath)
    {
        Bpm = 120; BeatSizeInQN = 1.0;
        using var reader = XmlReader.Create(filePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    private List<MeasureBeat> ParseReader(XmlReader reader)
    {
        var beats        = new List<MeasureBeat>();
        MeasureBeat currentBeat      = null;
        NoteGroup   currentNoteGroup = null;   // 화음 누적용

        int    measureNumber = 0;
        int    divisions     = 1;    // 4분음표 1박 = divisions 단위
        int    beatType      = 4;    // 박자표 분모 (4/4 → 4, 6/8 → 8)
        double beatSizeInQN  = 1.0;  // 1박의 크기 (4분음표 단위)
        double qnAccumulator = 0;    // 마디 내 4분음표 누산기
        double bpm           = 120;  // <sound tempo="..."> 에서 읽음

        int    beatsPerMeasure = 4;    // 박자표 분자 (4/4 → 4, 6/8 → 6)
        double measureLengthQN = 4.0;  // 한 마디 길이 (4분음표 단위) = 분자 × beatSizeInQN

        var repeatStartMeasures = new HashSet<int>();  // ‖: 가 있는 마디 번호
        var repeatEndMeasures   = new HashSet<int>();  // :‖ 가 있는 마디 번호
        var timeSigAtMeasure    = new Dictionary<int, (int num, int den)>();  // <time>이 나온 마디 → 박자표

        // 누산기가 마디 길이를 넘었으면 그만큼 다음 마디로 넘긴다 (지연 마디 분할).
        // 음표 배치 '직전'에 호출하므로, <backup>이 먼저 누산기를 되감으면 분할은 일어나지
        // 않는다 → 다성부(backup으로 voice 2를 같은 마디에 겹쳐 쓰는 경우)가 보존된다.
        void NormalizeMeasure()
        {
            while (measureLengthQN > 0 && qnAccumulator >= measureLengthQN - 1e-9)
            {
                measureNumber++;
                qnAccumulator   -= measureLengthQN;
                currentNoteGroup = null;
            }
            if (qnAccumulator < 0) qnAccumulator = 0;
        }

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                // ── 마디 경계 ──────────────────────────────────────────────────
                case "measure":
                    measureNumber++;
                    qnAccumulator    = 0;
                    currentNoteGroup = null;
                    break;

                // ── 도돌이표: <barline><repeat direction="forward|backward"/></barline> ──
                // forward(location=left) = 시작 도돌이(‖:), backward(location=right) = 끝 도돌이(:‖)
                case "barline":
                {
                    using var blReader = reader.ReadSubtree();
                    while (blReader.Read())
                    {
                        if (blReader.NodeType != XmlNodeType.Element || blReader.LocalName != "repeat") continue;
                        var dir = blReader.GetAttribute("direction");
                        if (dir == "forward")       repeatStartMeasures.Add(measureNumber);
                        else if (dir == "backward") repeatEndMeasures.Add(measureNumber);
                    }
                    break;
                }

                // ── divisions ─────────────────────────────────────────────────
                case "divisions":
                    if (int.TryParse(reader.ReadElementContentAsString(), out int d) && d > 0)
                        divisions = d;
                    break;

                // ── 템포: <sound tempo="62"/> ─────────────────────────────────
                // <direction><sound tempo="..."/></direction> 형태로 나타남
                case "sound":
                {
                    var tempoAttr = reader.GetAttribute("tempo");
                    if (tempoAttr != null &&
                        double.TryParse(tempoAttr,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double t) && t > 0)
                        bpm = t;
                    break;
                }

                // ── 박자표: <time><beats>N</beats><beat-type>D</beat-type></time>
                case "time":
                {
                    using var timeReader = reader.ReadSubtree();
                    int beatsNum = 0;
                    while (timeReader.Read())
                    {
                        if (timeReader.NodeType != XmlNodeType.Element) continue;
                        if (timeReader.LocalName == "beats" &&
                            int.TryParse(timeReader.ReadElementContentAsString(), out int bn) && bn > 0)
                        {
                            beatsNum = bn;
                        }
                        else if (timeReader.LocalName == "beat-type" &&
                            int.TryParse(timeReader.ReadElementContentAsString(), out int bt) && bt > 0)
                        {
                            beatType     = bt;
                            beatSizeInQN = 4.0 / beatType;  // 1박 = 4분음표 몇 개
                        }
                    }
                    if (beatsNum > 0) beatsPerMeasure = beatsNum;
                    measureLengthQN = beatsPerMeasure * beatSizeInQN;  // 한 마디 길이(4분음표 단위)
                    timeSigAtMeasure[measureNumber] = (beatsPerMeasure, beatType);  // 박자표 표시용
                    break;
                }

                // ── backup: 다성부 표기에서 이전 위치로 되감기 ──────────────
                // Voice 1 끝난 뒤 <backup>으로 마디 처음으로 돌아가고 Voice 2를 씀
                case "backup":
                {
                    using var bkReader = reader.ReadSubtree();
                    while (bkReader.Read())
                    {
                        if (bkReader.NodeType != XmlNodeType.Element) continue;
                        if (bkReader.LocalName == "duration" &&
                            int.TryParse(bkReader.ReadElementContentAsString(), out int bkDur) && bkDur > 0)
                        {
                            // 비정상적으로 큰 backup(OCR 잡음)은 마디 시작으로 되감기
                            double bkQN = (double)bkDur / divisions;
                            double cap  = measureLengthQN > 0 ? measureLengthQN : 16.0;
                            qnAccumulator = bkQN <= cap + 1e-9
                                ? Math.Max(0, qnAccumulator - bkQN)
                                : 0;
                            currentNoteGroup = null;  // 성부 전환 시 화음 누적 리셋
                        }
                    }
                    break;
                }

                // ── forward: 쉼표 없이 위치만 앞으로 이동 ────────────────────
                case "forward":
                {
                    using var fwdReader = reader.ReadSubtree();
                    while (fwdReader.Read())
                    {
                        if (fwdReader.NodeType != XmlNodeType.Element) continue;
                        if (fwdReader.LocalName == "duration" &&
                            int.TryParse(fwdReader.ReadElementContentAsString(), out int fwdDur) && fwdDur > 0)
                        {
                            // 비정상적으로 큰 forward(OCR 잡음)는 무시. 마디 넘김은 다음 음표 배치 때 처리.
                            double fwdQN = (double)fwdDur / divisions;
                            double cap   = measureLengthQN > 0 ? measureLengthQN : 16.0;
                            if (fwdQN <= cap + 1e-9)
                                qnAccumulator += fwdQN;
                        }
                    }
                    break;
                }

                // ── note ──────────────────────────────────────────────────────
                case "note":
                {
                    bool   isChord     = false;
                    bool   isRest      = false;
                    string step        = "C";
                    int    octave      = 4;
                    int    alter       = 0;
                    int    rawDuration = divisions;
                    int    voice       = 1;

                    using var noteReader = reader.ReadSubtree();
                    while (noteReader.Read())
                    {
                        if (noteReader.NodeType != XmlNodeType.Element) continue;
                        switch (noteReader.LocalName)
                        {
                            case "chord":
                                isChord = true;
                                break;
                            case "rest":
                                isRest = true;
                                break;
                            case "step":
                                step = noteReader.ReadElementContentAsString();
                                break;
                            case "octave":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int o))
                                    octave = o;
                                break;
                            case "alter":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int a))
                                    alter = a;
                                break;
                            case "duration":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int dur))
                                    rawDuration = dur;
                                break;
                            case "voice":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int v))
                                    voice = v;
                                break;
                        }
                    }

                    double durationInQN = rawDuration > 0
                        ? (double)rawDuration / divisions
                        : 1.0;

                    var note = new Note();
                    if (isRest) note.IsRest = true;
                    else        { note.Step = step; note.Octave = octave; note.Alter = alter; }

                    if (isChord && currentNoteGroup != null && !currentNoteGroup.IsRest)
                    {
                        // 화음 — 같은 NoteGroup에 추가, 박 누산기 건드리지 않음
                        currentNoteGroup.Notes.Add(note);
                    }
                    else
                    {
                        // 박자표 기반 지연 마디 분할: 새 음표를 놓기 전에 누산기가 마디를
                        // 넘겼으면 그때 마디를 넘긴다. <backup>이 먼저 되감았다면 분할되지 않아
                        // 다성부가 같은 마디에 유지된다.
                        NormalizeMeasure();

                        // 새 음표 이벤트: 정수 박 번호 계산
                        // 1e-9 epsilon: 부동소수점 오차로 박 경계를 살짝 넘는 경우 방지
                        int beatNumber = (int)(qnAccumulator / beatSizeInQN + 1e-9) + 1;

                        // 해당 (마디, 박)의 MeasureBeat를 찾거나 새로 만든다
                        if (currentBeat == null ||
                            currentBeat.MeasureNumber != measureNumber ||
                            currentBeat.BeatNumber    != beatNumber)
                        {
                            currentBeat = new MeasureBeat
                            {
                                MeasureNumber = measureNumber,
                                BeatNumber    = beatNumber,
                            };
                            beats.Add(currentBeat);
                        }

                        var ng = new NoteGroup();
                        ng.DurationInQN = durationInQN;
                        ng.Notes.Add(note);
                        currentBeat.Notes.Add(ng);
                        currentNoteGroup = ng;

                        // <backup>이 성부 전환을 처리하므로 모든 성부가 누산기를 진행
                        qnAccumulator += durationInQN;
                    }
                    break;
                }
            }
        }

        // 도돌이표·박자표를 마디 번호 기준으로 각 MeasureBeat에 표시
        foreach (var mb in beats)
        {
            if (repeatStartMeasures.Contains(mb.MeasureNumber)) mb.RepeatStart = true;
            if (repeatEndMeasures.Contains(mb.MeasureNumber))   mb.RepeatEnd   = true;
            if (timeSigAtMeasure.TryGetValue(mb.MeasureNumber, out var ts))
            {
                mb.TimeSigNum = ts.num;
                mb.TimeSigDen = ts.den;
            }
        }

        // 파싱 완료 후 인스턴스 프로퍼티에 저장
        Bpm             = bpm;
        BeatSizeInQN    = beatSizeInQN;
        BeatsPerMeasure = beatsPerMeasure;
        BeatType        = beatType;
        return beats;
    }
}
