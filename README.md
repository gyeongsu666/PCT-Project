# PCT-Web — Piano/Chord Tab Converter (웹 GUI)

## 프로젝트 구조

```
PCT-Web/
├── PCT.Core/          ← 핵심 변환 로직 (C# 라이브러리)
│   ├── Note.cs
│   ├── NoteGroup.cs
│   ├── GuitarTuning.cs
│   ├── TabPosition.cs
│   ├── TabPositionGroup.cs
│   ├── MusicXmlParser.cs
│   ├── TabConverter.cs
│   └── TabImageRenderer.cs
├── PCT.Api/           ← ASP.NET Core Web API + 정적 프론트엔드
│   ├── Program.cs     ← API 엔드포인트 정의
│   ├── appsettings.json
│   └── wwwroot/
│       └── index.html ← 웹 GUI (단일 HTML 파일)
└── PCT-Web.sln
```

## 실행 방법

### 요구사항
- .NET 8.0 SDK (https://dotnet.microsoft.com/download)

### 실행

```bash
cd PCT-Web/PCT.Api
dotnet run
```

브라우저에서 http://localhost:5000 접속

## API 엔드포인트

### POST /api/convert
MusicXML 파일을 타브악보 JSON으로 변환

**요청 (multipart/form-data)**
- `file`: MusicXML 파일
- `maxFingerSpan`: 최대 손가락 범위 (기본 5)
- `highFretThreshold`: 하이프렛 기준 (기본 9)
- `handMoveCost`: 손 이동 가중치 (기본 2.5)

**응답 (JSON)**
```json
{
  "title": "악보이름",
  "beatCount": 84,
  "noteCount": 217,
  "chordCount": 32,
  "droppedCount": 3,
  "settings": { ... },
  "groups": [
    {
      "isRest": false,
      "droppedCount": 0,
      "positions": [
        { "stringIndex": 0, "fret": 3 }
      ]
    }
  ]
}
```

### POST /api/render-png
MusicXML 파일을 PNG 이미지로 변환하여 반환

**요청**: /api/convert와 동일
**응답**: PNG 바이트 (image/png)

## 원본 대비 변경사항

1. **PCT.Core**: namespace를 `PCT` → `PCT.Core`로 변경
2. **MusicXmlParser**: `Stream`으로도 파싱 가능하도록 오버로드 추가
3. **TabConverter**: 설정값을 `TabConverterSettings`로 외부 주입 가능
4. **TabImageRenderer**: `RenderToPng()` 외에 `RenderToPngBytes()` 추가 (HTTP 응답용)
5. **SkiaSharp**: Windows 전용 → Linux 지원 패키지로 교체
