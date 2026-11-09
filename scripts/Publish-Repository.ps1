param([string]$Repository = 'xjanova/XSurLab')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git failed ($LASTEXITCODE): $args" }
}

& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub CLI in your regular user session first: gh auth login' }
$visibility = & gh repo view $Repository --json visibility --jq '.visibility'
if ($LASTEXITCODE -ne 0) { throw "Cannot read $Repository." }
if ($visibility.Trim() -ne 'PUBLIC') { throw 'This project is expected to be public, as requested by its owner.' }

$expectedRemote = "https://github.com/$Repository.git"
$existingRemote = & git remote get-url origin 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Git remote add origin $expectedRemote
} elseif ($existingRemote.Trim() -ne $expectedRemote) {
    throw "origin points to $existingRemote. Review it before changing the remote."
}
$branch = & git branch --show-current
if ($branch.Trim() -ne 'main') { throw 'Run this bootstrap from main.' }
& git rev-parse --verify HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    & git ls-remote --exit-code --heads origin main
    if ($LASTEXITCODE -eq 0) {
        Invoke-Git fetch origin main
        Invoke-Git reset --mixed origin/main
    }
}
Invoke-Git add -- .gitignore global.json Directory.Build.props XSurLab.slnx README.md src tests docs design scripts .github
Invoke-Git diff --cached --check
& git diff --cached --quiet
$diffExit = $LASTEXITCODE
if ($diffExit -eq 1) {
    Invoke-Git commit -m 'Build XSurLab native offline surround mixer'
} elseif ($diffExit -ne 0) {
    throw 'Could not inspect staged changes.'
}
Invoke-Git push -u origin main
Write-Host "Published https://github.com/$Repository"

