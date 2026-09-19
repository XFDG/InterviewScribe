[CmdletBinding()]
param(
    [string] $DestinationRoot,
    [string] $CacheRoot,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# GitHub and Hugging Face require modern TLS.  Windows PowerShell 5.1 may still
# inherit an older .NET Framework default on machines upgraded from old Windows.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockFile = Join-Path $repositoryRoot 'packaging\dependencies.lock.json'

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $repositoryRoot 'artifacts\native'
}
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot 'artifacts\downloads'
}

$DestinationRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
$CacheRoot = [System.IO.Path]::GetFullPath($CacheRoot)

function Assert-LockEntry {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [string] $Name
    )

    if ($Entry.fileSizeBytes -le 0) {
        throw "$Name 的 fileSizeBytes 无效。"
    }
    if ([string]$Entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "$Name 的 SHA-256 无效；不允许使用占位值下载。"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Entry.url) -or
        [string]::IsNullOrWhiteSpace([string]$Entry.archiveFileName)) {
        throw "$Name 的 URL 或归档文件名缺失。"
    }
}

function Test-LockedFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Entry
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$Entry.fileSizeBytes) {
        Write-Warning "缓存文件大小不匹配：$($file.Name)"
        return $false
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actualHash -eq ([string]$Entry.sha256).ToLowerInvariant()
}

function Get-LockedArtifact {
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [string] $Name
    )

    Assert-LockEntry -Entry $Entry -Name $Name
    $archivePath = Join-Path $CacheRoot ([string]$Entry.archiveFileName)

    if (-not $Force -and (Test-LockedFile -Path $archivePath -Entry $Entry)) {
        Write-Host "使用已验证的缓存：$($Entry.archiveFileName)"
        return $archivePath
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    $partialPath = "$archivePath.partial"
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Write-Host "下载 $Name：$($Entry.url)"
    try {
        Invoke-WebRequest -Uri ([string]$Entry.url) -OutFile $partialPath -UseBasicParsing
        if (-not (Test-LockedFile -Path $partialPath -Entry $Entry)) {
            throw "$Name 的大小或 SHA-256 校验失败。已拒绝使用该文件。"
        }
        Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
    }
    catch {
        if (Test-Path -LiteralPath $partialPath) {
            Remove-Item -LiteralPath $partialPath -Force
        }
        throw
    }

    Write-Host "SHA-256 校验通过：$($Entry.archiveFileName)"
    return $archivePath
}

function Find-SingleFile {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $FileName
    )

    $matches = @(Get-ChildItem -LiteralPath $Root -Filter $FileName -File -Recurse)
    if ($matches.Count -ne 1) {
        throw "期望在归档中找到 1 个 $FileName，实际找到 $($matches.Count) 个。"
    }
    return $matches[0]
}

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

if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
    throw "找不到依赖锁文件：$lockFile"
}

$lock = Get-Content -LiteralPath $lockFile -Raw -Encoding UTF8 | ConvertFrom-Json
$ffmpeg = $lock.nativeDependencies.ffmpeg
$transcribe = $lock.nativeDependencies.transcribeCpp

New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

$ffmpegArchive = Get-LockedArtifact -Entry $ffmpeg -Name 'FFmpeg'
$transcribeArchive = Get-LockedArtifact -Entry $transcribe -Name 'transcribe.cpp'

$extractRoot = Join-Path $CacheRoot ('.extract-' + [Guid]::NewGuid().ToString('N'))
$ffmpegExtractRoot = Join-Path $extractRoot 'ffmpeg'
$transcribeExtractRoot = Join-Path $extractRoot 'transcribe'
$ffmpegDestination = Join-Path $DestinationRoot 'tools\ffmpeg'
$transcribeDestination = Join-Path $DestinationRoot 'tools\transcribe'

try {
    New-Item -ItemType Directory -Path $ffmpegExtractRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $transcribeExtractRoot -Force | Out-Null

    Write-Host '解压 FFmpeg…'
    Expand-Archive -LiteralPath $ffmpegArchive -DestinationPath $ffmpegExtractRoot -Force
    $ffmpegExe = Find-SingleFile -Root $ffmpegExtractRoot -FileName 'ffmpeg.exe'
    $ffprobeExe = Find-SingleFile -Root $ffmpegExtractRoot -FileName 'ffprobe.exe'

    if (Test-Path -LiteralPath $ffmpegDestination) {
        Remove-Item -LiteralPath $ffmpegDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ffmpegDestination -Force | Out-Null
    Copy-Item -LiteralPath $ffmpegExe.FullName -Destination (Join-Path $ffmpegDestination 'ffmpeg.exe')
    Copy-Item -LiteralPath $ffprobeExe.FullName -Destination (Join-Path $ffmpegDestination 'ffprobe.exe')

    $ffmpegLicenseRoot = Join-Path $ffmpegDestination 'licenses'
    $ffmpegDocuments = @(Get-ChildItem -LiteralPath $ffmpegExtractRoot -File -Recurse | Where-Object {
        $_.Name -match '^(LICENSE|COPYING|README)(\..*)?$'
    })
    if ($ffmpegDocuments.Count -gt 0) {
        New-Item -ItemType Directory -Path $ffmpegLicenseRoot -Force | Out-Null
        foreach ($document in $ffmpegDocuments) {
            Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $ffmpegLicenseRoot $document.Name) -Force
        }
    }

    Write-Host '解压 transcribe.cpp…'
    $tar = Get-Command 'tar.exe' -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        $tar = Get-Command 'tar' -ErrorAction SilentlyContinue
    }
    if ($null -eq $tar) {
        throw '找不到 tar。Windows 10/11 自带 tar.exe；请确认 System32 在 PATH 中。'
    }

    & $tar.Source '-xzf' $transcribeArchive '-C' $transcribeExtractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "transcribe.cpp 归档解压失败，tar 退出码：$LASTEXITCODE"
    }

    $transcribeDll = Find-SingleFile -Root $transcribeExtractRoot -FileName 'transcribe.dll'
    $transcribePayloadRoot = $transcribeDll.Directory.FullName
    Assert-RequiredPayloadFiles `
        -Root $transcribePayloadRoot `
        -RelativePaths @($transcribe.requiredFiles) `
        -Name 'transcribe.cpp 归档'
    if (Test-Path -LiteralPath $transcribeDestination) {
        Remove-Item -LiteralPath $transcribeDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $transcribeDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $transcribePayloadRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $transcribeDestination -Recurse -Force
    }

    Copy-Item -LiteralPath $lockFile -Destination (Join-Path $DestinationRoot 'dependencies.lock.json') -Force
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}

Assert-RequiredPayloadFiles `
    -Root $ffmpegDestination `
    -RelativePaths @($ffmpeg.requiredFiles) `
    -Name 'FFmpeg 发布目录'
Assert-RequiredPayloadFiles `
    -Root $transcribeDestination `
    -RelativePaths @($transcribe.requiredFiles) `
    -Name 'transcribe.cpp 发布目录'

Write-Host "原生依赖已验证并准备到：$DestinationRoot"
Write-Host '模型未下载；MOSS GGUF 由应用在首次使用时按锁定 revision 下载并校验 SHA-256。'
