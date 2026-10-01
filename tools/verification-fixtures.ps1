function Get-FormulaVerificationSample {
  param([object[]]$Items)
  # LibraryService derives IDs from the source and SHA256 of the relative filename.
  # Pin formula-workbench.phyphox; catalog order and localized titles can change.
  $formulaId = 'sample-84281042995114bb0660'
  $samples = @($Items | Where-Object { $_.source -eq 'sample' -and $_.id -eq $formulaId })
  if ($samples.Count -ne 1) {
    throw "Expected exactly one formula-workbench.phyphox sample ($formulaId); found $($samples.Count)."
  }
  if ($samples[0].loadError) { throw "Formula verification sample could not be loaded: $($samples[0].loadError)" }
  return $samples[0]
}
