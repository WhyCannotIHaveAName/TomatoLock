param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Requires Windows x64 with .NET Framework 4.8. No NuGet or downloaded installer is used.' }
$outDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$tomatoBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(222,103,79))
$leafPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(34,83,59)),5
$graphics.FillEllipse($tomatoBrush,5,15,54,46)
$graphics.DrawLine($leafPen,32,5,29,23)
$graphics.DrawLine($leafPen,29,20,16,13)
$graphics.DrawLine($leafPen,29,20,44,12)
$icon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconPath = Join-Path $outDir 'TomatoLock.ico'
$stream = [IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $tomatoBrush.Dispose(); $leafPen.Dispose()
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll')
$argsCommon = @('/nologo','/optimize+','/platform:x64','/codepage:65001') + @($refs | ForEach-Object { '/reference:' + (Join-Path $framework $_) })
& $compiler @argsCommon /target:winexe /out:build\TomatoLock.exe /win32manifest:src\app.manifest /win32icon:build\TomatoLock.ico /resource:src\Main.xaml,Main.xaml /resource:assets\tomato-badges.png,tomato-badges.png src\Core.cs src\Native.cs src\MainController.cs src\AppLauncher.cs src\TomatoArt.cs src\Program.cs
if ($LASTEXITCODE -ne 0) { throw 'App compilation failed' }
Copy-Item -LiteralPath src\TomatoLock.exe.config -Destination build\TomatoLock.exe.config -Force
& $compiler /nologo /optimize+ /platform:x64 /codepage:65001 /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:build\TomatoLock-Setup-1.1.0.exe /win32manifest:src\app.manifest /win32icon:build\TomatoLock.ico /resource:build\TomatoLock.exe,TomatoLock.exe /resource:src\TomatoLock.exe.config,TomatoLock.exe.config src\Setup.cs
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed' }
if ($Test) {
    & $compiler @argsCommon /target:exe /out:build\TomatoLock.Tests.exe src\Core.cs src\Native.cs src\AppLauncher.cs tests\Tests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & .\build\TomatoLock.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $compiler @argsCommon /target:exe /main:Smoke /out:build\TomatoLock.Smoke.exe /win32icon:build\TomatoLock.ico /resource:src\Main.xaml,Main.xaml /resource:assets\tomato-badges.png,tomato-badges.png src\Core.cs src\Native.cs src\MainController.cs src\AppLauncher.cs src\TomatoArt.cs src\Program.cs tests\Smoke.cs
    if ($LASTEXITCODE -ne 0) { throw 'UI smoke test compilation failed' }
    & .\build\TomatoLock.Smoke.exe
    if ($LASTEXITCODE -ne 0) { throw 'UI smoke tests failed' }
    $verify = Start-Process -FilePath (Join-Path $outDir 'TomatoLock-Setup-1.1.0.exe') -ArgumentList '--verify' -WindowStyle Hidden -Wait -PassThru
    if ($verify.ExitCode -ne 0) { throw 'Setup payload verification failed' }
}
if ($Package) {
    New-Item -ItemType Directory -Path releases -Force | Out-Null
    Copy-Item -LiteralPath build\TomatoLock-Setup-1.1.0.exe -Destination releases -Force
    Compress-Archive -Path build\TomatoLock.exe,build\TomatoLock.exe.config,README.md,LICENSE -DestinationPath releases\TomatoLock-Portable-1.1.0-win-x64.zip -Force
    Get-ChildItem -LiteralPath releases -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath releases\SHA256SUMS.txt -Encoding ASCII
}
Write-Output 'Build completed.'
