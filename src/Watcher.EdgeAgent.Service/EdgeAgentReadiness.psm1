Set-StrictMode -Version Latest

function Test-EdgeAgentReadiness {
    [CmdletBinding()]
    param([Parameter(Mandatory = $false)][AllowNull()][object]$Response)

    if (-not $Response -or $Response.Succeeded -ne $true -or -not $Response.Status) {
        return $false
    }

    $status = $Response.Status
    $deviceId = [guid]::Empty
    $locationId = [guid]::Empty
    if (-not [guid]::TryParse([string]$status.DeviceId, [ref]$deviceId) -or
        -not [guid]::TryParse([string]$status.LocationId, [ref]$locationId) -or
        $deviceId -eq [guid]::Empty -or
        $locationId -eq [guid]::Empty) {
        return $false
    }

    return $status.ConnectionState -eq 'Streaming' -and
        $status.AudioState -in @('ready', 'idle', 'playing') -and
        -not [string]::IsNullOrWhiteSpace([string]$status.AudioEndpointId)
}

Export-ModuleMember -Function Test-EdgeAgentReadiness
