[CmdletBinding()]
param(
    [ValidateSet("Auto", "Cuda", "Cpu")]
    [string]$TorchBackend = "Auto",

    [switch]$SkipLocalModels
)

# Qwen's official ModelScope repositories are public.  Do not add token
# parameters here: command lines are visible to other local processes.
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
        throw "Cannot access the system-wide Qwen installer lock. Close any other MediaScribe installer, then try again."
    }

    if (-not $InstallerMutexOwned) {
        [Console]::Error.WriteLine(
            "INTERVIEWSCRIBE_INSTALL_ALREADY_RUNNING: Another MediaScribe Qwen component installation is already running. Wait for it to finish before trying again."
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
$AsrDescriptor = $DependencyLock.models.qwen3Asr17bModelScope
$AlignerDescriptor = $DependencyLock.models.qwen3ForcedAligner06bModelScope
if ($null -eq $AsrDescriptor -or $null -eq $AlignerDescriptor) {
    throw "dependencies.lock.json does not contain both required ModelScope Qwen descriptors."
}

$AsrRepository = [string]$AsrDescriptor.repository
$AsrRevision = [string]$AsrDescriptor.revision
$AlignerRepository = [string]$AlignerDescriptor.repository
$AlignerRevision = [string]$AlignerDescriptor.revision

if ([string]::IsNullOrWhiteSpace($AsrRepository) -or
    $AsrRevision -notmatch '^[0-9a-fA-F]{40}$' -or
    [string]::IsNullOrWhiteSpace($AlignerRepository) -or
    $AlignerRevision -notmatch '^[0-9a-fA-F]{40}$' -or
    $null -eq $AsrDescriptor.files -or
    $null -eq $AlignerDescriptor.files) {
    throw "dependencies.lock.json contains an invalid ModelScope Qwen descriptor."
}

$InstallRoot = Join-Path $env:LOCALAPPDATA "InterviewScribe"
$RuntimeRoot = Join-Path $InstallRoot "qwen-runtime"
$VenvRoot = Join-Path $RuntimeRoot ".venv"
$PythonExe = Join-Path $VenvRoot "Scripts\python.exe"
$QwenModelScopeRoot = Join-Path $InstallRoot "ModelScope-Qwen3"
$AsrDestination = Join-Path $QwenModelScopeRoot "Qwen3-ASR-1.7B"
$AlignerDestination = Join-Path $QwenModelScopeRoot "Qwen3-ForcedAligner-0.6B"

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

function Invoke-PythonTextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Program,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Purpose
    )

    # Windows PowerShell's legacy native-command argument marshalling removes
    # quotes embedded in a multi-line `python -c` program.  That produced the
    # user-visible `transformers: 5.17.0` SyntaxError.  A securely created,
    # short-lived .py file preserves the source byte-for-byte and avoids putting
    # Python source in the process command line.
    $temporaryPath = Join-Path $RuntimeRoot ".installer-$Purpose-$([Guid]::NewGuid().ToString('N')).py"
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $Source,
            [System.Text.UTF8Encoding]::new($false))
        Invoke-Checked -Program $Program -CommandArguments @($temporaryPath)
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
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

function Get-SafeSnapshotFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        return $null
    }

    $normalizedRoot = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $normalizedRoot $RelativePath))
    if (-not $candidate.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return $candidate
}

function Test-PinnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $ExpectedBytes) { return $false }
    $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actualSha256 -eq $ExpectedSha256.ToLowerInvariant()
}

function Test-ModelScopeSnapshotFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][object]$Descriptor
    )

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { return $false }
    $files = @($Descriptor.files)
    if ($files.Count -eq 0) { return $false }

    $seenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        $relativePath = [string]$file.path
        $expectedBytes = [long]$file.fileSizeBytes
        $expectedSha256 = [string]$file.sha256
        $fullPath = Get-SafeSnapshotFilePath -Destination $Destination -RelativePath $relativePath
        if ($null -eq $fullPath -or
            -not $seenPaths.Add($relativePath) -or
            $expectedBytes -le 0 -or
            $expectedSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            -not (Test-PinnedFile -Path $fullPath -ExpectedBytes $expectedBytes -ExpectedSha256 $expectedSha256)) {
            return $false
        }
    }

    return $true
}

