param([switch]$SandboxSafe, [switch]$LiveProcessCheck)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path "$PSScriptRoot\test-output" | Out-Null
& $compiler /nologo /target:exe /main:Tests /out:"$PSScriptRoot\test-output\Tests.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll "$PSScriptRoot\ClaudeSelector.cs" "$PSScriptRoot\DesktopLogin.cs" "$PSScriptRoot\LoginBrowser.cs" "$PSScriptRoot\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
Push-Location $PSScriptRoot
try {
    $testArguments = @()
    if ($SandboxSafe) { $testArguments += '--sandbox-safe' }
    if ($LiveProcessCheck) { $testArguments += '--live-process-check' }
    & "$PSScriptRoot\test-output\Tests.exe" @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
