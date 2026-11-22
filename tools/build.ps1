param(
    [string]$DotNet = 'dotnet',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\GameDesk\eFootballNet.Windows.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$temp = Join-Path ([IO.Path]::GetTempPath()) ('gamedesk-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

$packages = @(
    @{ Name = "eFootballNet-$version-win-x64.zip"; Mode = 'standalone'; SelfContained = 'true' },
    @{ Name = "eFootballNet-$version-win-x64-slim.zip"; Mode = 'slim'; SelfContained = 'false' }
)

function Copy-RuntimeNotices([string]$PublishPath) {
    $assetsPath = Join-Path (Split-Path $project) 'obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $deps = Get-Content -LiteralPath (Join-Path $PublishPath 'eFootballNet.deps.json') -Raw | ConvertFrom-Json
    $runtimePacks = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.*' })
    if ($runtimePacks.Count -eq 0) { throw 'The standalone package did not declare its runtime packs.' }
    foreach ($pack in $runtimePacks) {
        $id, $packVersion = $pack.Substring('runtimepack.'.Length).Split('/')
        $packagePath = Join-Path $assets.project.restore.packagesPath (Join-Path $id.ToLowerInvariant() $packVersion)
        $files = @(Get-ChildItem -LiteralPath $packagePath -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$' })
        if (!($files | Where-Object { $_.Name -match '^LICENSE' })) {
            throw "Missing upstream license for $id $packVersion."
        }
        foreach ($file in $files) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $PublishPath "licenses\$id-$($file.Name)")
        }
    }
}

try {
    foreach ($package in $packages) {
        $publish = Join-Path $temp $package.Mode
        & $DotNet publish $project -c Release -r win-x64 --self-contained $package.SelfContained -o $publish --nologo -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($($package.Mode))." }

        foreach ($document in 'LICENSE','README.md','THIRD-PARTY-NOTICES.md','CHANGELOG.md','SECURITY.md') {
            Copy-Item -LiteralPath (Join-Path $root $document) -Destination $publish
        }
        Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $publish -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $publish -Recurse
        if ($package.Mode -eq 'standalone') {
            if (!(Test-Path (Join-Path $publish 'coreclr.dll'))) { throw 'The standalone publish is missing its runtime.' }
            Copy-RuntimeNotices $publish
        }
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $output $package.Name) -Force
    }
    @($packages | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath (Join-Path $output $_.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding Ascii
    Get-ChildItem -LiteralPath $output -File | Select-Object Name,Length
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
