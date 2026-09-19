[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string] $Version = '0.2.0',
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
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\InterviewScribe'
$enginePublishRoot = Join-Path $repositoryRoot 'artifacts\publish\InterviewScribe.EngineHost'
$packageRoot = Join-Path $repositoryRoot 'artifacts\package\InterviewScribe'
$releaseRoot = Join-Path $repositoryRoot 'artifacts\release'
$installerScript = Join-Path $repositoryRoot 'packaging\installer.iss'
$lockFile = Join-Path $repositoryRoot 'packaging\dependencies.lock.json'

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

$appExecutables = @(@(
        (Join-Path $packageRoot 'InterviewScribe.exe'),
        (Join-Path $packageRoot 'InterviewScribe.App.exe')
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

$installerPath = Join-Path $releaseRoot 'InterviewScribe-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup 未生成预期文件：$installerPath"
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$installerHash  InterviewScribe-Setup-x64.exe`n",
    $utf8WithoutBom)
Write-Host "安装包：$installerPath"
Write-Host "SHA-256：$installerHash"
Write-Host "校验文件：$checksumPath"
