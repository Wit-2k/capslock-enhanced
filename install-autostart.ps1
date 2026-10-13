[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Uninstall,
    [string]$TaskName = 'CapsLock+ mini'
)

$ErrorActionPreference = 'Stop'
$recoveryTaskName = "$TaskName recovery"

# 两个任务都以当前交互用户最高权限运行；仅安装/删除时需要一次管理员确认。
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principalCheck = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator -and -not $WhatIfPreference) {
    throw '请在一次性管理员 PowerShell 中运行此脚本；登录时任务不会再次弹出 UAC。'
}

if ($Uninstall) {
    foreach ($name in @($TaskName, $recoveryTaskName)) {
        $existingTask = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($null -eq $existingTask) {
            continue
        }
        if ($PSCmdlet.ShouldProcess($name, '删除 CapsLock+ 登录/恢复任务')) {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            Write-Output "已删除计划任务：$name"
        }
    }
    return
}

$executablePath = Join-Path $PSScriptRoot 'capslock-enhanced.exe'
$recoveryScriptPath = Join-Path $PSScriptRoot 'ensure-autostart.ps1'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "找不到与安装脚本同目录的 capslock-enhanced.exe：$executablePath"
}
if (-not (Test-Path -LiteralPath $recoveryScriptPath -PathType Leaf)) {
    throw "找不到周期恢复脚本：$recoveryScriptPath"
}

$userName = $identity.Name
$principal = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartCount 255 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

$logonAction = New-ScheduledTaskAction -Execute $executablePath -Argument '--autostart' -WorkingDirectory $PSScriptRoot
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
$recoveryPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$recoveryArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$recoveryScriptPath`" -ExecutablePath `"$executablePath`""
$recoveryAction = New-ScheduledTaskAction -Execute $recoveryPowerShell -Argument $recoveryArguments -WorkingDirectory $PSScriptRoot
$recoveryLogonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
$recoveryRepeatTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1) `
    -RepetitionDuration (New-TimeSpan -Days 3650)

if ($PSCmdlet.ShouldProcess($TaskName, "注册当前用户最高权限登录任务，程序=$executablePath")) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $logonAction `
        -Trigger $logonTrigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Write-Output "已注册登录任务：$TaskName"
}

if ($PSCmdlet.ShouldProcess($recoveryTaskName, "注册每分钟检查一次的 watchdog recovery 任务")) {
    Register-ScheduledTask `
        -TaskName $recoveryTaskName `
        -Action $recoveryAction `
        -Trigger @($recoveryLogonTrigger, $recoveryRepeatTrigger) `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Write-Output "已注册周期恢复任务：$recoveryTaskName"
}
