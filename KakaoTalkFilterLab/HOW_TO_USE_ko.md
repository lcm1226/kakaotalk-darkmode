# KakaoTalk Filter Lab 사용설명서

## 목적
KakaoTalk Filter Lab은 Windows용 카카오톡 PC 창 위에 클릭 통과 overlay를 붙여 다크모드에 가까운 화면을 실험하는 프로토타입입니다.

현재 주요 목표는 `Smart` 모드에서 텍스트는 선명하게 반전하고, 채팅/친구 목록의 프로필 이미지는 원본에 가깝게 보존하는 것입니다.

## 바로 실행하기
1. `KakaoTalkFilterLab-win-x64.zip` 파일을 원하는 폴더에 압축 해제합니다.
2. 카카오톡 PC를 먼저 실행합니다.
3. 압축 해제한 폴더 안의 `KakaoTalkFilterLab.exe`를 실행합니다.
4. 프로그램에서 `Status`가 `Main window detected`, `Capture`가 `WGC active` 또는 `WGC frame ready`에 가까운 상태로 보이면 연결된 상태입니다.

## 필요한 환경
- Windows 10 2004 이상 또는 Windows 11
- x64 PC
- 카카오톡 PC Windows 버전
- self-contained 배포본은 별도 .NET 설치 없이 실행되도록 빌드되어 있습니다.

## 주요 기능
- `Enabled`: overlay 켜기/끄기
- `Privacy mode`: 카카오톡 좌측 사이드바와 우측 상단 창 버튼 영역을 제외한 대부분의 화면을 가립니다.
- `Ctrl + H`: Privacy mode 단축키 토글
- `Mode: Dim`: 단순 어둡게 덮기, 가장 가볍습니다.
- `Mode: Invert`: 전체 화면 반전 기반 필터입니다.
- `Mode: Smart`: 텍스트 가독성을 유지하면서 프로필 이미지는 반전 제외 영역으로 보존합니다.
- `Strength / Brightness / Contrast / Gamma`: Invert/Smart 필터 강도를 조절합니다.
- `Export Frames`: 현재 source/invert/smart 캡처를 `captures` 폴더로 내보냅니다.

## 종료 방식
- 창의 `X` 버튼을 누르면 앱이 완전히 종료되지 않고 시스템 트레이로 최소화됩니다.
- 트레이 아이콘을 더블 클릭하면 다시 열립니다.
- 트레이 메뉴의 `Exit`을 눌러야 완전히 종료됩니다.

## 현재 권장 설정
- Smart 기본값: Strength 100%, Brightness +50, Contrast 120%, Gamma 90%
- 텍스트 선명도 우선: Invert
- 프로필 이미지 보존 우선: Smart
- CPU 사용량 최우선: Dim

## 알려진 제한
- 이 앱은 카카오톡 공식 다크모드가 아니라 화면 캡처와 overlay 기반 프로토타입입니다.
- Smart 모드는 프로필 이미지 반전 제외를 위해 화면 분석을 수행하므로 Dim보다 무겁습니다.
- 카카오톡 UI 배율, 창 폭, 업데이트 상태에 따라 프로필 감지 품질이 달라질 수 있습니다.
- 현재 Smart는 프로필 반전 제외 영역을 실제 프로필보다 약 1px 안쪽으로 줄여 외곽 흰 프린지를 배경 반전색으로 덮는 방향입니다.

## 개발자용 빌드
소스에서 직접 빌드하려면 repo 루트에서 다음 명령을 실행합니다.

```powershell
dotnet build .\KakaoTalkFilterLab\KakaoTalkFilterLab.csproj
```

실행 파일은 다음 위치에 생성됩니다.

```text
KakaoTalkFilterLab\bin\Debug\net8.0-windows10.0.19041.0\KakaoTalkFilterLab.exe
```

배포용 ZIP은 다음 위치에 생성됩니다.

```text
KakaoTalkFilterLab\publish\KakaoTalkFilterLab-win-x64.zip
```