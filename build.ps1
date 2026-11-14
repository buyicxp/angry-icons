$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$sxaml = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'

$a = @(
  '/nologo', '/target:winexe', '/platform:anycpu', '/codepage:65001',
  "/out:$root\DesktopAngry.exe",
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
  "/r:$wpf\WindowsBase.dll",
  "/r:$wpf\PresentationCore.dll",
  "/r:$wpf\PresentationFramework.dll",
  "/r:$sxaml",
  "/r:$root\Box2DX.dll",
  "$root\DesktopAngry.cs",
  "$root\Phys.cs"
)

if (Test-Path -LiteralPath "$root\app.ico") { $a += "/win32icon:$root\app.ico" }

& $csc $a
if ($LASTEXITCODE -ne 0) { throw "compile failed, exit=$LASTEXITCODE" }
Write-Host "BUILD OK -> $root\DesktopAngry.exe"