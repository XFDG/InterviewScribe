[CmdletBinding()]
param(
    [ValidateSet("Auto", "Cuda", "Cpu")]
    [string]$TorchBackend = "Auto",

    [switch]$SkipLocalModels
)

# This installer intentionally accepts authentication only through HF_TOKEN.
# Never add a token parameter: process command lines are visible to other tools.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$InstallerMutexName = "Global\InterviewScribe.QwenRuntimeInstaller.v1"
$InstallerAlreadyRunningExitCode = 1618
$InstallerMutex = $null
$InstallerMutexOwned = $false

try {
    try {
        $InstallerMutex = [System.Threading.Mutex]::new($false, $InstallerMutexName)
        try {
            $InstallerMutexOwned = $InstallerMutex.WaitOne(0)
        }
        catch [System.Threading.AbandonedMutexException] {
            # WaitOne grants ownership when it reports an abandoned mutex.  The
            # previous process is gone, so it is safe to recover and continue.
            $InstallerMutexOwned = $true
            Write-Warning "A previous Qwen installer ended unexpectedly. Continuing with validation and repair."
        }
    }
    catch [System.UnauthorizedAccessException] {
        throw "Cannot access the system-wide Qwen installer lock. Close any other InterviewScribe installer, then try again."
    }

    if (-not $InstallerMutexOwned) {
        [Console]::Error.WriteLine(
            "INTERVIEWSCRIBE_INSTALL_ALREADY_RUNNING: Another InterviewScribe Qwen component installation is already running. Wait for it to finish before trying again."
        )
        exit $InstallerAlreadyRunningExitCode
    }

$RevisionMarker = ".interviewscribe-revision"

$ProductRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$LockCandidates = @(
    (Join-Path $ProductRoot "dependencies.lock.json"),
    (Join-Path $ProductRoot "packaging\dependencies.lock.json")
)
$DependencyLockPath = $LockCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($DependencyLockPath)) {
    throw "dependencies.lock.json was not found beside the installed application or in the source tree."
}

$DependencyLock = Get-Content -LiteralPath $DependencyLockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$MossDescriptor = $DependencyLock.models.mossTranscribeDiarizeQ8
$AsrDescriptor = $DependencyLock.models.qwen3Asr17bHf
$AlignerDescriptor = $DependencyLock.models.qwen3ForcedAligner06bHf
if ($null -eq $MossDescriptor -or $null -eq $AsrDescriptor -or $null -eq $AlignerDescriptor) {
    throw "dependencies.lock.json does not contain all required model descriptors."
}

$AsrRepository = [string]$AsrDescriptor.repository
$AsrRevision = [string]$AsrDescriptor.revision
$AlignerRepository = [string]$AlignerDescriptor.repository
$AlignerRevision = [string]$AlignerDescriptor.revision
$MossFileName = [string]$MossDescriptor.fileName
$MossUrl = [string]$MossDescriptor.url
$MossExpectedBytes = [long]$MossDescriptor.fileSizeBytes
$MossSha256 = [string]$MossDescriptor.sha256

if ([string]::IsNullOrWhiteSpace($AsrRepository) -or
    [string]::IsNullOrWhiteSpace($AsrRevision) -or
    [string]::IsNullOrWhiteSpace($AlignerRepository) -or
    [string]::IsNullOrWhiteSpace($AlignerRevision) -or
    [string]::IsNullOrWhiteSpace($MossFileName) -or
    [System.IO.Path]::GetFileName($MossFileName) -ne $MossFileName -or
    -not [Uri]::IsWellFormedUriString($MossUrl, [UriKind]::Absolute) -or
    -not $MossUrl.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase) -or
    $MossExpectedBytes -le 0 -or
    $MossSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "dependencies.lock.json contains an invalid model descriptor."
}

$InstallRoot = Join-Path $env:LOCALAPPDATA "InterviewScribe"
$RuntimeRoot = Join-Path $InstallRoot "qwen-runtime"
$VenvRoot = Join-Path $RuntimeRoot ".venv"
$PythonExe = Join-Path $VenvRoot "Scripts\python.exe"
$ModelsRoot = Join-Path $InstallRoot "models"
$AsrDestination = Join-Path $ModelsRoot "Qwen3-ASR-1.7B-hf"
$AlignerDestination = Join-Path $ModelsRoot "Qwen3-ForcedAligner-0.6B-hf"
$MossDestination = Join-Path $ModelsRoot $MossFileName

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
    param(
        [Parameter(Mandatory = $true)][string]$PythonPath
    )

    $CompatibilityProbe = @'
