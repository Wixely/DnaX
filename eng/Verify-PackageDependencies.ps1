param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = "artifacts/packages"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$allowed = @{
    "DnaX.Compatibility" = @()
    "DnaX.Diagnostics" = @()
    "DnaX.Data" = @(
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions"
    )
    "DnaX.Data.Migrations" = @(
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions"
    )
    "DnaX.Data.Migrations.Sqlite" = @(
        "DnaX.Data.Migrations",
        "Microsoft.Data.Sqlite"
    )
    "DnaX.Data.Migrations.Sqlite.Testing" = @(
        "DnaX.Data.Migrations",
        "DnaX.Data.Migrations.Sqlite",
        "Microsoft.Extensions.DependencyInjection"
    )
    "DnaX.Hosting" = @(
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.FileProviders.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Options"
    )
    "DnaX.Caching" = @(
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options"
    )
    "DnaX.Redis.StackExchangeRedis" = @(
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "StackExchange.Redis"
    )
}

$packages = Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg"
if ($packages.Count -ne $allowed.Count) {
    throw "Expected $($allowed.Count) packages but found $($packages.Count) in '$PackageDirectory'."
}

foreach ($package in $packages) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -like "*.nuspec" } |
            Select-Object -First 1

        if ($null -eq $entry) {
            throw "Package '$($package.Name)' has no nuspec."
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            $nuspec = [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $id = [string]$nuspec.package.metadata.id
        if (-not $allowed.ContainsKey($id)) {
            throw "Unexpected package '$id'."
        }

        $license = [string]$nuspec.package.metadata.license.InnerText
        if ($license -ne "MIT") {
            throw "Package '$id' does not declare the MIT license expression."
        }

        $actualDependencies = @(
            $nuspec.package.metadata.dependencies.group.dependency |
                ForEach-Object { [string]$_.id } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Sort-Object -Unique
        )
        $expectedDependencies = @($allowed[$id] | Sort-Object -Unique)

        $difference = Compare-Object $expectedDependencies $actualDependencies
        if ($null -ne $difference) {
            $rendered = $difference | Out-String
            throw "Package '$id' has an unexpected dependency set:`n$rendered"
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Output "Verified dependency isolation and MIT metadata for $($packages.Count) DNA X packages."
