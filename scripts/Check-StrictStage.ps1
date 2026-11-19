$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
Push-Location $Root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Нужен .NET SDK 8 или совместимый SDK. Отсутствие SDK не означает успешную проверку.'
    }
    dotnet build .\DocsApi.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Основной проект не собран.' }
    dotnet run --project .\tests\StrictAccess.Checks\StrictAccess.Checks.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Проверки строгих решений не прошли.' }
    dotnet run --project .\tests\ProductExplorer.Checks\ProductExplorer.Checks.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Проверки изделия не прошли.' }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node --check .\wwwroot\reporter\product.js
        if ($LASTEXITCODE -ne 0) { throw 'Синтаксис product.js.' }
        node --test .\tests\product-model.test.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Проверки модели интерфейса.' }
    } else { Write-Warning 'Проверки JS пропущены: Node.js отсутствует.' }
    Write-Host 'Сборка и чистые проверки завершены. WinAuth, IIS, SQL, отказы и журнал проверить отдельно.'
} finally { Pop-Location }
