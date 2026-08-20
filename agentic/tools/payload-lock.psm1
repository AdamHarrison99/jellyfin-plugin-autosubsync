<#
.SYNOPSIS
    Shared helpers for reading and writing agentic/payload.lock.json.
#>

function Get-RepoRoot {
    return Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

function Get-LockPath {
    return Join-Path (Get-RepoRoot) 'agentic\payload.lock.json'
}

function Get-PayloadRoot {
    return Join-Path (Get-RepoRoot) 'agentic\payload'
}

function Read-PayloadLock {
    $path = Get-LockPath
    if (-not (Test-Path $path)) {
        throw "Lock file not found: $path"
    }
    return Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

# ! 5.1 escapes apostrophes as ' and 7 does not.
function ConvertTo-JsonString {
    param([string]$Value)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')

    foreach ($ch in $Value.ToCharArray()) {
        switch ($ch) {
            '"' { [void]$sb.Append('\"'); continue }
            '\' { [void]$sb.Append('\\'); continue }
            "`b" { [void]$sb.Append('\b'); continue }
            "`f" { [void]$sb.Append('\f'); continue }
            "`n" { [void]$sb.Append('\n'); continue }
            "`r" { [void]$sb.Append('\r'); continue }
            "`t" { [void]$sb.Append('\t'); continue }
            default {
                if ([int]$ch -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
                else { [void]$sb.Append($ch) }
            }
        }
    }

    [void]$sb.Append('"')
    return $sb.ToString()
}

# ! ConvertTo-Json indents differently in 5.1 and 7, and the lock is written by both. Hand-rolled
#   so the file a Windows build produces is byte-identical to the one a Linux build produces.
function ConvertTo-StableJson {
    param($Value, [int]$Depth = 0)

    $pad = '  ' * $Depth
    $inner = '  ' * ($Depth + 1)

    if ($null -eq $Value) { return 'null' }

    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }

    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return [string]::Format([cultureinfo]::InvariantCulture, '{0}', $Value)
    }

    if ($Value -is [string]) {
        return ConvertTo-JsonString -Value $Value
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $names = @($Value.Keys)
        $pairs = foreach ($name in $names) {
            $inner + (ConvertTo-JsonString -Value ([string]$name)) + ': ' +
            (ConvertTo-StableJson -Value $Value[$name] -Depth ($Depth + 1))
        }
        if ($pairs.Count -eq 0) { return '{}' }
        return "{`n" + ($pairs -join ",`n") + "`n$pad}"
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $pairs = foreach ($property in $Value.PSObject.Properties) {
            $inner + (ConvertTo-JsonString -Value $property.Name) + ': ' +
            (ConvertTo-StableJson -Value $property.Value -Depth ($Depth + 1))
        }
        if ($pairs.Count -eq 0) { return '{}' }
        return "{`n" + ($pairs -join ",`n") + "`n$pad}"
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $items = foreach ($item in $Value) {
            $inner + (ConvertTo-StableJson -Value $item -Depth ($Depth + 1))
        }
        if ($items.Count -eq 0) { return '[]' }
        return "[`n" + ($items -join ",`n") + "`n$pad]"
    }

    return ConvertTo-JsonString -Value ([string]$Value)
}

# ! Set-Content -Encoding UTF8 writes a BOM on 5.1 and none on 7.
function Write-TextFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)

    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false))
}

function Write-PayloadLock {
    param([Parameter(Mandatory)] $Lock)

    Write-TextFile -Path (Get-LockPath) -Text ((ConvertTo-StableJson -Value $Lock) + "`n")
}

function Get-LockTool {
    param(
        [Parameter(Mandatory)] $Lock,
        [Parameter(Mandatory)][string]$Name
    )

    $tool = $Lock.tools.PSObject.Properties | Where-Object { $_.Name -eq $Name }
    if (-not $tool) {
        throw "No tool '$Name' in the lock file."
    }
    return $tool.Value
}

<#
.SYNOPSIS
    Stable hash over a directory tree: per-file SHA256 keyed by relative path.
#>
function Get-TreeHash {
    param([Parameter(Mandatory)][string]$Path)

    $full = (Resolve-Path $Path).Path
    $byRelative = @{}
    foreach ($file in (Get-ChildItem -Path $full -Recurse -File)) {
        $rel = $file.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/')
        $byRelative[$rel] = $file.FullName
    }

    # ! Ordinal, not the culture-aware default, and an explicit LF below. Both platforms build
    #   this tool and the two hashes have to come out the same.
    $names = [string[]]@($byRelative.Keys)
    [array]::Sort($names, [System.StringComparer]::Ordinal)

    $builder = New-Object System.Text.StringBuilder
    foreach ($rel in $names) {
        $hash = (Get-FileHash -Path $byRelative[$rel] -Algorithm SHA256).Hash.ToLower()
        [void]$builder.Append("$rel $hash`n")
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower()
    }
    finally {
        $sha.Dispose()
    }
}

