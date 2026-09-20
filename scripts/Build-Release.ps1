[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string] $Version = '0.5.1',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $NativeRoot,
    [switch] $SkipTests,
    [switch] $SkipDependencyFetch,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw '发布脚本需在 Windows 10/11 x64 上运行。'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $repositoryRoot 'src\InterviewScribe.App\InterviewScribe.App.csproj'
$engineProjectFile = Join-Path $repositoryRoot 'src\InterviewScribe.EngineHost\InterviewScribe.EngineHost.csproj'
if ([string]::IsNullOrWhiteSpace($NativeRoot)) {
    $NativeRoot = Join-Path $repositoryRoot 'artifacts\native'
}
$NativeRoot = [System.IO.Path]::GetFullPath($NativeRoot)
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\MediaScribe'
$enginePublishRoot = Join-Path $repositoryRoot 'artifacts\publish\InterviewScribe.EngineHost'
$packageRoot = Join-Path $repositoryRoot 'artifacts\package\MediaScribe'
$releaseRoot = Join-Path $repositoryRoot 'artifacts\release'
$installerScript = Join-Path $repositoryRoot 'packaging\installer.iss'
$lockFile = Join-Path $repositoryRoot 'packaging\dependencies.lock.json'
$qwenToolSourceRoot = Join-Path $repositoryRoot 'tools\qwen'
$whisperToolSourceRoot = Join-Path $repositoryRoot 'tools\whisper'
$qwenManifestFile = Join-Path $repositoryRoot 'src\InterviewScribe.Infrastructure\QwenModelManifest.cs'
$whisperManifestFile = Join-Path $repositoryRoot 'src\InterviewScribe.Infrastructure\FasterWhisperModelManifest.cs'
$qwenSidecarFile = Join-Path $qwenToolSourceRoot 'qwen_sidecar.py'
$whisperSidecarFile = Join-Path $whisperToolSourceRoot 'whisper_sidecar.py'
$qwenToolRequiredFiles = @(
    'qwen_sidecar.py',
    'Install-QwenRuntime.ps1'
)
$whisperToolRequiredFiles = @(
    'whisper_sidecar.py',
    'Install-FasterWhisperRuntime.ps1'
)

function Assert-RequiredPayloadFiles {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [object[]] $RelativePaths,
        [Parameter(Mandatory)] [string] $Name
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($relativePathValue in $RelativePaths) {
        $relativePath = [string]$relativePathValue
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath)) {
            throw "$Name 的 requiredFiles 包含无效路径：$relativePath"
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $normalizedRoot $relativePath))
        if (-not $fullPath.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Name 的 requiredFiles 越过了依赖根目录：$relativePath"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "$Name 不完整，缺少必需文件：$relativePath"
        }
        if ((Get-Item -LiteralPath $fullPath).Length -le 0) {
            throw "$Name 的必需文件为空：$relativePath"
        }
    }
}

function Get-RequiredRegexCapture {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Pattern,
        [Parameter(Mandatory)] [string] $Name
    )

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Source,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success -or $match.Groups.Count -lt 2) {
        throw "无法从 $Name 读取锁定的模型版本。"
    }

    return $match.Groups[1].Value
}

$dotnet = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    $dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
}
if ($null -eq $dotnet) {
    throw '找不到 dotnet。请安装 .NET 10 SDK x64。'
}

$sdkVersionText = (& $dotnet.Source '--version').Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersionText -notmatch '^(\d+)\.') {
    throw "无法确定 .NET SDK 版本：$sdkVersionText"
}
if ([int]$Matches[1] -lt 10) {
    throw "需要 .NET 10 SDK，当前为 $sdkVersionText。"
}

if (-not $SkipDependencyFetch) {
    & (Join-Path $PSScriptRoot 'Fetch-NativeDependencies.ps1') -DestinationRoot $NativeRoot
}

if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
    throw "找不到依赖锁文件：$lockFile"
}
$dependencyLock = Get-Content -LiteralPath $lockFile -Raw -Encoding UTF8 | ConvertFrom-Json
$qwenManifestSource = Get-Content -LiteralPath $qwenManifestFile -Raw -Encoding UTF8
$whisperManifestSource = Get-Content -LiteralPath $whisperManifestFile -Raw -Encoding UTF8
$qwenSidecarSource = Get-Content -LiteralPath $qwenSidecarFile -Raw -Encoding UTF8
$lockedAsrRevision = [string]$dependencyLock.models.qwen3Asr17bModelScope.revision
$lockedAlignerRevision = [string]$dependencyLock.models.qwen3ForcedAligner06bModelScope.revision
$manifestAsrRevision = Get-RequiredRegexCapture `
    -Source $qwenManifestSource `
    -Pattern 'internal\s+const\s+string\s+AsrRevision\s*=\s*"([0-9a-f]{40})"' `
    -Name 'QwenModelManifest.cs'
$manifestAlignerRevision = Get-RequiredRegexCapture `
    -Source $qwenManifestSource `
    -Pattern 'internal\s+const\s+string\s+AlignerRevision\s*=\s*"([0-9a-f]{40})"' `
    -Name 'QwenModelManifest.cs'
