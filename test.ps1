<#
.SYNOPSIS
Build and run regression tests against isolated synthetic files.
.DESCRIPTION
Keeps logs, test executables and fixtures in a new OutputRoot subfolder,
under LocalAppData\SapienCopy\Tests by default. Use a short local NTFS path
for OutputRoot because some native helpers retain Windows path-length limits.
Ui adds layout and interaction tests. Restartable adds a local interrupted-copy
diagnostic; it does not test physical drive removal or guarantee byte-offset resume.
.EXAMPLE
.\test.ps1 -Ui -ReleaseExe .\SapienCopy-v1.10.0.exe
#>
param(
    [string]$OutputRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SapienCopy\Tests'),
    [string]$ReleaseExe,
    [switch]$Ui,
    [switch]$Restartable
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework x64 C# compiler was not found.' }
if ($ReleaseExe) {
    if (!(Test-Path -LiteralPath $ReleaseExe -PathType Leaf)) { throw "Release executable was not found: $ReleaseExe" }
    $ReleaseExe = (Resolve-Path -LiteralPath $ReleaseExe).Path
}

$runFolder = Join-Path ($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)) ('run-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runFolder) | Out-Null
$core = @('CopyEngine.cs', 'OneDrivePolicy.cs', 'CopyEstimate.cs', 'DestinationPolicy.cs', 'CopyWorker.cs', 'VolumeIdentity.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }

function Invoke-TestSuite {
    param([string]$Name, [string]$EntryPoint, [string[]]$Sources, [string[]]$TestArguments = @())
    $executable = Join-Path $runFolder ($Name + '.exe')
    Write-Host "Building $Name"
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
        "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\SapienCopy.ico')" "/main:$EntryPoint" "/out:$executable" @Sources (Join-Path $PSScriptRoot ($Name + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed with exit code $LASTEXITCODE." }
    & $executable @TestArguments 2>&1 | Tee-Object -FilePath (Join-Path $runFolder ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE. See $runFolder" }
}

Push-Location -LiteralPath $runFolder
try {
    foreach ($name in @('CopyEngineTests', 'FileSelectionTests', 'VolumeIdentityTests', 'ResumeTests')) {
        Invoke-TestSuite $name ('SapienCopy.' + $name) $core
    }
    foreach ($name in @('EngineAuditTests', 'CopyEstimateTests', 'CopyWorkerTests')) {
        Invoke-TestSuite $name $name $core
    }
    Invoke-TestSuite 'FolderDropTests' 'FolderDropTests' @((Join-Path $PSScriptRoot 'FolderDrop.cs'))
    if ($ReleaseExe) {
        & (Join-Path $runFolder 'CopyWorkerTests.exe') --release $ReleaseExe 2>&1 |
            Tee-Object -FilePath (Join-Path $runFolder 'ReleaseWorker.log')
        if ($LASTEXITCODE -ne 0) { throw "Release worker test failed with exit code $LASTEXITCODE. See $runFolder" }
    }
    if ($Ui) {
        $uiSources = $core + @((Join-Path $PSScriptRoot 'MainForm.cs'), (Join-Path $PSScriptRoot 'FolderDrop.cs'))
        Invoke-TestSuite 'UiSmokeTest' 'UiSmokeTest' $uiSources @((Join-Path $runFolder 'preview.png'))
    }
    if ($Restartable) {
        Invoke-TestSuite 'RestartableCopyTests' 'RestartableCopyTests' $core
    }
    Write-Host "PASS: all requested suites completed. Logs and synthetic fixtures: $runFolder"
}
finally { Pop-Location }
