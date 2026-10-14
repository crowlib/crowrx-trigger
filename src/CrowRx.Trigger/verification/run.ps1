param(
    [ValidateSet('Tests', 'Benchmarks', 'LoopComparison', 'Construction', 'Creation', 'Enumeration', 'Capacity', 'All')]
    [string]$Mode = 'Tests',
    [switch]$Logging,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$verificationRoot = $PSScriptRoot

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Build-Project {
    param([string]$Project, [switch]$Rebuild, [switch]$WithLogging)
    $buildArguments = @(
        'build', (Join-Path $verificationRoot $Project),
        '--configuration', 'Release', '--nologo', '--verbosity', 'minimal',
        '-m:1', '-p:GeneratePackageOnBuild=false', '-p:UseSharedCompilation=false'
    )
    if ($Rebuild) { $buildArguments += '-t:Rebuild' }
    if ($WithLogging) { $buildArguments += '-p:DefineConstants=CROWRX_LOG_EXCEPTION' }
    if ($Offline) {
        $buildArguments += '--ignore-failed-sources', '-p:NuGetAudit=false'
    }
    Invoke-DotNet -Arguments $buildArguments
}

function Run-Suite {
    param([string]$TestProject, [string]$TestAssembly)
    $testAssemblyPath = Join-Path $verificationRoot $TestAssembly
    Write-Host "Running standard lifetime checks for $TestProject."
    Build-Project -Project $TestProject
    Invoke-DotNet -Arguments @($testAssemblyPath)
    if ($Logging) {
        try {
            Write-Host 'Running exception logging checks.'
            Build-Project -Project $TestProject -Rebuild -WithLogging
            Invoke-DotNet -Arguments @($testAssemblyPath, '--logging')
        }
        finally {
            Write-Host 'Restoring the standard library and test build.'
            Build-Project -Project $TestProject -Rebuild
        }
    }
}

function Run-Tests {
    Run-Suite -TestProject 'Lifetime/Lifetime.csproj' -TestAssembly 'Lifetime/bin/Release/net8.0/Lifetime.dll'
    Run-Suite -TestProject 'Creation/Creation.csproj' -TestAssembly 'Creation/bin/Release/net8.0/Creation.dll'
    Invoke-DotNet -Arguments @((Join-Path $verificationRoot 'Creation/bin/Release/net8.0/Creation.dll'), '--capacity-tests')
}

function Save-Benchmark {
    param([string]$Assembly, [string]$FileName, [string[]]$RunArguments = @())
    $assemblyPath = Join-Path $verificationRoot $Assembly
    $jsonLines = @(& dotnet $assemblyPath @RunArguments)
    if ($LASTEXITCODE -ne 0) { throw "Benchmark $FileName failed with exit code $LASTEXITCODE." }
    $json = $jsonLines -join [Environment]::NewLine
    $result = $json | ConvertFrom-Json
    $controls = @($result.Results | Where-Object { $_.Name -eq 'control_no_op' })
    $positiveControls = @($result.Results | Where-Object { $_.Name -like 'control_allocate_64*' })
    if ($controls.Count -eq 0 -or $positiveControls.Count -eq 0) {
        throw "Benchmark $FileName did not emit allocation controls."
    }
    foreach ($control in $controls) {
        if ($control.BytesPerOp -ne 0) { throw "Benchmark $FileName failed its zero allocation control." }
    }
    foreach ($control in $positiveControls) {
        if ($control.BytesPerOp -le 0) { throw "Benchmark $FileName failed its positive allocation control." }
    }
    if ($result.PSObject.Properties.Name -contains 'ExternalPosts' -and $result.ExternalPosts -ne 0) {
        throw "Benchmark $FileName observed work outside its measurement thread."
    }
    $resultDirectory = Join-Path $verificationRoot 'results'
    [System.IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
    $resultPath = Join-Path $resultDirectory $FileName
    [System.IO.File]::WriteAllText($resultPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Saved $resultPath"
}

function Run-Benchmarks {
    Build-Project -Project 'BoolPools/BoolPools.csproj'
    Build-Project -Project 'Performance/Performance.csproj'
    Build-Project -Project 'BufferComparison/Compare.csproj'
    Build-Project -Project 'Construction/Construction.csproj'
    Build-Project -Project 'Creation/Creation.csproj'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'BoolPools/bin/Release/net8.0/BoolPools.dll' -FileName 'bool-pools.json'
        Save-Benchmark -Assembly 'Performance/bin/Release/net8.0/Performance.dll' -FileName 'performance.json' -RunArguments @('preserved-harness')
        Save-Benchmark -Assembly 'BufferComparison/bin/Release/net8.0/Compare.dll' -FileName 'buffers-paired.json'
        Save-Benchmark -Assembly 'Construction/bin/Release/net8.0/Construction.dll' -FileName 'construction.json'
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'creation.json' -RunArguments @('--benchmark')
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'enumeration.json' -RunArguments @('--enumeration')
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'capacity.json' -RunArguments @('--capacity')
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

function Run-Construction {
    Build-Project -Project 'Construction/Construction.csproj'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'Construction/bin/Release/net8.0/Construction.dll' -FileName 'construction.json'
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

function Run-Creation {
    Run-Suite -TestProject 'Creation/Creation.csproj' -TestAssembly 'Creation/bin/Release/net8.0/Creation.dll'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'creation.json' -RunArguments @('--benchmark')
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

function Run-Enumeration {
    Run-Suite -TestProject 'Creation/Creation.csproj' -TestAssembly 'Creation/bin/Release/net8.0/Creation.dll'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'enumeration.json' -RunArguments @('--enumeration')
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

function Run-Capacity {
    Run-Suite -TestProject 'Creation/Creation.csproj' -TestAssembly 'Creation/bin/Release/net8.0/Creation.dll'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'Creation/bin/Release/net8.0/Creation.dll' -FileName 'capacity.json' -RunArguments @('--capacity')
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

function Run-LoopComparison {
    Run-Suite -TestProject 'LoopComparison/Tests/Baseline/Lifetime.csproj' -TestAssembly 'LoopComparison/Tests/Baseline/bin/Release/net8.0/Loops.Baseline.Lifetime.dll'
    Run-Suite -TestProject 'LoopComparison/Tests/Fused/Lifetime.csproj' -TestAssembly 'LoopComparison/Tests/Fused/bin/Release/net8.0/Loops.Fused.Lifetime.dll'
    Build-Project -Project 'LoopComparison/Compare.csproj'
    $previousTieredCompilation = [Environment]::GetEnvironmentVariable('DOTNET_TieredCompilation', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', '0', 'Process')
        Save-Benchmark -Assembly 'LoopComparison/bin/Release/net8.0/Compare.dll' -FileName 'loops-paired.json'
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_TieredCompilation', $previousTieredCompilation, 'Process')
    }
}

if ($Offline) {
    Write-Host 'Offline mode: reuse cached packages, ignore unavailable sources, and skip NuGet vulnerability auditing for this invocation.'
}
if ($Mode -eq 'Tests' -or $Mode -eq 'LoopComparison' -or $Mode -eq 'All') { Run-Tests }
if ($Mode -eq 'Benchmarks' -or $Mode -eq 'All') { Run-Benchmarks }
if ($Mode -eq 'Construction') { Run-Construction }
if ($Mode -eq 'Creation') { Run-Creation }
if ($Mode -eq 'Enumeration') { Run-Enumeration }
if ($Mode -eq 'Capacity') { Run-Capacity }
if ($Mode -eq 'LoopComparison' -or $Mode -eq 'All') { Run-LoopComparison }
