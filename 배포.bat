@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================================
echo  PC 관리 도구 - GitHub 배포 (빌드 + 커밋 + Release 업로드)
echo ============================================================
for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" PCManagerCS.csproj') do set CUR=%%v
echo  현재 버전: %CUR%
echo.
set /p VER="새 버전을 입력하세요 (예: 2.0.1): "
if "%VER%"=="" (
    echo 취소했습니다.
    pause
    exit /b 1
)
set /p NOTES="변경 내용 (Enter = 커밋 기록으로 자동 작성): "
if "%NOTES%"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 %VER%
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 %VER% -Notes "%NOTES%"
)
echo.
pause
