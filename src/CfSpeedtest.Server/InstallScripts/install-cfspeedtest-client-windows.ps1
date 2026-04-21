param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [string]$Isp = 'Telecom',
    [string]$ClientName = '',
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$ReleaseTag,
    [string]$GhProxyPrefix = ''
)

$ErrorActionPreference = 'Stop'

function Write-Log([string]$Message) {
    Write-Host "[CfSpeedtest] $Message"
}

function Get-Platform {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { return 'win-arm64' }
        'X86' { return 'win-x86' }
        default { return 'win-x64' }
    }
}

function Get-DownloadUrl([string]$AssetName) {
    $rawUrl = "https://github.com/$Repository/releases/download/$ReleaseTag/$AssetName"
    if ([string]::IsNullOrWhiteSpace($GhProxyPrefix)) {
        return $rawUrl
    }
    return "$($GhProxyPrefix.TrimEnd('/'))/$rawUrl"
}

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行此 PowerShell 脚本'
}

if ([string]::IsNullOrWhiteSpace($ClientName)) {
    $ClientName = "$Isp-$($ClientId.Substring(0, [Math]::Min(8, $ClientId.Length)))"
}

$platform = Get-Platform
$assetName = "cfspeedtest-client-$platform.zip"
$downloadUrl = Get-DownloadUrl -AssetName $assetName
$zipPath = Join-Path $env:TEMP $assetName
$stageDir = Join-Path $env:TEMP ("cfspeedtest-client-" + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $env:ProgramFiles 'CfSpeedtestClient'
$taskName = 'CfSpeedtestClient'
$exePath = Join-Path $installDir 'CfSpeedtest.Client.exe'

Write-Log "平台: $platform"
Write-Log "下载地址: $downloadUrl"

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Write-Log '下载客户端...'
Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

Write-Log '解压客户端...'
Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
Copy-Item -Path (Join-Path $stageDir '*') -Destination $installDir -Recurse -Force

$argList = "--server `"$ServerUrl`" --client-id $ClientId --isp $Isp --name `"$ClientName`" --service"
$action = New-ScheduledTaskAction -Execute $exePath -Argument $argList
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -Path $stageDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Log '安装完成'
Write-Log "查看任务: Get-ScheduledTask -TaskName $taskName"
Write-Log "立即启动: Start-ScheduledTask -TaskName $taskName"
