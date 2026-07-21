# One-time setup for the tester log-diagnostics bucket (run with your AWS credentials):
#   powershell -File infra/diagnostics-s3-setup.ps1
#
# Creates s3://punkmultiverse-diagnostics with:
#   - anonymous WRITE-ONLY access to PunkMultiverse/logs/* (the mod ships no credentials;
#     players' `uploadlogs` devcmd PUTs gzipped BepInEx logs there, grouped by run id)
#   - NO public read/list (ACLs stay blocked; only s3:PutObject is granted)
#   - 30-day auto-expiry on the logs prefix (bounds the classic junk-upload/billing risk
#     of an anonymous-PUT bucket — the accepted tradeoff for a zero-backend playtest pipeline)
#
# Reading logs (you, with credentials):
#   aws s3 ls s3://punkmultiverse-diagnostics/PunkMultiverse/logs/
#   aws s3 sync s3://punkmultiverse-diagnostics/PunkMultiverse/logs/<runId>/ ./logs-<runId>/

$ErrorActionPreference = "Stop"
$Bucket = "punkmultiverse-diagnostics"

aws s3api create-bucket --bucket $Bucket --region us-east-1

aws s3api put-public-access-block --bucket $Bucket --public-access-block-configuration `
    "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=false,RestrictPublicBuckets=false"

$policy = @'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "AnonWriteOnlyGameLogs",
    "Effect": "Allow",
    "Principal": "*",
    "Action": "s3:PutObject",
    "Resource": "arn:aws:s3:::punkmultiverse-diagnostics/PunkMultiverse/logs/*"
  }]
}
'@
$policyFile = Join-Path $env:TEMP "punkmv-log-bucket-policy.json"
Set-Content -Path $policyFile -Value $policy -Encoding Ascii
aws s3api put-bucket-policy --bucket $Bucket --policy ("file://" + $policyFile)

$lifecycle = @'
{
  "Rules": [{
    "ID": "expire-playtest-logs-30d",
    "Filter": { "Prefix": "PunkMultiverse/logs/" },
    "Status": "Enabled",
    "Expiration": { "Days": 30 }
  }]
}
'@
$lifecycleFile = Join-Path $env:TEMP "punkmv-log-bucket-lifecycle.json"
Set-Content -Path $lifecycleFile -Value $lifecycle -Encoding Ascii
aws s3api put-bucket-lifecycle-configuration --bucket $Bucket --lifecycle-configuration ("file://" + $lifecycleFile)

Write-Host ""
Write-Host "DONE. Smoke test the anonymous write-only contract:"
Write-Host "  curl.exe -s -X PUT --data-binary test https://$Bucket.s3.amazonaws.com/PunkMultiverse/logs/setup-test/probe.txt -o - -w '%{http_code}'   (expect 200)"
Write-Host "  curl.exe -s https://$Bucket.s3.amazonaws.com/PunkMultiverse/logs/setup-test/probe.txt -o NUL -w '%{http_code}'                          (expect 403 — no public read)"
