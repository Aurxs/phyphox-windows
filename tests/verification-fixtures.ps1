$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../tools/verification-fixtures.ps1')

function Assert-Rejected($items, $message) {
  try { $null = Get-FormulaVerificationSample -Items $items }
  catch {
    if ($_.Exception.Message -notlike $message) { throw }
    return
  }
  throw 'Invalid formula fixture selection was accepted.'
}

# This is LibraryService's ID for assets/samples/formula-workbench.phyphox.
$formulaId = 'sample-84281042995114bb0660'
$formula = [pscustomobject]@{ id=$formulaId; source='sample'; title='Renamed formula'; loadError=$null }
$phone = [pscustomobject]@{ id='sample-phone'; source='sample'; title='Formula workbench'; loadError=$null }
$user = [pscustomobject]@{ id='user-formula'; source='user'; title='Formula workbench'; loadError=$null }

# A phone sample precedes the formula sample after sorting the current catalog.
# Display titles and source alone must not identify this numerical fixture.
foreach ($items in @(@($phone, $user, $formula), @($formula, $phone, $user), @($user, $formula, $phone))) {
  $selected = Get-FormulaVerificationSample -Items $items
  if ($selected.id -ne $formulaId) { throw 'Selected a different sample instead of the formula fixture.' }
}
Assert-Rejected @() '*Expected exactly one formula-workbench.phyphox sample*'
Assert-Rejected @($phone, $user) '*Expected exactly one formula-workbench.phyphox sample*'
Assert-Rejected @($formula, $formula) '*Expected exactly one formula-workbench.phyphox sample*'
Assert-Rejected @([pscustomobject]@{ id=$formulaId; source='user'; loadError=$null }) '*Expected exactly one formula-workbench.phyphox sample*'
Assert-Rejected @([pscustomobject]@{ id=$formulaId; source='sample'; loadError='Invalid XML' }) '*Formula verification sample could not be loaded: Invalid XML*'
Write-Output 'PASS: formula fixture identity, catalog order, missing/duplicate/unreadable fixture rejection.'
