<# 
.SYNOPSIS
    Gets GPU VRAM information for AstroHybrid memory pressure handling.

.DESCRIPTION
    Queries WMI for GPU adapter RAM and outputs the VRAM in MB for the specified GPU.
    If no GPU name is provided, returns the maximum VRAM across all GPUs.

.PARAMETER GpuName
    Optional GPU name to match (substring match). If not provided, returns max VRAM.

.PARAMETER OutputFormat
    Output format: "mb" (default), "gb", "bytes", or "json".

.EXAMPLE
    .\get-gpu-vram.ps1
    Returns: 8192 (for 8GB GPU)

.EXAMPLE
    .\get-gpu-vram.ps1 -GpuName "RTX 4090" -OutputFormat gb
    Returns: 24

.EXAMPLE
    .\get-gpu-vram.ps1 -OutputFormat json
    Returns: {"max_vram_mb": 8192, "gpus": [{"name": "NVIDIA GeForce RTX 4090", "vram_mb": 24576}]}
#>

param(
    [string]$GpuName = "",
    [ValidateSet("mb", "gb", "bytes", "json")]
    [string]$OutputFormat = "mb"
)

try {
    $gpus = @()
    $maxVramBytes = 0
    
    # Query WMI for video controllers
    $videoControllers = Get-WmiObject -Class Win32_VideoController -ErrorAction SilentlyContinue
    
    foreach ($controller in $videoControllers) {
        $name = $controller.Name
        $adapterRam = $controller.AdapterRAM
        
        if ($null -eq $name -or $name -eq "") { continue }
        if ($null -eq $adapterRam -or $adapterRam -eq 0) { continue }
        
        # Skip Microsoft Basic Display Adapter and similar
        if ($name -like "*Basic*Display*" -or $name -like "*Remote*" -or $name -like "*Virtual*") { continue }
        
        $vramBytes = [long]$adapterRam
        $vramMB = [math]::Floor($vramBytes / 1MB)
        
        $gpus += @{
            name = $name
            vram_bytes = $vramBytes
            vram_mb = $vramMB
            vram_gb = [math]::Round($vramBytes / 1GB, 2)
            driver_version = $controller.DriverVersion
            driver_date = $controller.DriverDate
        }
        
        if ($vramBytes -gt $maxVramBytes) {
            $maxVramBytes = $vramBytes
        }
    }
    
    # If specific GPU requested, filter
    if ($GpuName) {
        $matched = $gpus | Where-Object { $_.name -like "*$GpuName*" }
        if ($matched.Count -gt 0) {
            $maxVramBytes = ($matched | Measure-Object -Property vram_bytes -Maximum).Maximum
        } else {
            Write-Error "GPU matching '$GpuName' not found"
            exit 1
        }
    }
    
    # Output based on format
    switch ($OutputFormat) {
        "mb" { [math]::Floor($maxVramBytes / 1MB) }
        "gb" { [math]::Round($maxVramBytes / 1GB, 2) }
        "bytes" { $maxVramBytes }
        "json" {
            $result = @{
                max_vram_mb = [math]::Floor($maxVramBytes / 1MB)
                max_vram_gb = [math]::Round($maxVramBytes / 1GB, 2)
                gpus = $gpus
            }
            $result | ConvertTo-Json -Depth 3
        }
    }
}
catch {
    Write-Error "Failed to get GPU VRAM: $_"
    exit 1
}