function Write-RevisionMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Revision
    )

    $markerPath = Join-Path $Destination $RevisionMarker
    $temporaryPath = "$markerPath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            "$Revision`n",
            [System.Text.UTF8Encoding]::new($false))
        if ([System.IO.File]::Exists($markerPath)) {
            [System.IO.File]::Replace($temporaryPath, $markerPath, $null)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $markerPath)
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-InstalledSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][object]$Descriptor
    )

    $markerPath = Join-Path $Destination $RevisionMarker
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { return $false }
    try {
        $markerValue = (Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8).Trim()
    }
    catch {
        return $false
    }
    if ($markerValue -ne [string]$Descriptor.revision) { return $false }
    return Test-ModelScopeSnapshotFiles -Destination $Destination -Descriptor $Descriptor
}

function Install-PinnedModelScopeSnapshot {
    param(
        [Parameter(Mandatory = $true)][object]$Descriptor,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $repository = [string]$Descriptor.repository
    $revision = [string]$Descriptor.revision
    if (Test-InstalledSnapshot -Destination $Destination -Descriptor $Descriptor) {
        Write-Host "Verified ModelScope model snapshot: $Destination"
        return
    }

    # A user may already have followed Qwen's official `modelscope download`
    # instructions.  Adopt that exact, hash-verified data instead of wasting a
    # second 6+ GB download; the marker is written only after full validation.
    if (Test-ModelScopeSnapshotFiles -Destination $Destination -Descriptor $Descriptor) {
        Write-RevisionMarker -Destination $Destination -Revision $revision
        Write-Host "Verified and adopted existing ModelScope model: $Destination"
        return
    }

    $staging = "$Destination.staging-$([Guid]::NewGuid().ToString('N'))"
    $backup = "$Destination.backup-$([Guid]::NewGuid().ToString('N'))"
    $movedExisting = $false
    $hadDownloadRepository = Test-Path Env:\INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY
    $hadDownloadRevision = Test-Path Env:\INTERVIEWSCRIBE_DOWNLOAD_REVISION
    $hadDownloadDestination = Test-Path Env:\INTERVIEWSCRIBE_DOWNLOAD_DESTINATION
    $previousDownloadRepository = $env:INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY
    $previousDownloadRevision = $env:INTERVIEWSCRIBE_DOWNLOAD_REVISION
    $previousDownloadDestination = $env:INTERVIEWSCRIBE_DOWNLOAD_DESTINATION
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $downloadCode = @'
import os
from pathlib import Path
from modelscope import snapshot_download

repository = os.environ["INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY"]
revision = os.environ["INTERVIEWSCRIBE_DOWNLOAD_REVISION"]
destination = Path(os.environ["INTERVIEWSCRIBE_DOWNLOAD_DESTINATION"])

snapshot_download(
    model_id=repository,
    revision=revision,
    local_dir=str(destination),
)
'@

    try {
        $env:INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY = $repository
        $env:INTERVIEWSCRIBE_DOWNLOAD_REVISION = $revision
        $env:INTERVIEWSCRIBE_DOWNLOAD_DESTINATION = $staging
        Write-Host "Downloading pinned ModelScope snapshot $repository at $revision ..."
        Invoke-PythonTextFile -Program $PythonExe -Source $downloadCode -Purpose "modelscope-download"

        if (-not (Test-ModelScopeSnapshotFiles -Destination $staging -Descriptor $Descriptor)) {
            throw "ModelScope snapshot hash or completeness validation failed after download: $repository"
        }
        Write-RevisionMarker -Destination $staging -Revision $revision
        if (-not (Test-InstalledSnapshot -Destination $staging -Descriptor $Descriptor)) {
            throw "ModelScope snapshot marker validation failed after download: $repository"
        }
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $backup
            $movedExisting = $true
        }
        Move-Item -LiteralPath $staging -Destination $Destination
        Write-Host "Installed ModelScope model snapshot: $Destination"
        if ($movedExisting) {
            Write-Warning "The previous invalid snapshot was retained at: $backup"
        }
    }
    catch {
        if ($movedExisting -and -not (Test-Path -LiteralPath $Destination) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $Destination
        }
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        throw
    }
    finally {
        if ($hadDownloadRepository) {
            $env:INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY = $previousDownloadRepository
        }
        else {
            Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_REPOSITORY -ErrorAction SilentlyContinue
        }
        if ($hadDownloadRevision) {
            $env:INTERVIEWSCRIBE_DOWNLOAD_REVISION = $previousDownloadRevision
        }
        else {
            Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_REVISION -ErrorAction SilentlyContinue
        }
        if ($hadDownloadDestination) {
            $env:INTERVIEWSCRIBE_DOWNLOAD_DESTINATION = $previousDownloadDestination
        }
        else {
            Remove-Item Env:\INTERVIEWSCRIBE_DOWNLOAD_DESTINATION -ErrorAction SilentlyContinue
        }
    }
}

if (-not $env:LOCALAPPDATA) {
    throw "LOCALAPPDATA is not set. Run this installer from Windows PowerShell."
}

$EffectiveTorchBackend = $TorchBackend
if ($TorchBackend -eq "Auto") {
    if ($SkipLocalModels) {
        # Runtime-only repair does not run local Qwen inference, so avoid a
        # multi-gigabyte CUDA download when models were explicitly skipped.
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
New-Item -ItemType Directory -Path $QwenModelScopeRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) {
    $BasePython = Get-CompatibleBasePython
    Write-Host "Creating an application virtual environment with $BasePython`: $VenvRoot"
    Invoke-Checked -Program $BasePython -CommandArguments @("-m", "venv", $VenvRoot)
}
elseif (-not (Test-CompatiblePython -PythonPath $PythonExe)) {
    throw (
        "The existing MediaScribe Qwen environment is not 64-bit CPython 3.11 or 3.12: $PythonExe. " +
        "Remove that application-specific environment, then run this installer again."
    )
}

Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "install", "--upgrade", "pip")

