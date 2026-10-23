#Requires -Version 7.0
# Kept only while a registration still names the policies one by one (.claude/settings.json, and the trusted
# .codex/hooks.json until the owner trusts the new one); the payload's shape names the tool. Delete afterwards.
& "$PSScriptRoot/agent-policy.ps1" -Only world-devices
exit $LASTEXITCODE