<#
.SYNOPSIS
    Runtime identifier for the current machine, matching PlatformRid.Current.
#>
function Get-CurrentRid {
    if ([System.Environment]::Is64BitOperatingSystem) {
        $arch = 'x64'
    }
    else {
        throw 'Only 64-bit build hosts are supported.'
    }

    $procArch = $env:PROCESSOR_ARCHITECTURE
    if ($procArch -eq 'ARM64') {
        $arch = 'arm64'
    }

    if ($IsLinux) { return "linux-$arch" }
    if ($IsMacOS) { return "osx-$arch" }
    return "win-$arch"
}

function Get-ManifestPath {
    return Join-Path (Get-RepoRoot) 'Cli\PayloadManifest.g.cs'
}

function Get-DistRoot {
    return Join-Path (Get-RepoRoot) 'agentic\dist'
}

<#
.SYNOPSIS
    Expands the {version}, {upstream}, {tag} and {rid} placeholders in a release naming template.
.DESCRIPTION
    {version} is the payload's own revision and {upstream} is the version of the tool it freezes.
    They are different numbers, and an archive is named after what is inside it.
#>
function Expand-AssetTemplate {
    param(
        [Parameter(Mandatory)][string]$Template,
        [Parameter(Mandatory)][string]$Version,
        [string]$Upstream = '',
        [string]$Tag = '',
        [string]$Rid = ''
    )

    return $Template.Replace('{version}', $Version).Replace('{upstream}', $Upstream).Replace('{tag}', $Tag).Replace('{rid}', $Rid)
}

<#
.SYNOPSIS
    The download base URL for one tool's release assets.