$sidecarAsrRevision = Get-RequiredRegexCapture `
    -Source $qwenSidecarSource `
    -Pattern '^ASR_REVISION\s*=\s*"([0-9a-f]{40})"' `
    -Name 'qwen_sidecar.py'
$sidecarAlignerRevision = Get-RequiredRegexCapture `
    -Source $qwenSidecarSource `
    -Pattern '^ALIGNER_REVISION\s*=\s*"([0-9a-f]{40})"' `
    -Name 'qwen_sidecar.py'

$asrRevisionSet = @(@($lockedAsrRevision, $manifestAsrRevision, $sidecarAsrRevision) | Sort-Object -Unique)
$alignerRevisionSet = @(@($lockedAlignerRevision, $manifestAlignerRevision, $sidecarAlignerRevision) | Sort-Object -Unique)
if ($asrRevisionSet.Count -ne 1 -or $alignerRevisionSet.Count -ne 1) {
    throw '依赖锁、C# 运行时与 Python sidecar 中的 ModelScope Qwen 模型版本不一致。'
}

$lockedWhisperRevision = [string]$dependencyLock.models.fasterWhisperLargeV3Turbo.revision
$lockedWhisperRepository = [string]$dependencyLock.models.fasterWhisperLargeV3Turbo.repository
$lockedWhisperFileName = [string]$dependencyLock.models.fasterWhisperLargeV3Turbo.fileName
$lockedWhisperBytes = [long]$dependencyLock.models.fasterWhisperLargeV3Turbo.fileSizeBytes
$lockedWhisperSha256 = [string]$dependencyLock.models.fasterWhisperLargeV3Turbo.sha256
$manifestWhisperRevision = Get-RequiredRegexCapture `
    -Source $whisperManifestSource `
    -Pattern 'internal\s+const\s+string\s+Revision\s*=\s*"([0-9a-f]{40})"' `
    -Name 'FasterWhisperModelManifest.cs'
$manifestWhisperRepository = Get-RequiredRegexCapture `
    -Source $whisperManifestSource `
    -Pattern 'internal\s+const\s+string\s+Repository\s*=\s*"([^"]+)"' `
    -Name 'FasterWhisperModelManifest.cs'
$manifestWhisperFileName = Get-RequiredRegexCapture `
    -Source $whisperManifestSource `
    -Pattern 'internal\s+const\s+string\s+ModelFileName\s*=\s*"([^"]+)"' `
    -Name 'FasterWhisperModelManifest.cs'
$manifestWhisperSha256 = Get-RequiredRegexCapture `
    -Source $whisperManifestSource `
    -Pattern 'internal\s+const\s+string\s+ModelFileSha256\s*=\s*"([0-9a-f]{64})"' `
    -Name 'FasterWhisperModelManifest.cs'
$manifestWhisperBytesText = Get-RequiredRegexCapture `
    -Source $whisperManifestSource `
    -Pattern 'internal\s+const\s+long\s+ModelFileSizeBytes\s*=\s*([0-9_]+)' `
    -Name 'FasterWhisperModelManifest.cs'
$manifestWhisperBytes = [long]($manifestWhisperBytesText -replace '_', '')
if ($lockedWhisperRevision -notmatch '^[0-9a-f]{40}$' -or
    $lockedWhisperSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [string]::IsNullOrWhiteSpace($lockedWhisperRepository) -or
    [string]::IsNullOrWhiteSpace($lockedWhisperFileName) -or
    $lockedWhisperBytes -le 0 -or
    $lockedWhisperRevision -ne $manifestWhisperRevision -or
    $lockedWhisperRepository -ne $manifestWhisperRepository -or
    $lockedWhisperFileName -ne $manifestWhisperFileName -or
    $lockedWhisperBytes -ne $manifestWhisperBytes -or
    $lockedWhisperSha256 -ne $manifestWhisperSha256) {
    throw '依赖锁与 Faster-Whisper C# 运行时模型描述不一致。'
}

$ffmpegRequiredFiles = @($dependencyLock.nativeDependencies.ffmpeg.requiredFiles)
$transcribeRequiredFiles = @($dependencyLock.nativeDependencies.transcribeCpp.requiredFiles)
Assert-RequiredPayloadFiles `
    -Root (Join-Path $NativeRoot 'tools\ffmpeg') `
    -RelativePaths $ffmpegRequiredFiles `
    -Name 'FFmpeg 原生依赖'
