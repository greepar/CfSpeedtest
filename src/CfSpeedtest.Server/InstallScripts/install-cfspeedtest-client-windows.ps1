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

function Test-IsAdministrator {
    return ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-Argument([string]$Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Restart-Elevated {
    $argList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-ServerUrl', $ServerUrl,
        '-ClientId', $ClientId,
        '-Isp', $Isp,
        '-ClientName', $ClientName,
        '-Repository', $Repository,
        '-ReleaseTag', $ReleaseTag
    )

    if (-not [string]::IsNullOrWhiteSpace($GhProxyPrefix)) {
        $argList += @('-GhProxyPrefix', $GhProxyPrefix)
    }

    $argumentString = ($argList | ForEach-Object { Quote-Argument $_ }) -join ' '
    Write-Log 'Administrator rights are required. Requesting UAC elevation to install/update the NSSM service...'
    $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentString -Verb RunAs -Wait -PassThru
    exit $proc.ExitCode
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

if (-not (Test-IsAdministrator)) {
    Restart-Elevated
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

Write-Log "Platform: $platform"
Write-Log "Download URL: $downloadUrl"

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $nssmDir -Force | Out-Null

Write-Log 'Downloading client package...'
Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

Write-Log 'Extracting client package...'
Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
Copy-Item -Path (Join-Path $stageDir '*') -Destination $installDir -Recurse -Force

Write-Log 'Preparing NSSM...'
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

Write-Log 'Installing/updating Windows Service...'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    Write-Log 'Existing Windows Service detected. Replacing it...'
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

Write-Log 'Install completed.'
Write-Log "Check service: sc query $serviceName"
Write-Log "Check NSSM: $nssmExe"
