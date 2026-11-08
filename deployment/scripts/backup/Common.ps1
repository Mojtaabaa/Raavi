# Shared helpers for the Raavi backup / restore scripts (Phase 12b, ADR-038). Dot-source this file; it does nothing by itself.
# Uses System.Data.SqlClient only, so no PowerShell modules are needed (Windows PowerShell 5.1 or PowerShell 7 with the same assembly).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Data

function New-RaaviSqlConnection {
    param([Parameter(Mandatory)][string]$ServerInstance, [string]$Database = 'master', [string]$ConnectionString)
    $cs = if ($ConnectionString) { $ConnectionString } else { "Server=$ServerInstance;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=raavi-backup" }
    $c = New-Object System.Data.SqlClient.SqlConnection $cs
    $c.Open()
    return $c
}

function Invoke-RaaviSql {
    # Runs one statement; returns all rows as PSCustomObjects (empty array for statements without a result).
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory)][string]$Sql,
        [hashtable]$Parameters = @{},
        [int]$TimeoutSeconds = 7200
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $TimeoutSeconds
    foreach ($k in $Parameters.Keys) { [void]$cmd.Parameters.AddWithValue("@$k", $Parameters[$k]) }
    $rows = New-Object System.Collections.Generic.List[object]
    $r = $cmd.ExecuteReader()
    try {
        do {
            while ($r.Read()) {
                $o = [ordered]@{}
                for ($i = 0; $i -lt $r.FieldCount; $i++) {
                    $v = $r.GetValue($i)
                    $o[$r.GetName($i)] = if ($v -is [System.DBNull]) { $null } else { $v }
                }
                $rows.Add([pscustomobject]$o)
            }
        } while ($r.NextResult())
    } finally { $r.Dispose() }
    return , $rows.ToArray()
}

function Format-RaaviIdentifier {
    # Bracket-quotes a database / certificate name; refuses anything that is not a plain identifier.
    param([Parameter(Mandatory)][string]$Name)
    if ($Name -notmatch '^[A-Za-z0-9_.\-]{1,128}$') { throw "Not a valid database or object name: '$Name'" }
    return '[' + $Name + ']'
}

function Format-RaaviLiteral {
    # N'...' literal for a file path (single quotes doubled).
    param([Parameter(Mandatory)][string]$Value)
    return "N'" + $Value.Replace("'", "''") + "'"
}

function Get-RaaviDatabaseInfo {
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection, [Parameter(Mandatory)][string]$Database)
    $rows = Invoke-RaaviSql $Connection 'SELECT name, recovery_model_desc, compatibility_level, state_desc FROM sys.databases WHERE name = @n' @{ n = $Database }
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-RaaviTableCounts {
    # Row count per user table (name = schema.table) of the database the connection points at.
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection)
    $sql = @'
SELECT s.name + N'.' + t.name AS tbl, SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS cnt
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.partitions p ON p.object_id = t.object_id
GROUP BY s.name, t.name
'@
    $result = @{}
    foreach ($row in (Invoke-RaaviSql $Connection $sql)) { $result[$row.tbl] = [int64]$row.cnt }
    return $result
}

function Get-RaaviStructureSummary {
    # Things a restore must bring back besides rows: tables, security policies (row-level security), indexes, foreign keys.
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection)
    $sql = @'
SELECT
  (SELECT COUNT(*) FROM sys.tables) AS tables,
  (SELECT COUNT(*) FROM sys.security_policies) AS securityPolicies,
  (SELECT COUNT(*) FROM sys.indexes WHERE object_id IN (SELECT object_id FROM sys.tables) AND index_id > 0) AS indexes,
  (SELECT COUNT(*) FROM sys.foreign_keys) AS foreignKeys
'@
    return (Invoke-RaaviSql $Connection $sql)[0]
}

function Get-RaaviFileHash {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}