Assert-RequiredPayloadFiles `
    -Root (Join-Path $NativeRoot 'tools\transcribe') `
    -RelativePaths $transcribeRequiredFiles `
    -Name 'transcribe.cpp 原生依赖'
Assert-RequiredPayloadFiles `
    -Root $qwenToolSourceRoot `
    -RelativePaths $qwenToolRequiredFiles `
    -Name 'Qwen 高精度模式工具'
Assert-RequiredPayloadFiles `
    -Root $whisperToolSourceRoot `
    -RelativePaths $whisperToolRequiredFiles `
    -Name 'Whisper Turbo 快速模式工具'

Push-Location $repositoryRoot
try {
    Write-Host "使用 .NET SDK $sdkVersionText 还原依赖…"
    & $dotnet.Source 'restore' $projectFile `
        '--runtime' 'win-x64' `
        '-p:PublishReadyToRun=true'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore 失败，退出码：$LASTEXITCODE"
    }
    & $dotnet.Source 'restore' $engineProjectFile `
        '--runtime' 'win-x64' `
        '-p:PublishReadyToRun=true'
    if ($LASTEXITCODE -ne 0) {
        throw "EngineHost 的 dotnet restore 失败，退出码：$LASTEXITCODE"
    }

    if (-not $SkipTests) {
        $pythonLauncher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
        $pythonArguments = @('-3')
        if ($null -eq $pythonLauncher) {
            $pythonLauncher = Get-Command 'python.exe' -ErrorAction SilentlyContinue
            $pythonArguments = @()
        }
        if ($null -eq $pythonLauncher) {
            throw '运行 Qwen 和 Whisper sidecar 测试需要 Python 3。'
        }

        Write-Host '运行 Qwen sidecar Python 测试…'
        & $pythonLauncher.Source @pythonArguments '-m' 'unittest' 'discover' `
            '-s' (Join-Path $repositoryRoot 'tools\qwen\tests') '-v'
        if ($LASTEXITCODE -ne 0) {
            throw "Qwen sidecar Python 测试失败，退出码：$LASTEXITCODE"
        }

        Write-Host '运行 Whisper sidecar Python 测试…'
        & $pythonLauncher.Source @pythonArguments '-m' 'unittest' 'discover' `
            '-s' (Join-Path $repositoryRoot 'tools\whisper\tests') '-v'
        if ($LASTEXITCODE -ne 0) {
            throw "Whisper sidecar Python 测试失败，退出码：$LASTEXITCODE"
        }

        & $pythonLauncher.Source @pythonArguments '-m' 'py_compile' `
            (Join-Path $repositoryRoot 'tools\qwen\qwen_sidecar.py')
        if ($LASTEXITCODE -ne 0) {
            throw "Qwen sidecar Python 语法检查失败，退出码：$LASTEXITCODE"
        }

        & $pythonLauncher.Source @pythonArguments '-m' 'py_compile' `
            (Join-Path $repositoryRoot 'tools\whisper\whisper_sidecar.py')
        if ($LASTEXITCODE -ne 0) {
            throw "Whisper sidecar Python 语法检查失败，退出码：$LASTEXITCODE"
        }

        $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -File -Recurse -ErrorAction SilentlyContinue)
        foreach ($testProject in $testProjects) {
            Write-Host "运行测试：$($testProject.Name)"
            & $dotnet.Source 'test' $testProject.FullName '--configuration' $Configuration
            if ($LASTEXITCODE -ne 0) {
                throw "测试失败：$($testProject.FullName)"
            }
        }
    }

    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $enginePublishRoot) {
        Remove-Item -LiteralPath $enginePublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $enginePublishRoot -Force | Out-Null

    Write-Host '发布 Windows x64 自包含应用…'
    & $dotnet.Source 'publish' $projectFile `
        '--configuration' $Configuration `
        '--runtime' 'win-x64' `
        '--self-contained' 'true' `
        '--no-restore' `
        '--output' $publishRoot `
        '-p:PublishSingleFile=false' `
        '-p:PublishReadyToRun=true' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false' `
        "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    Write-Host '发布隔离的原生推理宿主…'
    & $dotnet.Source 'publish' $engineProjectFile `
        '--configuration' $Configuration `
        '--runtime' 'win-x64' `
        '--self-contained' 'true' `
        '--no-restore' `
        '--output' $enginePublishRoot `
        '-p:PublishSingleFile=false' `
        '-p:PublishReadyToRun=true' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false' `
        "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "EngineHost 的 dotnet publish 失败，退出码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $packageRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $NativeRoot 'tools') -Destination (Join-Path $packageRoot 'tools') -Recurse -Force
