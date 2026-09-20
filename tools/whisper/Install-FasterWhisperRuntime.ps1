[CmdletBinding()]
param(
    [ValidateSet("Auto", "Cuda", "Cpu")]
    [string]$Backend = "Auto"
)

# Installs an application-isolated Faster-Whisper runtime.  No packages are
# installed into the user's system Python and no model is resolved at inference
# time after this script succeeds.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$MutexName = "Global\MediaScribe.FasterWhisperInstaller.v1"
$Mutex = $null
$OwnsMutex = $false
$RevisionMarker = ".mediascribe-revision"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Program,
        [Parameter(Mandatory = $true)][string[]]$CommandArguments
    )

    & $Program @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Program"
    }
}

function Test-CompatiblePython {
    param([Parameter(Mandatory = $true)][string]$PythonPath)

    $Probe = @'
import sys
ok = sys.implementation.name == "cpython" and sys.version_info[:2] in ((3, 11), (3, 12)) and sys.maxsize > 2**32
raise SystemExit(0 if ok else 3)
'@
    $Temporary = [System.IO.Path]::GetTempFileName() + ".py"
    try {
        [System.IO.File]::WriteAllText($Temporary, $Probe, [System.Text.UTF8Encoding]::new($false))
        & $PythonPath $Temporary 2>$null
        return $LASTEXITCODE -eq 0
    }
    finally {
        Remove-Item -LiteralPath $Temporary -Force -ErrorAction SilentlyContinue
    }
}

function Get-CompatibleBasePython {
    $CandidatePaths = [System.Collections.Generic.List[string]]::new()
    $SeenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $PyLauncher = Get-Command "py.exe" -ErrorAction SilentlyContinue
    if ($null -ne $PyLauncher) {
        foreach ($RowValue in @(& $PyLauncher.Source "-0p" 2>$null)) {
            $Row = [string]$RowValue
            $Separator = $Row.IndexOf(":\")
            if ($Separator -lt 1) { continue }
            $Candidate = $Row.Substring($Separator - 1).Trim().Trim('"')
            if ((Test-Path -LiteralPath $Candidate -PathType Leaf) -and $SeenPaths.Add($Candidate)) {
                $CandidatePaths.Add($Candidate)
            }
        }
    }

    foreach ($Command in @(Get-Command "python.exe" -All -ErrorAction SilentlyContinue)) {
        $Candidate = [string]$Command.Source
        if (-not [string]::IsNullOrWhiteSpace($Candidate) -and
            (Test-Path -LiteralPath $Candidate -PathType Leaf) -and $SeenPaths.Add($Candidate)) {
            $CandidatePaths.Add($Candidate)
        }
    }

    foreach ($Candidate in $CandidatePaths) {
        if (Test-CompatiblePython -PythonPath $Candidate) { return $Candidate }
    }
    throw "找不到 64 位 CPython 3.11 或 3.12。请先安装其中一个版本，再安装快速模式组件。"
}

function Write-TemporaryPythonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Content
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $Path = Join-Path $Directory (".installer-" + [Guid]::NewGuid().ToString("N") + ".py")
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
    return $Path
}

function Test-InstalledModel {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Revision,
        [Parameter(Mandatory = $true)][string]$ModelFileName,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string[]]$RequiredFiles
    )

    foreach ($RequiredFile in $RequiredFiles) {
        $Path = Join-Path $Destination $RequiredFile
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -le 0) {
            return $false
        }
    }
    $MarkerPath = Join-Path $Destination $RevisionMarker
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf) -or
        (Get-Content -LiteralPath $MarkerPath -Raw -Encoding UTF8).Trim() -ne $Revision) {
        return $false
    }
    $ModelPath = Join-Path $Destination $ModelFileName
    if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf) -or (Get-Item -LiteralPath $ModelPath).Length -ne $ExpectedBytes) {
        return $false
    }
    return (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedSha256.ToLowerInvariant()
}