import sys

is_cpython = sys.implementation.name == bytes((99, 112, 121, 116, 104, 111, 110)).decode()
supported = is_cpython and sys.version_info[:2] in ((3, 11), (3, 12)) and sys.maxsize > 2**32
raise SystemExit(0 if supported else 3)
'@

    & $PythonPath "-c" $CompatibilityProbe 2>$null
    return $LASTEXITCODE -eq 0
}

function Get-CompatibleBasePython {
    # The Windows launcher can list PEP 514 registrations that it cannot select
    # with a short form such as `py -3.12` (notably uv-managed interpreters).
    # Resolve the registered executable paths directly so those installations
    # work without asking the user to install a duplicate copy of Python.
    $CandidatePaths = [System.Collections.Generic.List[string]]::new()
    $SeenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    $PyLauncher = Get-Command "py.exe" -ErrorAction SilentlyContinue
    if ($null -ne $PyLauncher) {
        $LauncherRows = @(& $PyLauncher.Source "-0p" 2>$null)
        foreach ($LauncherRowValue in $LauncherRows) {
            $LauncherRow = [string]$LauncherRowValue
            $DriveSeparator = $LauncherRow.IndexOf(":\")
            if ($DriveSeparator -lt 1) { continue }

            $CandidatePath = $LauncherRow.Substring($DriveSeparator - 1).Trim().Trim('"')
            if ((Test-Path -LiteralPath $CandidatePath -PathType Leaf) -and $SeenPaths.Add($CandidatePath)) {
                $CandidatePaths.Add($CandidatePath)
            }
        }
    }

    foreach ($PythonCommand in @(Get-Command "python.exe" -All -ErrorAction SilentlyContinue)) {
        $CandidatePath = [string]$PythonCommand.Source
        if (-not [string]::IsNullOrWhiteSpace($CandidatePath) -and
            (Test-Path -LiteralPath $CandidatePath -PathType Leaf) -and
            $SeenPaths.Add($CandidatePath)) {
            $CandidatePaths.Add($CandidatePath)
        }
    }

    foreach ($CandidatePath in $CandidatePaths) {
        if (Test-CompatiblePython -PythonPath $CandidatePath) {
            return $CandidatePath
        }
    }

    throw (
        "No compatible Python runtime was found. Install 64-bit Python 3.11 or 3.12, " +
        "then run this script again. Python 3.13/3.14 are not used because this release has not validated its pinned Qwen stack on them."
    )
}

function Test-InstalledSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Revision
    )
    $MarkerPath = Join-Path $Destination $RevisionMarker
    $ConfigPath = Join-Path $Destination "config.json"
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $false }
    $MarkerValue = (Get-Content -LiteralPath $MarkerPath -Raw).Trim()
    if ($MarkerValue -ne $Revision) { return $false }
    $IndexPath = Join-Path $Destination "model.safetensors.index.json"
    if (Test-Path -LiteralPath $IndexPath -PathType Leaf) {
        try {
            $Index = Get-Content -LiteralPath $IndexPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $WeightFiles = @($Index.weight_map.PSObject.Properties.Value | Sort-Object -Unique)
            if ($WeightFiles.Count -eq 0) { return $false }
            $NormalizedRoot = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            foreach ($WeightFileValue in $WeightFiles) {
                $WeightFile = [string]$WeightFileValue
                $FullWeightPath = [System.IO.Path]::GetFullPath((Join-Path $NormalizedRoot $WeightFile))
                if (-not $FullWeightPath.StartsWith($NormalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
                if (-not (Test-Path -LiteralPath $FullWeightPath -PathType Leaf)) { return $false }
                if ((Get-Item -LiteralPath $FullWeightPath).Length -le 0) { return $false }
            }
            return $true
        }
        catch {
            return $false
        }
    }

    $Weights = @(Get-ChildItem -LiteralPath $Destination -Filter "*.safetensors" -File -ErrorAction SilentlyContinue)
    return ($Weights.Count -gt 0 -and @($Weights | Where-Object { $_.Length -le 0 }).Count -eq 0)
}

function Test-PinnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $ExpectedBytes) { return $false }
    $ActualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $ActualSha256 -eq $ExpectedSha256.ToLowerInvariant()
}

