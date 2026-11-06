param()
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'dist\FacadeJoin2024.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Сначала соберите DLL.' }
$destination = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$escaped = [System.Security.SecurityElement]::Escape($dll)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Name>Соединить фасад — обобщённые модели</Name>
    <Assembly>$escaped</Assembly>
    <AddInId>47AE72A8-4CF4-49D7-9202-FCB6765B6B08</AddInId>
    <FullClassName>FacadeJoin2024.JoinCommand</FullClassName>
    <Text>Соединить фасад</Text>
    <VendorId>A101</VendorId>
    <VendorDescription>Facade geometry join for Revit 2024</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
$path = Join-Path $destination 'A101.FacadeJoin2024.addin'
Set-Content -LiteralPath $path -Value $manifest -Encoding UTF8
Write-Host "Установлено: $path"
Write-Host 'Перезапустите Revit 2024. Команда: Надстройки → Внешние инструменты → Соединить фасад.'