# This pair uses PyTorch's stable ABI for torchaudio.  It is intentionally kept
# independent from the PyPI resolver so optional Qwen packages cannot replace it.
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

Write-Host "Installing pinned local Qwen + ModelScope dependencies ..."
# The retired qwen3-asr-toolkit pins an incompatible Transformers API and is
# not used by this local backend. Remove it from the application-owned
# environment before installing Qwen's official local package.
& $PythonExe -m pip uninstall --yes qwen3-asr-toolkit
if ($LASTEXITCODE -ne 0) {
    throw "Could not remove the retired qwen3-asr-toolkit package (exit code $LASTEXITCODE)."
}
Invoke-Checked -Program $PythonExe -CommandArguments @(
    "-m", "pip", "install", "--extra-index-url", $TorchIndexUrl,
    "torch==$ExpectedTorchVersion",
    "torchaudio==$ExpectedTorchaudioVersion",
    "transformers==4.57.6",
    "accelerate==1.12.0",
    "nagisa==0.2.11",
    "soynlp==0.0.493",
    "librosa==0.11.0",
    "soundfile==0.13.1",
    "qwen-asr==0.0.6",
    "modelscope==1.40.1"
)
Invoke-Checked -Program $PythonExe -CommandArguments @("-m", "pip", "check")

$SmokeTest = @'
import importlib.metadata as metadata
import os
import torch
import torchaudio
import transformers
import modelscope
from qwen_asr import Qwen3ASRModel, Qwen3ForcedAligner

expected = {
    "torch": os.environ["INTERVIEWSCRIBE_EXPECTED_TORCH"],
    "torchaudio": os.environ["INTERVIEWSCRIBE_EXPECTED_TORCHAUDIO"],
    "transformers": "4.57.6",
    "accelerate": "1.12.0",
    "nagisa": "0.2.11",
    "soynlp": "0.0.493",
    "librosa": "0.11.0",
    "soundfile": "0.13.1",
    "qwen-asr": "0.0.6",
    "modelscope": "1.40.1",
}
for package, version in expected.items():
    actual = metadata.version(package)
    if actual != version:
        raise RuntimeError(f"{package}: expected {version}, got {actual}")
for name in ("AutoConfig", "AutoModel", "AutoProcessor"):
    if not hasattr(transformers, name):
        raise RuntimeError(f"transformers is missing {name}")
for name, value in {
    "Qwen3ASRModel.from_pretrained": Qwen3ASRModel.from_pretrained,
    "Qwen3ForcedAligner.from_pretrained": Qwen3ForcedAligner.from_pretrained,
    "modelscope.snapshot_download": modelscope.snapshot_download,
}.items():
    if not callable(value):
        raise RuntimeError(f"local Qwen runtime is missing callable {name}")
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
    Invoke-PythonTextFile -Program $PythonExe -Source $SmokeTest -Purpose "smoke-test"
}
finally {
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCH -ErrorAction SilentlyContinue
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCHAUDIO -ErrorAction SilentlyContinue
    Remove-Item Env:\INTERVIEWSCRIBE_EXPECTED_TORCH_BACKEND -ErrorAction SilentlyContinue
}

if (-not $SkipLocalModels) {
    Install-PinnedModelScopeSnapshot -Descriptor $AsrDescriptor -Destination $AsrDestination
    Install-PinnedModelScopeSnapshot -Descriptor $AlignerDescriptor -Destination $AlignerDestination
}
else {
    Write-Host "Skipped local ModelScope snapshots. Install them before using local high-accuracy transcription."
}

Write-Host ""
Write-Host "Qwen runtime installation completed."
Write-Host "Python: $PythonExe"
if (-not $SkipLocalModels) {
    Write-Host "ASR model: $AsrDestination"
    Write-Host "Aligner model: $AlignerDestination"
    Write-Host "Local mode does not need a network connection after installation."
}
else {
    Write-Host "Local Qwen model snapshots were intentionally skipped."
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
