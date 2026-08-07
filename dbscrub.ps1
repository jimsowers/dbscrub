# dbscrub — run the tool without remembering the dotnet incantation.
#
#   .\dbscrub report --server localhost --database MyDb --config config\mydb.masking.json
#
# Why this exists: the raw form is
#
#   dotnet run --project src/DbScrub.Cli -- report --server ...
#
# and that bare `--` is load-bearing. Everything before it belongs to `dotnet`,
# everything after it to dbscrub. Lose the space and it becomes `--report`,
# which dotnet reads as its own unknown option and rejects with a wall of
# "Unrecognized command or argument" lines. That is a bad thing to put in every
# example in the documentation, so this script puts it in one place instead.
#
# Works from any directory: paths resolve against the script's own location,
# not the caller's. The tool's exit code is passed straight through, because
# they are load-bearing (SPEC section 2) and a wrapper that swallowed them
# would quietly break any script gating on the result.

$ErrorActionPreference = 'Stop'

& dotnet run --project (Join-Path $PSScriptRoot 'src/DbScrub.Cli') -- @args

exit $LASTEXITCODE
