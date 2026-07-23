# Create a DEDICATED, least-privilege IAM user for this project's S3 deploy work (uploading the
# game archive to the game bucket), isolated from your everyday user. Run ONCE with your ADMIN
# credentials:
#
#   powershell -File infra/create-deploy-user.ps1 -Profile myadmin
#
# It creates:
#   * a customer-managed policy (infra/deploy-user-policy.json) scoped to ONLY the game bucket
#   * an IAM user with that policy attached
#   * an access key, printed at the end so you can configure it as a CLI profile
#
# The user can manage objects in punkmultiverse-gamefiles-... and nothing else. It has NO IAM,
# NO Lambda, and NO access to any other bucket. Creating the redirect Lambda is a separate,
# admin-only, one-time step (infra/gamefiles-lambda-setup.ps1) and deliberately NOT granted here.
param(
    [string]$Profile = "",
    [string]$UserName = "punkmv-deploy",
    [string]$PolicyName = "PunkMultiverseS3Deploy",
    [string]$Region = "us-east-1"
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$P = @()
if ($Profile) { $P = @("--profile", $Profile) }

# Call aws.exe explicitly: a bareword 'aws' resolves to THIS function (names are
# case-insensitive) and recurses to a call-depth overflow.
function Aws { & aws.exe @P @args }

$acct = (Aws sts get-caller-identity --query "Account" --output text)
Write-Host "== acting in account $acct =="

Write-Host "== 1/4 managed policy =="
$policyFile = Join-Path $RepoDir "infra\deploy-user-policy.json"
$policyArn = "arn:aws:iam::${acct}:policy/$PolicyName"
try {
    Aws iam create-policy --policy-name $PolicyName --policy-document ("file://" + $policyFile) `
        --description "PunkMultiverse deploy: manage the game-files S3 bucket only" | Out-Null
    Write-Host "  created policy $policyArn"
} catch {
    # Already exists: push the current file as a new default version so edits take effect.
    Aws iam create-policy-version --policy-arn $policyArn --policy-document ("file://" + $policyFile) `
        --set-as-default | Out-Null
    Write-Host "  updated policy $policyArn (new default version)"
}

Write-Host "== 2/4 user =="
try {
    Aws iam create-user --user-name $UserName | Out-Null
    Write-Host "  created user $UserName"
} catch { Write-Host "  (user $UserName exists)" }
Aws iam attach-user-policy --user-name $UserName --policy-arn $policyArn | Out-Null
Write-Host "  attached $PolicyName to $UserName"

Write-Host "== 3/4 access key =="
$key = Aws iam create-access-key --user-name $UserName | ConvertFrom-Json
$id = $key.AccessKey.AccessKeyId
$secret = $key.AccessKey.SecretAccessKey

Write-Host "== 4/4 done =="
Write-Host ""
Write-Host "Dedicated deploy user is ready. Configure it as a CLI profile named 'pmv-deploy':"
Write-Host ""
Write-Host "  aws configure set aws_access_key_id     $id --profile pmv-deploy"
Write-Host "  aws configure set aws_secret_access_key $secret --profile pmv-deploy"
Write-Host "  aws configure set region                $Region --profile pmv-deploy"
Write-Host ""
Write-Host "Then all game-S3 work runs as this user, e.g.:"
Write-Host "  aws --profile pmv-deploy s3 cp punk-base.tar.gz s3://punkmultiverse-gamefiles-${acct}-${Region}/punk-base.tar.gz"
Write-Host ""
Write-Host "SECURITY: the secret above is shown ONCE. Store it safely; anyone with it can manage"
Write-Host "the game bucket. To rotate: aws iam create-access-key / delete-access-key for $UserName."
