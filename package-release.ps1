$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionPath = Join-Path $projectRoot 'VERSION'
$buildScript = Join-Path $projectRoot 'build.ps1'
$executable = Join-Path $projectRoot 'dist\CodexUsageWidget.exe'
$license = Join-Path $projectRoot 'LICENSE'
$readmeTemplate = Join-Path $projectRoot 'packaging\README.txt'
$releaseDirectory = Join-Path $projectRoot 'release'
$stagingDirectory = Join-Path $releaseDirectory '.staging'

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'VERSION must contain a semantic version such as 1.0.0.'
}

if (Get-Process -Name 'CodexUsageWidget' -ErrorAction SilentlyContinue) {
    throw 'Exit Codex Usage Widget before creating a release package.'
}

& $buildScript
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable)) {
    throw 'The release build did not complete successfully.'
}

New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null

$releaseRoot = [IO.Path]::GetFullPath($releaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedStaging = [IO.Path]::GetFullPath($stagingDirectory)
if (-not $resolvedStaging.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The staging directory resolved outside the release directory.'
}
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    Copy-Item -LiteralPath $executable -Destination (Join-Path $stagingDirectory 'CodexUsageWidget.exe')
    Copy-Item -LiteralPath $license -Destination (Join-Path $stagingDirectory 'LICENSE')

    $releaseReadme = (Get-Content -LiteralPath $readmeTemplate -Raw).Replace('{VERSION}', $version)
    Set-Content -LiteralPath (Join-Path $stagingDirectory 'README.txt') -Value $releaseReadme -Encoding UTF8

    $testReportName = 'release-self-test.txt'
    $testProcess = Start-Process -FilePath (Join-Path $stagingDirectory 'CodexUsageWidget.exe') `
        -ArgumentList "--self-test $testReportName" `
        -WorkingDirectory $stagingDirectory `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($testProcess.ExitCode -ne 0) {
        throw "The packaged executable failed its self-test with exit code $($testProcess.ExitCode)."
    }
    $testReport = Join-Path $stagingDirectory $testReportName
    if (-not (Test-Path -LiteralPath $testReport) -or (Get-Content -LiteralPath $testReport -Raw) -notmatch '(?m)^PASS\s*$') {
        throw 'The packaged executable did not produce a passing self-test report.'
    }
    Remove-Item -LiteralPath $testReport -Force

    $archiveName = "CodexUsageWidget-v$version-windows.zip"
    $archivePath = Join-Path $releaseDirectory $archiveName
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal

    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    $checksumPath = $archivePath + '.sha256.txt'
    ($hash.Hash.ToLowerInvariant() + '  ' + $archiveName) | Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Output "Created $archivePath"
    Write-Output "Created $checksumPath"
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
