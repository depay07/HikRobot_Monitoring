param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Release build failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/RecipeFileManagerTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SC6000DualMonitor/RecipeFileManager.cs SC6000DualMonitor/RecipeFilesForm.cs tests/RecipeFileManagerTests.cs
    if ($LASTEXITCODE) { throw 'Recipe file tests compilation failed' }
    & ./test-results/RecipeFileManagerTests.exe "$PWD/test-results/recipe-files-preview.png"
    if ($LASTEXITCODE) { throw 'Recipe file tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/RecipeFileSdkTests.exe tests/RecipeFileSdkTests.cs
    if ($LASTEXITCODE) { throw 'SDK FTP tests compilation failed' }
    & ./test-results/RecipeFileSdkTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk
    if ($LASTEXITCODE) { throw 'SDK FTP tests failed' }
}
finally { Pop-Location }
