[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'capslock-enhanced.exe')
)

$ErrorActionPreference = 'Stop'
$disabledMarker = Join-Path $env:LOCALAPPDATA 'CapsLockPlus\autostart.disabled'
if (Test-Path -LiteralPath $disabledMarker -PathType Leaf) {
    return
}

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "找不到 capslock-enhanced.exe：$ExecutablePath"
}

# 周期任务只补启动缺失的 watchdog；正常运行时不创建额外进程。
$processName = [IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
    return
}

if ($PSCmdlet.ShouldProcess($ExecutablePath, '恢复缺失的 CapsLock+ watchdog')) {
    Start-Process -FilePath $ExecutablePath -ArgumentList '--recover' -WorkingDirectory (Split-Path -Parent $ExecutablePath)
}