function Publish-PinnedMossPartial {
    param(
        [Parameter(Mandatory = $true)][string]$PartialPath
    )

    if (-not (Test-Path -LiteralPath $PartialPath -PathType Leaf)) { return $false }
    $PartialLength = (Get-Item -LiteralPath $PartialPath).Length
    if ($PartialLength -ne $MossExpectedBytes) {
        if ($PartialLength -gt $MossExpectedBytes) {
            Remove-Item -LiteralPath $PartialPath -Force
        }
        return $false
    }

    if (-not (Test-PinnedFile -Path $PartialPath -ExpectedBytes $MossExpectedBytes -ExpectedSha256 $MossSha256)) {
        Remove-Item -LiteralPath $PartialPath -Force
        return $false
    }

    if (Test-Path -LiteralPath $MossDestination -PathType Leaf) {
        $BackupPath = "$MossDestination.invalid-$([Guid]::NewGuid().ToString('N'))"
        try {
            [System.IO.File]::Replace($PartialPath, $MossDestination, $BackupPath, $true)
        }
        finally {
            Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Move-Item -LiteralPath $PartialPath -Destination $MossDestination
    }

    Write-Host "Installed MOSS speaker model: $MossDestination"
    return $true
}

function Install-PinnedMossModel {
    if (Test-PinnedFile -Path $MossDestination -ExpectedBytes $MossExpectedBytes -ExpectedSha256 $MossSha256) {
        Write-Host "Verified MOSS speaker model: $MossDestination"
        return
    }

    $CurlCommand = Get-Command "curl.exe" -ErrorAction SilentlyContinue
    if ($null -eq $CurlCommand) {
        throw "curl.exe was not found. Install the Windows curl component, then run this script again."
    }

    $PartialPath = "$MossDestination.part"
    if (Publish-PinnedMossPartial -PartialPath $PartialPath) {
        return
    }

    for ($Attempt = 1; $Attempt -le 3; $Attempt++) {
        try {
            Write-Host "Downloading pinned MOSS speaker model (attempt $Attempt/3; resume enabled) ..."
            Invoke-Checked -Program $CurlCommand.Source -CommandArguments @(
                "--fail", "--location", "--retry", "2", "--retry-delay", "2",
                "--continue-at", "-", "--output", $PartialPath, $MossUrl
            )

            if (-not (Publish-PinnedMossPartial -PartialPath $PartialPath)) {
                Remove-Item -LiteralPath $PartialPath -Force -ErrorAction SilentlyContinue
                throw "MOSS model size or SHA-256 validation failed."
            }

            return
        }
        catch {
            if ($Attempt -eq 3) { throw }
            Write-Warning "MOSS model download attempt $Attempt/3 failed: $($_.Exception.Message)"
        }
    }
}

function Install-PinnedSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Revision,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-InstalledSnapshot -Destination $Destination -Revision $Revision) {
        Write-Host "Verified model snapshot: $Destination"
        return
    }

    $Staging = "$Destination.staging-$([Guid]::NewGuid().ToString('N'))"
    $Backup = "$Destination.backup-$([Guid]::NewGuid().ToString('N'))"
    $MovedExisting = $false
    New-Item -ItemType Directory -Path $Staging -Force | Out-Null

    $DownloadCode = @'
import os
from pathlib import Path
from huggingface_hub import snapshot_download

repository = os.environ["INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY"]
revision = os.environ["INTERVIEWSCRIBE_DOWNLOAD_REVISION"]
destination = Path(os.environ["INTERVIEWSCRIBE_DOWNLOAD_DESTINATION"])
token = os.environ.get("HF_TOKEN") or None

snapshot_download(
    repo_id=repository,
    revision=revision,
    local_dir=str(destination),
    token=token,
)

if not (destination / "config.json").is_file():
    raise RuntimeError("Downloaded snapshot is missing config.json")
if not any(destination.glob("*.safetensors")):
    raise RuntimeError("Downloaded snapshot is missing safetensors weights")

marker = destination / ".interviewscribe-revision"
temporary = destination / (marker.name + ".tmp")
with temporary.open("w", encoding="utf-8", newline="\n") as stream:
    stream.write(revision + "\n")
    stream.flush()
    os.fsync(stream.fileno())
