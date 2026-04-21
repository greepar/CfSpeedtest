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

function Get-NssmUrl {
    return 'https://nssm.cc/release/nssm-2.24.zip'
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
$serviceName = 'CfSpeedtestClient'
$exePath = Join-Path $installDir 'CfSpeedtest.Client.exe'
$nssmDir = Join-Path $installDir 'nssm'
$nssmZipPath = Join-Path $env:TEMP 'nssm-2.24.zip'
$nssmStageDir = Join-Path $env:TEMP ("nssm-" + [guid]::NewGuid().ToString('N'))

Write-Log "平台: $platform"
Write-Log "下载地址: $downloadUrl"

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $nssmDir -Force | Out-Null

Write-Log '下载客户端...'
Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

Write-Log '解压客户端...'
Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
Copy-Item -Path (Join-Path $stageDir '*') -Destination $installDir -Recurse -Force

Write-Log '准备 NSSM...'
$nssmExe = Join-Path $nssmDir 'nssm.exe'
if (-not (Test-Path $nssmExe)) {
    Invoke-WebRequest -Uri (Get-NssmUrl) -OutFile $nssmZipPath
    New-Item -ItemType Directory -Path $nssmStageDir -Force | Out-Null
    Expand-Archive -Path $nssmZipPath -DestinationPath $nssmStageDir -Force
    $nssmSource = Join-Path $nssmStageDir 'nssm-2.24\win64\nssm.exe'
    if ($platform -eq 'win-x86') {
        $nssmSource = Join-Path $nssmStageDir 'nssm-2.24\win32\nssm.exe'
    }
    Copy-Item -Path $nssmSource -Destination $nssmExe -Force
}

$argList = "--server `"$ServerUrl`" --client-id $ClientId --isp $Isp --name `"$ClientName`" --service"

Write-Log '安装/更新 Windows Service...'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    Write-Log '检测到已存在的 Windows Service，准备覆盖更新...'
    & $nssmExe stop $serviceName | Out-Null
    & $nssmExe remove $serviceName confirm | Out-Null
}

& $nssmExe install $serviceName $exePath $argList | Out-Null
& $nssmExe set $serviceName AppDirectory $installDir | Out-Null
& $nssmExe set $serviceName DisplayName 'CfSpeedtest Client' | Out-Null
& $nssmExe set $serviceName Description 'CfSpeedtest native client service' | Out-Null
& $nssmExe set $serviceName Start SERVICE_AUTO_START | Out-Null
& $nssmExe set $serviceName AppExit Default Restart | Out-Null
& $nssmExe set $serviceName AppRestartDelay 5000 | Out-Null
& $nssmExe set $serviceName ObjectName LocalSystem | Out-Null
& $nssmExe start $serviceName | Out-Null

Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -Path $stageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $nssmZipPath -Force -ErrorAction SilentlyContinue
Remove-Item -Path $nssmStageDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Log '安装完成'
Write-Log "查看服务: sc query $serviceName"
Write-Log "查看 NSSM: $nssmExe"
