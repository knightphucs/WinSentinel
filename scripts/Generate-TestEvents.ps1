#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Sinh lan luot cac event ma TaskServiceMonitor dang theo doi, de kiem tra parser.

.DESCRIPTION
    Chay script nay o MOT cua so PowerShell Administrator, trong khi app dang chay
    o mot cua so khac. Moi hanh dong deu in nhan truoc khi thuc hien de doi chieu
    voi dong log ma app in ra.

    Script tu don dep task/service test o cuoi, ke ca khi bi loi giua chung.

.EXAMPLE
    .\scripts\Generate-TestEvents.ps1
    .\scripts\Generate-TestEvents.ps1 -DelaySeconds 5
#>
[CmdletBinding()]
param(
    # Nghi giua cac buoc de kip nhin log ben cua so app.
    [int]$DelaySeconds = 3,

    [string]$TaskName = 'WinSentinelTest',
    [string]$ServiceName = 'WinSentinelSvc'
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([int]$EventId, [string]$Message)
    Write-Host ''
    Write-Host ("=" * 70) -ForegroundColor DarkGray
    Write-Host (" Ky vong Event {0}: {1}" -f $EventId, $Message) -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor DarkGray
}

function Test-AuditSubcategory {
    param([string]$Name, [string]$WhichEvents)

    $line = auditpol /get /subcategory:"$Name" 2>$null | Where-Object { $_ -match $Name }
    if ($line -match 'Success') {
        Write-Host ("  [OK]  Audit '{0}' da bat -> {1} se sinh ra." -f $Name, $WhichEvents) -ForegroundColor Green
        return $true
    }

    Write-Host ("  [!!]  Audit '{0}' CHUA bat -> {1} se KHONG sinh ra." -f $Name, $WhichEvents) -ForegroundColor Yellow
    Write-Host ('        Bat bang: auditpol /set /subcategory:"{0}" /success:enable /failure:enable' -f $Name) -ForegroundColor Yellow
    return $false
}

Write-Host 'Kiem tra Audit Policy truoc khi bat dau' -ForegroundColor White
$null = Test-AuditSubcategory -Name 'Other Object Access Events' -WhichEvents '4698-4702 (scheduled task)'
$null = Test-AuditSubcategory -Name 'Security System Extension' -WhichEvents '4697 (service installed)'

Write-Host ''
Write-Host 'Bat dau sinh event. Nhin sang cua so dang chay app de doi chieu.' -ForegroundColor White

try {
    # ---------------------------------------------------------------- Scheduled Task
    Write-Step -EventId 4698 -Message 'Task created (action = Exec)'
    schtasks /create /tn $TaskName /tr 'cmd.exe /c echo hello' /sc once /st 23:59 /f | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Step -EventId 4702 -Message 'Task updated - doc field TaskContentNew, KHONG phai TaskContent'
    schtasks /change /tn $TaskName /tr 'cmd.exe /c echo da-sua' | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Step -EventId 4701 -Message 'Task disabled'
    schtasks /change /tn $TaskName /disable | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Step -EventId 4700 -Message 'Task enabled'
    schtasks /change /tn $TaskName /enable | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Step -EventId 4699 -Message 'Task deleted - TaskContent RONG, parser phai tra null'
    schtasks /delete /tn $TaskName /f | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    # ---------------------------------------------------------------- Service
    Write-Step -EventId 4697 -Message 'va 7045: CUNG mot lan cai service, hai event khac dinh dang'
    Write-Host '  4697 (Security): ServiceStartType=3, ServiceType=0x10  -> ma so' -ForegroundColor DarkGray
    Write-Host '  7045 (System)  : StartType="demand start"              -> chu' -ForegroundColor DarkGray
    Write-Host '  Parser phai chuan hoa ca hai ve cung mot dang.' -ForegroundColor DarkGray
    sc.exe create $ServiceName binPath= 'C:\Windows\System32\snmptrap.exe' start= demand | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Step -EventId 7040 -Message 'Start type changed - param2 (cu) -> param3 (moi)'
    sc.exe config $ServiceName start= auto | Out-Null
    Start-Sleep -Seconds $DelaySeconds

    Write-Host ''
    Write-Host 'Da sinh xong toan bo event.' -ForegroundColor Green
}
finally {
    # Don dep du co loi giua chung, tranh de lai rac tren may.
    Write-Host ''
    Write-Host 'Don dep task/service test...' -ForegroundColor White

    schtasks /delete /tn $TaskName /f 2>$null | Out-Null
    sc.exe delete $ServiceName 2>$null | Out-Null

    Write-Host 'Xong.' -ForegroundColor Green
}