os.replace(temporary, marker)
'@

    try {
        $env:INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY = $Repository
        $env:INTERVIEWSCRIBE_DOWNLOAD_REVISION = $Revision
        $env:INTERVIEWSCRIBE_DOWNLOAD_DESTINATION = $Staging
        Write-Host "Downloading pinned snapshot $Repository at $Revision ..."
        Invoke-Checked -Program $PythonExe -CommandArguments @("-c", $DownloadCode)

        if (-not (Test-InstalledSnapshot -Destination $Staging -Revision $Revision)) {
            throw "Snapshot validation failed after download: $Repository"
        }
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $Backup
            $MovedExisting = $true
        }
        Move-Item -LiteralPath $Staging -Destination $Destination
        Write-Host "Installed model snapshot: $Destination"
        if ($MovedExisting) {
            Write-Warning "The previous invalid snapshot was retained at: $Backup"
        }
    }
    catch {
        if ($MovedExisting -and -not (Test-Path -LiteralPath $Destination) -and (Test-Path -LiteralPath $Backup)) {
            Move-Item -LiteralPath $Backup -Destination $Destination
        }
        if (Test-Path -LiteralPath $Staging) {
            Remove-Item -LiteralPath $Staging -Recurse -Force
        }
        throw
    }
    finally {
        Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY -ErrorAction SilentlyContinue
        Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_REVISION -ErrorAction SilentlyContinue
        Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_DESTINATION -ErrorAction SilentlyContinue
    }
}

if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA is not set. Run this installer from Windows PowerShell."
}

$EffectiveTorchBackend = $TorchBackend
if ($TorchBackend -eq "Auto") {
    if ($SkipLocalModels) {
        # SDK mode only needs local VAD/post-processing, so avoid installing a
        # multi-gigabyte CUDA runtime that will never run Qwen inference.
        $EffectiveTorchBackend = "Cpu"
    }
    else {
        $HasNvidiaAdapter = $null -ne (Get-Command "nvidia-smi.exe" -ErrorAction SilentlyContinue)
        if (-not $HasNvidiaAdapter) {
            try {
                $HasNvidiaAdapter = $null -ne (Get-CimInstance Win32_VideoController -ErrorAction Stop |
                    Where-Object { $_.Name -match "NVIDIA" } |
                    Select-Object -First 1)
            }
            catch {
                $HasNvidiaAdapter = $false
            }
        }
        $EffectiveTorchBackend = if ($HasNvidiaAdapter) { "Cuda" } else { "Cpu" }
    }
}
Write-Host "Selected PyTorch backend: $EffectiveTorchBackend (requested: $TorchBackend)"

New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $ModelsRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) {
    $BasePython = Get-CompatibleBasePython
    Write-Host "Creating an application virtual environment with $BasePython`: $VenvRoot"
    Invoke-Checked -Program $BasePython -CommandArguments @("-m", "venv", $VenvRoot)
}
elseif (-not (Test-CompatiblePython -PythonPath $PythonExe)) {
    throw (
        "The existing InterviewScribe Qwen environment is not 64-bit CPython 3.11 or 3.12: $PythonExe. " +
        "Remove that application-specific environment, then run this installer again."
    )
}

Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "install", "--upgrade", "pip")

# This pair uses PyTorch's stable ABI for torchaudio.  It is intentionally kept
# independent from the PyPI resolver so qwen3-asr-toolkit cannot replace it.
$ExpectedTorchVersion = if ($EffectiveTorchBackend -eq "Cuda") { "2.11.0+cu130" } else { "2.11.0+cpu" }
$ExpectedTorchaudioVersion = if ($EffectiveTorchBackend -eq "Cuda") { "2.11.0+cu130" } else { "2.11.0+cpu" }
$TorchIndexUrl = if ($EffectiveTorchBackend -eq "Cuda") {
    "https://download.pytorch.org/whl/cu130"
}
else {
    "https://download.pytorch.org/whl/cpu"
}

if ($EffectiveTorchBackend -eq "Cuda") {
    Write-Host "Installing pinned CUDA 13.0 PyTorch runtime ..."
    Invoke-Checked -Program $PythonExe -CommandArguments @(
        "-m", "pip", "install", "--index-url", $TorchIndexUrl,
        "torch==$ExpectedTorchVersion", "torchaudio==$ExpectedTorchaudioVersion"
    )
}
else {
    Write-Host "Installing pinned CPU PyTorch runtime ..."
    Invoke-Checked -Program $PythonExe -CommandArguments @(
        "-m", "pip", "install", "--index-url", $TorchIndexUrl,
        "torch==$ExpectedTorchVersion", "torchaudio==$ExpectedTorchaudioVersion"
    )
}

Write-Host "Installing pinned Qwen local and SDK dependencies ..."
Invoke-Checked -Program $PythonExe -CommandArguments @(
    "-m", "pip", "install", "--extra-index-url", $TorchIndexUrl,
    "torch==$ExpectedTorchVersion",
    "torchaudio==$ExpectedTorchaudioVersion",
    "transformers==5.17.0",
    "huggingface-hub==1.32.0",
    "qwen3-asr-toolkit==1.0.4",
    "dashscope==1.27.6",
    "silero-vad[onnx-cpu]==6.2.1"
)
Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "check")

