#Requires -Version 7.0
# Kept only while .claude/settings.json registers the policies one by one; delete once it registers agent-policy.ps1.
& "$PSScriptRoot/agent-policy.ps1" -Tool claude -Only world-devices
exit $LASTEXITCODE
