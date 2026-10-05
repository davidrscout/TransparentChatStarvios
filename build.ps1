# Genera dist/TransparentChatStarvios.exe (un solo archivo, necesita .NET 8 Desktop Runtime).
$ErrorActionPreference = 'Stop'
dotnet publish src/TransparentChatStarvios -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
Write-Host "Listo: dist/TransparentChatStarvios.exe"