try {
    $Mutex = [System.Threading.Mutex]::new($false, $MutexName)
    try {
        $OwnsMutex = $Mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $OwnsMutex = $true
        Write-Warning "检测到之前的快速模式安装异常结束；将继续校验和修复。"
    }
    if (-not $OwnsMutex) {
        [Console]::Error.WriteLine("MEDIASCRIBE_WHISPER_INSTALL_ALREADY_RUNNING: Another fast-mode installation is already running.")
        exit 1618
    }

    if (-not $env:LOCALAPPDATA) {
        throw "LOCALAPPDATA 未设置。请从 Windows PowerShell 运行此脚本。"
    }

    $ProductRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    # The installed payload keeps the lock next to the EXE; a source-tree
    # launch keeps it under packaging/.  Supporting both lets the same script
    # be tested and manually repaired before a release is built.
    $LockCandidates = @(
        (Join-Path $ProductRoot "dependencies.lock.json"),
        (Join-Path $ProductRoot "packaging\\dependencies.lock.json")
    )
    $LockPath = $LockCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($LockPath)) {
        throw "找不到 dependencies.lock.json。请重新安装 MediaScribe。"
    }
    $Lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $Descriptor = $Lock.models.fasterWhisperLargeV3Turbo
    if ($null -eq $Descriptor) {
        throw "当前安装包不包含 Faster-Whisper 模型描述。请升级到支持快速模式的 MediaScribe 版本。"
    }

    $Repository = [string]$Descriptor.repository
    $Revision = [string]$Descriptor.revision
    $ModelFileName = [string]$Descriptor.fileName
    $ExpectedBytes = [long]$Descriptor.fileSizeBytes
    $ExpectedSha256 = [string]$Descriptor.sha256
    $RequiredFiles = @($Descriptor.requiredFiles | ForEach-Object { [string]$_ })
    if ([string]::IsNullOrWhiteSpace($Repository) -or $Revision -notmatch '^[0-9a-f]{40}$' -or
        [string]::IsNullOrWhiteSpace($ModelFileName) -or [System.IO.Path]::GetFileName($ModelFileName) -ne $ModelFileName -or
        $ExpectedBytes -le 0 -or $ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $RequiredFiles.Count -eq 0) {
        throw "dependencies.lock.json 中的 Faster-Whisper 模型描述无效。"
    }
    foreach ($RequiredFile in $RequiredFiles) {
        if ([string]::IsNullOrWhiteSpace($RequiredFile) -or
            [System.IO.Path]::IsPathRooted($RequiredFile) -or
            $RequiredFile -match '(^|[\\/])\.\.([\\/]|$)') {
            throw "dependencies.lock.json 中的 Faster-Whisper requiredFiles 包含无效路径：$RequiredFile"
        }
    }
    if ($RequiredFiles -notcontains $ModelFileName) {
        throw "dependencies.lock.json 中的 Faster-Whisper requiredFiles 未包含模型权重：$ModelFileName"
    }

    $InstallRoot = Join-Path $env:LOCALAPPDATA "InterviewScribe"
    $RuntimeRoot = Join-Path $InstallRoot "whisper-runtime"
    $VenvRoot = Join-Path $RuntimeRoot ".venv"
    $PythonExe = Join-Path $VenvRoot "Scripts\python.exe"
    $ModelsRoot = Join-Path $InstallRoot "models"
    $Destination = Join-Path $ModelsRoot "Whisper-large-v3-turbo-ct2"
    New-Item -ItemType Directory -Path $RuntimeRoot, $ModelsRoot -Force | Out-Null

    if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) {
        $BasePython = Get-CompatibleBasePython
        Write-Host "正在创建快速模式专用 Python 环境：$VenvRoot"
        Invoke-Checked -Program $BasePython -CommandArguments @("-m", "venv", $VenvRoot)
    }
    elseif (-not (Test-CompatiblePython -PythonPath $PythonExe)) {
        throw "现有 Faster-Whisper 环境不是受支持的 64 位 CPython 3.11/3.12：$PythonExe"
    }

    $EffectiveBackend = $Backend
    if ($Backend -eq "Auto") {
        $EffectiveBackend = if ($null -ne (Get-Command "nvidia-smi.exe" -ErrorAction SilentlyContinue)) { "Cuda" } else { "Cpu" }
    }
    Write-Host "Selected Faster-Whisper backend: $EffectiveBackend (requested: $Backend)"

    Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "install", "--upgrade", "pip")
    $RuntimePackages = @(
        "-m", "pip", "install", "--upgrade",
        "faster-whisper==1.2.1",
        "ctranslate2==4.8.2",
        "huggingface-hub==1.32.0"
    )
    if ($EffectiveBackend -eq "Cuda") {
        # CUDA 12 wheels are intentionally isolated in this venv.  They
        # coexist with the application's CUDA 13 PyTorch environment.
        $RuntimePackages += @(
            "nvidia-cuda-runtime-cu12==12.9.79",
            "nvidia-cublas-cu12==12.9.2.10",
            "nvidia-cudnn-cu12==9.26.0.51"
        )
    }
    Invoke-Checked -Program $PythonExe -CommandArguments $RuntimePackages
    Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "check")

    $SmokeCode = @'