#>
function Get-ToolBaseUrl {
    param([Parameter(Mandatory)] $Tool)

    $tag = Expand-AssetTemplate -Template $Tool.release.assetTag `
        -Version $Tool.version -Tag $Tool.upstream.tag
    return "https://github.com/$($Tool.release.assetRepo)/releases/download/$tag"
}

<#
.SYNOPSIS
    The rid-keyed asset table for one tool, whatever shape the lock stores it in.
.DESCRIPTION
    A built tool records assets under 'payloads' alongside its tree hash; a pinned tool records
    only what upstream published, under 'assets'. Both reduce to name, sha256, size and format.
#>
function Get-ToolAssets {
    param([Parameter(Mandatory)] $Tool)

    $result = [ordered]@{}

    if ($Tool.acquisition -eq 'built') {
        foreach ($property in $Tool.payloads.PSObject.Properties) {
            if (-not $property.Value.archiveSha256) { continue }
            $result[$property.Name] = [ordered]@{
                name   = $property.Value.archiveName
                sha256 = $property.Value.archiveSha256
                size   = $property.Value.archiveSize
                format = 'zip'
            }
        }

        return $result
    }

    foreach ($property in $Tool.assets.PSObject.Properties) {
        if (-not $property.Value.sha256) { continue }
        $result[$property.Name] = [ordered]@{
            name   = $property.Value.name
            sha256 = $property.Value.sha256
            size   = $property.Value.size
            format = $property.Value.format
        }
    }

    return $result
}

<#
.SYNOPSIS
    Renders Cli/PayloadManifest.g.cs from the lock file. Returns the source text.
.DESCRIPTION
    The hashes this emits are the plugin's only trust root for a fetched payload, so it is
    generated from the lock rather than maintained by hand. check-payload.ps1 re-renders it and
    fails on any difference, which is what keeps the assembly and the lock from drifting apart.
#>
function New-PayloadManifestSource {
    param([Parameter(Mandatory)] $Lock)

    # ! Round-trip first. On a hashtable, PSObject.Properties also yields Keys/Values/Count,
    #   and every asset field would render as an array.
    $Lock = $Lock | ConvertTo-Json -Depth 12 | ConvertFrom-Json

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('// <auto-generated />')
    [void]$lines.Add('// Rendered from the payload lock by the repository build tooling. Do not edit.')
    [void]$lines.Add('')
    [void]$lines.Add('#nullable enable')
    [void]$lines.Add('')
    [void]$lines.Add('namespace Jellyfin.Plugin.AutoSubSync.Cli;')
    [void]$lines.Add('')
    [void]$lines.Add('public enum PayloadArchiveFormat')
    [void]$lines.Add('{')
    [void]$lines.Add('    Zip,')
    [void]$lines.Add('    TarGz')
    [void]$lines.Add('}')
    [void]$lines.Add('')
    [void]$lines.Add('// The tool builds this assembly is pinned to, and the hashes that prove them.')
    [void]$lines.Add('public static class PayloadManifest')
    [void]$lines.Add('{')

    $fields = New-Object System.Collections.Generic.List[string]

    foreach ($property in $Lock.tools.PSObject.Properties) {
        $tool = $property.Value
        $field = ToolFieldName -Name $property.Name
        [void]$fields.Add($field)

        [void]$lines.Add("    public static readonly PayloadTool $field = new(")
        [void]$lines.Add("        `"$($property.Name)`",")
        [void]$lines.Add("        `"$($tool.binaryName)`",")
        [void]$lines.Add("        `"$($tool.version)`",")
        $toolVersion = if ($tool.upstream.PSObject.Properties['version']) { $tool.upstream.version } else { $tool.version }
        [void]$lines.Add("        `"$toolVersion`",")
        [void]$lines.Add('        // ! Compiled in. A configurable download host is arbitrary code execution.')
        [void]$lines.Add("        `"$(Get-ToolBaseUrl -Tool $tool)`",")
        [void]$lines.Add('        [')

        $assets = Get-ToolAssets -Tool $tool
        $entries = New-Object System.Collections.Generic.List[string]
        foreach ($rid in $assets.Keys) {
            $asset = $assets[$rid]
            $format = if ($asset.format -eq 'tar.gz') { 'PayloadArchiveFormat.TarGz' } else { 'PayloadArchiveFormat.Zip' }
            [void]$entries.Add(
                "            new(`"$rid`", `"$($asset.name)`", `"$($asset.sha256)`", " +
                "$($asset.size)L, $format)")
        }

        for ($i = 0; $i -lt $entries.Count; $i++) {
            $suffix = if ($i -eq $entries.Count - 1) { '' } else { ',' }
            [void]$lines.Add($entries[$i] + $suffix)
        }

        [void]$lines.Add('        ]);')
        [void]$lines.Add('')
    }

    [void]$lines.Add("    public static readonly IReadOnlyList<PayloadTool> All = [$($fields -join ', ')];")
    [void]$lines.Add('}')
    [void]$lines.Add('')
    [void]$lines.Add('public record PayloadAsset(')
    [void]$lines.Add('    string Rid, string FileName, string Sha256, long Size, PayloadArchiveFormat Format);')
    [void]$lines.Add('')
    [void]$lines.Add('public record PayloadTool(')
    [void]$lines.Add('    string Name,')
    [void]$lines.Add('    string BinaryName,')
    [void]$lines.Add('    string Version,')
    # ! ASCII only. PS 5.1 reads this BOM-less file as ANSI and mangles anything above U+007F.
    [void]$lines.Add('    // The bundled tool''s own version. Shown to the user, never used as the cache key.')
    [void]$lines.Add('    string ToolVersion,')
    [void]$lines.Add('    string BaseUrl,')
    [void]$lines.Add('    IReadOnlyList<PayloadAsset> Assets)')
    [void]$lines.Add('{')
    [void]$lines.Add('    public string ExecutableName')
    [void]$lines.Add('        => OperatingSystem.IsWindows() ? BinaryName + ".exe" : BinaryName;')
    [void]$lines.Add('')
    [void]$lines.Add('    public PayloadAsset? For(string? rid)')
    [void]$lines.Add('        => Assets.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.Ordinal));')
    [void]$lines.Add('')
    [void]$lines.Add('    public string UrlFor(PayloadAsset asset) => $"{BaseUrl}/{asset.FileName}";')
    [void]$lines.Add('}')

    return ($lines -join "`r`n") + "`r`n"
}

function ToolFieldName {
    param([Parameter(Mandatory)][string]$Name)

    $parts = $Name -split '[-_.]'
    return (($parts | ForEach-Object {
                if ($_.Length -eq 0) { '' } else { $_.Substring(0, 1).ToUpper() + $_.Substring(1) }
            }) -join '')
}

function Write-PayloadManifest {
    param([Parameter(Mandatory)] $Lock)

    $source = New-PayloadManifestSource -Lock $Lock
    Write-TextFile -Path (Get-ManifestPath) -Text $source
}

Export-ModuleMember -Function Get-RepoRoot, Get-LockPath, Get-PayloadRoot, Read-PayloadLock,
Write-PayloadLock, Get-LockTool, Get-TreeHash, Get-CurrentRid, Get-ManifestPath, Get-DistRoot,
Expand-AssetTemplate, Get-ToolBaseUrl, Get-ToolAssets, New-PayloadManifestSource, Write-PayloadManifest,
ConvertTo-StableJson, Write-TextFile
