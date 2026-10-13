$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
Push-Location $Root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Для проверки C# требуется установленный .NET SDK.'
    }
    dotnet build .\DocsApi.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Сборка основного проекта не прошла.' }
    dotnet run --project .\tests\ProductExplorer.Checks\ProductExplorer.Checks.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Проверки правил и SQL-фрагментов не прошли.' }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node --check .\wwwroot\reporter\product.js
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка синтаксиса product.js.' }
        node --test .\tests\product-model.test.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Проверки модели интерфейса не прошли.' }
    } else {
        Write-Warning 'Node.js не найден: проверки JavaScript пропущены, а не признаны успешными.'
    }
    Write-Host 'Проверки C# завершены. MSSQL, права пользователей и реальный HTTP отдельно проверить вручную.'
} finally { Pop-Location }
