# refresh-local.sample.ps1
# PERSONAL wrapper (copy to refresh-local.ps1, which is gitignored if it
# contains machine-specific paths). Purpose: make "restored" and "clean" one
# motion, so a raw-PII database named AAVSB never sits idle on this machine.
# This wraps the existing team restore script WITHOUT modifying it.

$ErrorActionPreference = "Stop"

# 1. Run the existing team restore script exactly as-is
#    (adjust the path to wherever your copy lives)
sqlcmd -S localhost -E -i "C:\temp\AAVSB\restore-aavsb-local.sql" -b
if ($LASTEXITCODE -ne 0) { throw "Restore script failed ($LASTEXITCODE)" }

# 2. Immediately scrub the restored copy in place
dbscrub clean --server localhost --database AAVSB --config "$PSScriptRoot\..\config\aavsb.masking.json" --yes
if ($LASTEXITCODE -ne 0) { throw "dbscrub clean failed ($LASTEXITCODE) - AAVSB IS STILL RAW" }

# 3. Belt and suspenders: confirm the stamp
#
#    NOT YET USABLE. `clean` masks but does not stamp, because the verify gate
#    that earns a stamp is step 5 (DECISIONS.md D22) — so this check fails today
#    even after a completely successful clean. That is the honest answer, not a
#    bug: nothing has verified the result. Uncomment when step 5 ships.
#
# dbscrub status --server localhost --database AAVSB
# if ($LASTEXITCODE -ne 0) { throw "AAVSB is not stamped clean" }

Write-Host "AAVSB restored and sanitized." -ForegroundColor Green

# Prod-support mode: don't use this wrapper - run the team restore script
# directly and skip dbscrub. 'dbscrub status' will report unstamped, which is
# the honest answer.
