# Renders the chart HTML files in a folder to PNG with headless Chrome.
# Usage: powershell -File render.ps1 -Dir <folder with *.html>
param([Parameter(Mandatory = $true)][string]$Dir)
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
if (-not (Test-Path $chrome)) { throw "Chrome not found at $chrome" }
Get-ChildItem -Path $Dir -Filter *.html | ForEach-Object {
    $png = [IO.Path]::ChangeExtension($_.FullName, '.png')
    & $chrome --headless=new --disable-gpu --hide-scrollbars --window-size=1200,620 "--screenshot=$png" $_.FullName 2>$null | Out-Null
    Write-Output $png
}
