[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'AstroHybrid.App\bin\Release'),
    [switch]$InIsolatedProcess
)

$ErrorActionPreference = 'Stop'
if (-not $InIsolatedProcess) {
    # Reflection-only assemblies cannot be unloaded. Isolate them so interactive rebuilds and releases never inherit file locks.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -AppDir $AppDir -InIsolatedProcess
    $auditExitCode = $LASTEXITCODE
    if ($auditExitCode -ne 0) {
        throw "Managed dependency audit failed with exit code $auditExitCode."
    }
    exit 0
}

$AppDir = (Resolve-Path -LiteralPath $AppDir).Path
$exe = Join-Path $AppDir 'AstroHybrid.exe'
$configPath = Join-Path $AppDir 'AstroHybrid.exe.config'
foreach ($requiredFile in $exe, $configPath) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Managed dependency audit requires $requiredFile"
    }
}

$files = @(Get-Item -LiteralPath $exe) + @(Get-ChildItem -LiteralPath $AppDir -Filter '*.dll' -File)
$identities = @{}
$assemblies = @()
foreach ($file in $files) {
    try {
        $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        # Byte loading leaves no file mapping behind, so a rebuild or repeated release can replace these DLLs.
        $assembly = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($file.FullName))
    }
    catch [BadImageFormatException] {
        if ($file.Extension -ieq '.exe') { throw }
        continue
    }
    if ($identities.ContainsKey($identity.Name)) {
        throw "Multiple bundled assemblies have the simple name $($identity.Name)"
    }
    $identities[$identity.Name] = $identity
    $assemblies += $assembly
}

if ($assemblies.Count -eq 0) { throw "No managed assemblies found in $AppDir" }

[xml]$config = Get-Content -LiteralPath $configPath
$namespaces = New-Object Xml.XmlNamespaceManager($config.NameTable)
$namespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$redirects = @{}
foreach ($node in $config.SelectNodes('//asm:dependentAssembly', $namespaces)) {
    $identityNode = $node.SelectSingleNode('asm:assemblyIdentity', $namespaces)
    $redirectNodes = @($node.SelectNodes('asm:bindingRedirect', $namespaces))
    if (($null -eq $identityNode) -or ($redirectNodes.Count -eq 0)) { continue }
    $name = $identityNode.GetAttribute('name')
    foreach ($redirectNode in $redirectNodes) {
        $versions = $redirectNode.GetAttribute('oldVersion').Split('-')
        $redirect = [PSCustomObject]@{
            Minimum = [version]$versions[0]
            Maximum = [version]$versions[-1]
            NewVersion = [version]$redirectNode.GetAttribute('newVersion')
            PublicKeyToken = $identityNode.GetAttribute('publicKeyToken')
        }
        if (-not $redirects.ContainsKey($name)) { $redirects[$name] = @() }
        $redirects[$name] += $redirect
    }
}

$checkedReferences = 0
$failures = @()
foreach ($assembly in $assemblies) {
    foreach ($reference in $assembly.GetReferencedAssemblies()) {
        if (-not $identities.ContainsKey($reference.Name)) { continue }
        $checkedReferences++
        $installed = $identities[$reference.Name]
        $referenceToken = (($reference.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join '')
        $installedToken = (($installed.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join '')
        $identityMatches = $referenceToken -ieq $installedToken
        $resolved = ($reference.Version -eq $installed.Version) -and $identityMatches
        if ($redirects.ContainsKey($reference.Name)) {
            foreach ($redirect in $redirects[$reference.Name]) {
                $redirectTokenMatches = (-not $redirect.PublicKeyToken) -or ($redirect.PublicKeyToken -ieq $referenceToken)
                $rangeMatches = $reference.Version -ge $redirect.Minimum -and $reference.Version -le $redirect.Maximum
                if ($redirectTokenMatches -and $rangeMatches) {
                    # An applicable redirect changes the version, but never the strong-name identity.
                    $resolved = ($redirect.NewVersion -eq $installed.Version) -and $identityMatches
                    if ($resolved) { break }
                }
            }
        }
        if (-not $resolved) {
            $referenceTokenDisplay = if ($referenceToken) { $referenceToken } else { '<none>' }
            $installedTokenDisplay = if ($installedToken) { $installedToken } else { '<none>' }
            $failures += "$($assembly.GetName().Name) requires $($reference.Name) $($reference.Version) " +
                "(public key token $referenceTokenDisplay), but $($installed.Version) " +
                "(public key token $installedTokenDisplay) is bundled and no matching binding redirect resolves it."
        }
    }
}

if ($failures.Count -gt 0) {
    $details = ($failures | Sort-Object -Unique | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "Managed dependency audit failed:$([Environment]::NewLine)$details$([Environment]::NewLine)" +
        'Rebuild the launcher with MSBuild /t:Restore,Rebuild /p:Configuration=Release.'
}

"Managed dependency audit passed: $($assemblies.Count) assemblies, $checkedReferences bundled references."