$qwenToolPackageRoot = Join-Path $packageRoot 'tools\qwen'
New-Item -ItemType Directory -Path $qwenToolPackageRoot -Force | Out-Null
foreach ($qwenToolFile in $qwenToolRequiredFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $qwenToolSourceRoot $qwenToolFile) `
        -Destination (Join-Path $qwenToolPackageRoot $qwenToolFile) `
        -Force
}
$whisperToolPackageRoot = Join-Path $packageRoot 'tools\whisper'
New-Item -ItemType Directory -Path $whisperToolPackageRoot -Force | Out-Null
foreach ($whisperToolFile in $whisperToolRequiredFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $whisperToolSourceRoot $whisperToolFile) `
        -Destination (Join-Path $whisperToolPackageRoot $whisperToolFile) `
        -Force
}
$enginePackageRoot = Join-Path $packageRoot 'tools\engine'
New-Item -ItemType Directory -Path $enginePackageRoot -Force | Out-Null
Get-ChildItem -LiteralPath $enginePublishRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $enginePackageRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\dependencies.lock.json') -Destination $packageRoot -Force

Assert-RequiredPayloadFiles `
    -Root (Join-Path $packageRoot 'tools\ffmpeg') `
    -RelativePaths $ffmpegRequiredFiles `
    -Name 'FFmpeg 打包结果'
Assert-RequiredPayloadFiles `
    -Root (Join-Path $packageRoot 'tools\transcribe') `
    -RelativePaths $transcribeRequiredFiles `
    -Name 'transcribe.cpp 打包结果'
Assert-RequiredPayloadFiles `
    -Root $qwenToolPackageRoot `
    -RelativePaths $qwenToolRequiredFiles `
    -Name 'Qwen 高精度模式打包结果'
Assert-RequiredPayloadFiles `
    -Root $whisperToolPackageRoot `
    -RelativePaths $whisperToolRequiredFiles `
    -Name 'Whisper Turbo 快速模式打包结果'

$appExecutables = @(@(
        (Join-Path $packageRoot 'MediaScribe.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
if ($appExecutables.Count -ne 1) {
    throw "无法唯一确定应用入口 EXE，找到 $($appExecutables.Count) 个候选项。"
}
$appExeName = Split-Path -Leaf $appExecutables[0]

$engineExe = Join-Path $enginePackageRoot 'InterviewScribe.EngineHost.exe'
if (-not (Test-Path -LiteralPath $engineExe -PathType Leaf)) {
    throw "缺少原生推理宿主：$engineExe"
}

Write-Host "可便携运行目录已准备：$packageRoot"
if ($SkipInstaller) {
    Write-Host '已按参数跳过 Inno Setup 安装包。'
    return
}

$isccCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($env:ISCC_PATH)) {
    $isccCandidates += $env:ISCC_PATH
}
if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
    $isccCandidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
}
$isccCandidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    # winget may install Inno Setup per-user when the build is not elevated.
    $isccCandidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
}
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($iscc)) {
    $isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $isccCommand) {
        $iscc = $isccCommand.Source
    }
}
if ([string]::IsNullOrWhiteSpace($iscc)) {
    throw 'Inno Setup 6 未安装。可执行 winget install --id JRSoftware.InnoSetup -e，或用 -SkipInstaller 只生成可便携运行目录。'
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Write-Host '生成 Inno Setup 安装程序…'
& $iscc `
    "/DMyAppVersion=$Version" `
    "/DSourceDir=$packageRoot" `
    "/DOutputDir=$releaseRoot" `
    "/DAppExeName=$appExeName" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出码：$LASTEXITCODE"
}

$installerPath = Join-Path $releaseRoot 'MediaScribe-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup 未生成预期文件：$installerPath"
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$installerHash  MediaScribe-Setup-x64.exe`n",
    $utf8WithoutBom)
Write-Host "安装包：$installerPath"
Write-Host "SHA-256：$installerHash"
Write-Host "校验文件：$checksumPath"
