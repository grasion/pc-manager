@echo off
chcp 65001 >nul
echo ============================================================
echo  PC 관리 도구 빌드 (.NET Framework 4.8 / 단일 exe)
echo ============================================================

dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [오류] .NET SDK가 없습니다. https://dotnet.microsoft.com/download 에서 설치하세요.
    pause
    exit /b 1
)

dotnet build -c Release
if %errorlevel% neq 0 (
    echo [오류] 빌드 실패
    pause
    exit /b 1
)

if not exist publish mkdir publish
copy /Y "bin\Release\net48\PCManagerCS.exe" "publish\PCManagerCS.exe" >nul

echo.
echo [완료] publish\PCManagerCS.exe
echo  - exe 파일 하나만 복사하면 다른 PC에서도 실행됩니다.
echo  - Windows 10/11 기본 탑재 .NET Framework 4.8 사용 (별도 설치 불필요)
echo.
pause
