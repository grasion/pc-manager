<#
  PC 관리 도구 새 버전 배포 (이 PC에서 빌드 → GitHub 커밋/태그/푸시 → Release에 exe 업로드)
  설치된 프로그램은 시작할 때 최신 Release를 확인하고, 헤더의 [새 버전] 버튼으로 자동 업데이트한다.

  사용법:  .\release.ps1 2.1.0
           .\release.ps1 2.1.0 -Notes "프린터 대기열 초기화 추가"
  필요:    dotnet SDK, git, gh (GitHub CLI) 로그인 (gh auth login)
  첫 실행: GitHub 저장소가 없으면 공개 저장소로 만든다 (자동 업데이트는 공개 저장소에서만 동작).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ''
)

# 네이티브 명령(git 등)의 stderr 경고로 중단되지 않도록 Continue — 실패 여부는 종료 코드로 판단
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$Repo   = 'grasion/pc-manager'          # Updater.cs 의 Repo 와 같아야 함
$Csproj = 'PCManagerCS.csproj'
$Exe    = 'bin\Release\net48\PCManagerCS.exe'
$Tag    = "v$Version"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "버전은 1.2.3 형식이어야 합니다: $Version" }

function Step($msg) { Write-Host "`n▶ $msg" -ForegroundColor Cyan }
function Run([scriptblock]$cmd) { & $cmd; if ($LASTEXITCODE -ne 0) { throw "실패 (exit $LASTEXITCODE): $cmd" } }

# 0) 사전 확인
Step "사전 확인"
foreach ($tool in 'dotnet', 'git', 'gh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool 이(가) 설치되어 있지 않습니다." }
}
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub 로그인이 필요합니다: gh auth login" }
if (-not (Test-Path .git)) {
    Run { git init -q -b main }
}
if (git tag --list $Tag) { throw "$Tag 태그가 이미 있습니다. 더 높은 버전을 지정하세요." }
$prevTag = (git describe --tags --abbrev=0 2>$null)
if ($prevTag -and ([version]$Version -le [version]($prevTag.TrimStart('v')))) {
    throw "새 버전($Version)은 이전 버전($prevTag)보다 높아야 합니다."
}

# 1) 버전 반영
Step "버전 $Version 반영"
$text = [IO.File]::ReadAllText($Csproj)
$new  = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
if ($new -ne $text) { [IO.File]::WriteAllText($Csproj, $new, (New-Object Text.UTF8Encoding $false)) }

# 2) 빌드 (실행 중이면 exe가 잠겨 있으므로 종료)
Step "빌드"
Get-Process PCManagerCS -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Resolve-Path $Exe -ErrorAction SilentlyContinue).Path } | Stop-Process -Force
Run { dotnet build $Csproj -c Release -p:Version=$Version -nologo -v q }
New-Item -ItemType Directory -Force publish | Out-Null
Copy-Item $Exe publish\PCManagerCS.exe -Force
$size = [math]::Round((Get-Item publish\PCManagerCS.exe).Length / 1KB)
Write-Host "  publish\PCManagerCS.exe ($size KB)"

# 3) 커밋 + 태그
Step "커밋 / 태그"
git add -A 2>$null
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Run { git commit -q -m "Release $Tag" } }
Run { git tag $Tag }

# 4) 저장소 확인(없으면 생성) + 푸시
Step "GitHub 푸시"
gh repo view $Repo *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  저장소 $Repo 가 없어 새로 만듭니다 (공개)"
    Run { gh repo create $Repo --public --description "Everything 방식 초고속 파일 검색 + Windows PC 관리 도구" }
}
if (-not (git remote 2>$null | Select-String -SimpleMatch 'origin')) {
    Run { git remote add origin "https://github.com/$Repo.git" }
}
Run { git push -q -u origin HEAD }
Run { git push -q origin $Tag }

# 5) 릴리스 노트 (지정하지 않으면 이전 태그 이후 커밋 메시지)
if (-not $Notes) {
    $range = if ($prevTag) { "$prevTag..$Tag" } else { $Tag }
    $Notes = (git log $range --pretty=format:'- %s' --no-merges 2>$null | Where-Object { $_ -notmatch '^- Release v' }) -join "`n"
    if (-not $Notes) { $Notes = "- PC 관리 도구 $Tag" }
}
$Notes += "`n`n### 설치 / 업데이트`n- ``PCManagerCS.exe`` 하나만 내려받아 실행하면 됩니다 (Windows 10/11, 설치 불필요).`n- 이미 사용 중이면 프로그램 상단의 **새 버전** 버튼으로 자동 업데이트됩니다."
$notesFile = Join-Path $env:TEMP "pcm_release_notes.md"
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding $false))

# 6) GitHub Release 업로드
Step "GitHub Release 게시"
Run { gh release create $Tag publish\PCManagerCS.exe --repo $Repo --title "PC 관리 도구 $Tag" --notes-file $notesFile }
Remove-Item $notesFile -ErrorAction SilentlyContinue

Write-Host "`n✅ $Tag 배포 완료: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