import os
import site
from pathlib import Path
handles = []
directories = []
if os.name == "nt" and hasattr(os, "add_dll_directory"):
    for root in map(Path, site.getsitepackages()):
        nvidia_root = root / "nvidia"
        if not nvidia_root.is_dir():
            continue
        for package_root in sorted(nvidia_root.iterdir(), key=lambda item: item.name.lower()):
            candidate = package_root / "bin"
            if candidate.is_dir() and str(candidate) not in directories:
                directories.append(str(candidate))
                handles.append(os.add_dll_directory(str(candidate)))
    if directories:
        os.environ["PATH"] = os.pathsep.join([*directories, os.environ.get("PATH", "")])
import ctranslate2
import faster_whisper
from importlib import metadata
print("Validated faster-whisper", metadata.version("faster-whisper"), "ctranslate2", ctranslate2.__version__)
print("CUDA devices:", ctranslate2.get_cuda_device_count())
'@
    $SmokePath = Write-TemporaryPythonFile -Directory $RuntimeRoot -Content $SmokeCode
    try {
        Invoke-Checked -Program $PythonExe -CommandArguments @($SmokePath)
    }
    finally {
        Remove-Item -LiteralPath $SmokePath -Force -ErrorAction SilentlyContinue
    }

    if ($EffectiveBackend -eq "Cuda") {
        $CudaProbe = @'
import ctypes, os, site
from pathlib import Path
handles = []
directories = []
for root in map(Path, site.getsitepackages()):
    nvidia_root = root / "nvidia"
    if not nvidia_root.is_dir():
        continue
    for package_root in sorted(nvidia_root.iterdir(), key=lambda item: item.name.lower()):
        candidate = package_root / "bin"
        if candidate.is_dir() and str(candidate) not in directories:
            directories.append(str(candidate))
            handles.append(os.add_dll_directory(str(candidate)))
if directories:
    os.environ["PATH"] = os.pathsep.join([*directories, os.environ.get("PATH", "")])
for name in ("cudart64_12.dll", "cublas64_12.dll", "cudnn64_9.dll"):
    ctypes.WinDLL(name)
import ctranslate2
raise SystemExit(0 if ctranslate2.get_cuda_device_count() > 0 else 3)
'@
        $CudaProbePath = Write-TemporaryPythonFile -Directory $RuntimeRoot -Content $CudaProbe
        try {
            & $PythonExe $CudaProbePath
            if ($LASTEXITCODE -ne 0) {
                throw "已安装 CUDA 运行库，但 CTranslate2 没有检测到可用 NVIDIA GPU。请更新 NVIDIA 驱动，或使用 -Backend Cpu。"
            }
        }
        finally {
            Remove-Item -LiteralPath $CudaProbePath -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-InstalledModel -Destination $Destination -Revision $Revision -ModelFileName $ModelFileName -ExpectedBytes $ExpectedBytes -ExpectedSha256 $ExpectedSha256 -RequiredFiles $RequiredFiles) {
        Write-Host "已验证 Faster-Whisper 模型：$Destination"
    }
    else {
        # Keep the Hub's local download cache deliberately shallow.  Its
        # resumable temporary names include an ETag and a process suffix; a
        # staging folder below the long model name can exceed legacy Windows
        # path limits even though the final model folder itself is safe.
        # Both folders remain below LOCALAPPDATA, so Move-Item stays on one
        # volume and the final swap is still atomic.
        $DownloadStagingRoot = Join-Path $InstallRoot ".downloads"
        $Staging = Join-Path $DownloadStagingRoot ("fw-" + [Guid]::NewGuid().ToString('N'))
        $Backup = Join-Path $ModelsRoot ("Whisper-large-v3-turbo-ct2.invalid-" + [Guid]::NewGuid().ToString('N'))
        # huggingface_hub writes resumable metadata under
        # <local_dir>/.cache/huggingface/download.  Pre-create the shallow
        # staging root; the Python code below also uses an extended path for
        # the long temporary leaves created by Hub.
        New-Item -ItemType Directory -Path $DownloadStagingRoot, $Staging -Force | Out-Null
        $DownloadCode = @'
import os
from pathlib import Path
from huggingface_hub import snapshot_download
repository = os.environ["MEDIASCRIBE_WHISPER_REPOSITORY"]
revision = os.environ["MEDIASCRIBE_WHISPER_REVISION"]
destination = Path(os.environ["MEDIASCRIBE_WHISPER_DESTINATION"])
files = os.environ["MEDIASCRIBE_WHISPER_FILES"].split("|")
# Hugging Face Hub uses an ETag plus a process suffix for the partial-file
# name.  On Windows, that last suffix is added after the library's own path
# length conversion.  Always give the local-dir API an extended path so the
# installer remains reliable even when LongPathsEnabled is off.
def windows_extended_path(path: Path) -> Path:
    raw = str(path.resolve())
    if os.name != "nt" or raw.startswith("\\\\?\\"):
        return Path(raw)
    if raw.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + raw[2:])
    return Path("\\\\?\\" + raw)

hub_destination = windows_extended_path(destination)
# The model has one dominant weight file and a few small metadata files.  A
# single worker makes its resumable metadata preparation deterministic.
(hub_destination / ".cache" / "huggingface" / "download").mkdir(parents=True, exist_ok=True)
snapshot_download(
    repo_id=repository,
    revision=revision,
    local_dir=str(hub_destination),
    allow_patterns=files,
    token=os.environ.get("HF_TOKEN") or None,
    max_workers=1,
)
if any(not (hub_destination / file_name).is_file() for file_name in files):
    raise RuntimeError("Downloaded snapshot is missing required Faster-Whisper files")
(hub_destination / ".mediascribe-revision").write_text(revision + "\n", encoding="utf-8", newline="\n")
'@
        $DownloadPath = Write-TemporaryPythonFile -Directory $RuntimeRoot -Content $DownloadCode
        try {
            $env:MEDIASCRIBE_WHISPER_REPOSITORY = $Repository
            $env:MEDIASCRIBE_WHISPER_REVISION = $Revision
            $env:MEDIASCRIBE_WHISPER_DESTINATION = $Staging
            $env:MEDIASCRIBE_WHISPER_FILES = $RequiredFiles -join "|"
            Write-Host "正在下载锁定的 Whisper large-v3-turbo 模型（支持断点续传）…"
            Invoke-Checked -Program $PythonExe -CommandArguments @($DownloadPath)
            if (-not (Test-InstalledModel -Destination $Staging -Revision $Revision -ModelFileName $ModelFileName -ExpectedBytes $ExpectedBytes -ExpectedSha256 $ExpectedSha256 -RequiredFiles $RequiredFiles)) {
                throw "Faster-Whisper 模型下载后的完整性校验失败。"
            }
            if (Test-Path -LiteralPath $Destination) {
                Move-Item -LiteralPath $Destination -Destination $Backup
            }
            Move-Item -LiteralPath $Staging -Destination $Destination
            Write-Host "Faster-Whisper 模型已安装：$Destination"
        }
        finally {
            Remove-Item -LiteralPath $DownloadPath -Force -ErrorAction SilentlyContinue
            Remove-Item Env:\MEDIASCRIBE_WHISPER_REPOSITORY -ErrorAction SilentlyContinue
            Remove-Item Env:\MEDIASCRIBE_WHISPER_REVISION -ErrorAction SilentlyContinue
            Remove-Item Env:\MEDIASCRIBE_WHISPER_DESTINATION -ErrorAction SilentlyContinue
            Remove-Item Env:\MEDIASCRIBE_WHISPER_FILES -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $Staging) {
                Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ""
    Write-Host "快速模式安装完成。模型和运行组件均已保存到本机；之后可断网运行。"
    Write-Host "Python: $PythonExe"
    Write-Host "Model: $Destination"
}
finally {
    if ($OwnsMutex -and $null -ne $Mutex) {
        try { $Mutex.ReleaseMutex() } catch [System.ApplicationException] { }
    }
    if ($null -ne $Mutex) { $Mutex.Dispose() }
}