$SmokeTest = @'
import importlib.metadata as metadata
import os
import torch
import torchaudio
import transformers
from qwen3_asr_toolkit.audio_tools import load_audio, process_vad, save_audio_file
from qwen3_asr_toolkit.qwen3asr import QwenASR
from silero_vad import load_silero_vad

expected = {
    "torch": os.environ["INTERVIEWSCRIBE_EXPECTED_TORCH"],
    "torchaudio": os.environ["INTERVIEWSCRIBE_EXPECTED_TORCHAUDIO"],
    "transformers": "5.17.0",
    "huggingface-hub": "1.32.0",
    "qwen3-asr-toolkit": "1.0.4",
    "dashscope": "1.27.6",
    "silero-vad": "6.2.1",
}
for package, version in expected.items():
    actual = metadata.version(package)
    if actual != version:
        raise RuntimeError(f"{package}: expected {version}, got {actual}")
for name in ("AutoProcessor", "AutoModelForMultimodalLM", "AutoModelForTokenClassification"):
    if not hasattr(transformers, name):
        raise RuntimeError(f"transformers is missing {name}")
for name, value in {
    "load_audio": load_audio,
    "process_vad": process_vad,
    "save_audio_file": save_audio_file,
    "QwenASR.post_text_process": QwenASR(model="qwen3-asr-flash").post_text_process,
}.items():
    if not callable(value):
        raise RuntimeError(f"qwen3-asr-toolkit is missing callable {name}")
load_silero_vad(onnx=True)
backend = os.environ["INTERVIEWSCRIBE_EXPECTED_TORCH_BACKEND"]
if backend == "Cuda":
    if torch.version.cuda != "13.0":
        raise RuntimeError(f"expected CUDA 13.0 build, got torch.version.cuda={torch.version.cuda!r}")
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA 13.0 runtime was installed, but PyTorch cannot use the NVIDIA GPU. "
            "Update the NVIDIA driver, or rerun with -TorchBackend Cpu."
        )
if backend == "Cpu" and torch.version.cuda is not None:
    raise RuntimeError(f"expected CPU-only build, got torch.version.cuda={torch.version.cuda!r}")
print(f"Validated torch {torch.__version__}, torchaudio {torchaudio.__version__}, transformers {transformers.__version__}")
print(f"CUDA available: {torch.cuda.is_available()}")
'@
try {
    $env:INTERVIEWSCRIBE_EXPECTED_TORCH = $ExpectedTorchVersion
    $env:INTERVIEWSCRIBE_EXPECTED_TORCHAUDIO = $ExpectedTorchaudioVersion
    $env:INTERVIEWSCRIBE_EXPECTED_TORCH_BACKEND = $EffectiveTorchBackend
    Invoke-Checked -Program $PythonExe -CommandArguments @("-c", $SmokeTest)
}
finally {
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCH -ErrorAction SilentlyContinue
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCHAUDIO -ErrorAction SilentlyContinue
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCH_BACKEND -ErrorAction SilentlyContinue
}

$env:HF_HUB_DISABLE_TELEMETRY = "1"
Install-PinnedMossModel
if (-not $SkipLocalModels) {
    Install-PinnedSnapshot -Repository $AsrRepository -Revision $AsrRevision -Destination $AsrDestination
    Install-PinnedSnapshot -Repository $AlignerRepository -Revision $AlignerRevision -Destination $AlignerDestination
}
else {
    Write-Host "Skipped local model snapshots. SDK mode is ready; DASHSCOPE_API_KEY is required at run time."
}

Write-Host ""
Write-Host "Qwen runtime installation completed."
Write-Host "Python: $PythonExe"
if (-not $SkipLocalModels) {
    Write-Host "MOSS model: $MossDestination"
    Write-Host "ASR model: $AsrDestination"
    Write-Host "Aligner model: $AlignerDestination"
    Write-Host "Local mode does not need a network connection after installation."
}
else {
    Write-Host "MOSS model: $MossDestination"
    Write-Host "SDK mode still needs a network connection and DASHSCOPE_API_KEY at run time."
}
}
finally {
    if ($InstallerMutexOwned -and $null -ne $InstallerMutex) {
        try {
            $InstallerMutex.ReleaseMutex()
        }
        catch [System.ApplicationException] {
            Write-Warning "The Qwen installer lock was no longer owned while cleaning up."
        }
    }

    if ($null -ne $InstallerMutex) {
        $InstallerMutex.Dispose()
    }
}
