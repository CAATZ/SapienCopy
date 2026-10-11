param([string]$Output = (Join-Path $PSScriptRoot 'SapienCopy-v1.10.0.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\SapienCopy.ico')" "/out:$Output" `
    (Join-Path $PSScriptRoot 'AssemblyInfo.cs') (Join-Path $PSScriptRoot 'MainForm.cs') `
    (Join-Path $PSScriptRoot 'CopyEngine.cs') (Join-Path $PSScriptRoot 'OneDrivePolicy.cs') (Join-Path $PSScriptRoot 'CopyEstimate.cs') `
    (Join-Path $PSScriptRoot 'DestinationPolicy.cs') (Join-Path $PSScriptRoot 'FolderDrop.cs') (Join-Path $PSScriptRoot 'CopyWorker.cs') (Join-Path $PSScriptRoot 'VolumeIdentity.cs')
if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE." }
Get-Item -LiteralPath $Output | Select-Object FullName, Length
