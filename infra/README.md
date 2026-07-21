# Tester log diagnostics

Every net run gets a **run id** (e.g. `4914F229-17C8`) that is identical on every player's
machine — derived at go-live from the run seed plus the host's identity, so it needs no
network message. Players quote it in bug reports; `uploadlogs` files their log under it.

## For players / testers

Two devcmds (or the F1 dev console):

    runid        prints this run's id
    uploadlogs   gzips this machine's BepInEx log and uploads it

`uploadlogs` **always** writes the gzip to `BepInEx/plugins/PunkMultiverse/diagnostics/<runId>/`
first and prints the full path, then tries the upload. If the endpoint is unset, unreachable,
or errors, the message becomes "send this file: <path>" — the log is never lost, and the
pipeline is optional infrastructure, not a dependency.

To enable uploads, set in `BepInEx/plugins/PunkMultiverse/config.cfg`:

    [Diag]
    LogUploadEndpoint = https://<function-url>.lambda-url.us-east-1.on.aws/

Empty (the default) = collect-only.

## Architecture

    mod --GET--> Lambda Function URL  --> presigned S3 PUT URL, scoped to ONE key,
                 (validates + signs)      exact declared size, 5-minute expiry
    mod --PUT--> S3 (private bucket, no public access)

No AWS credentials ship in the mod (a DLL is trivially decompiled) and the bucket allows
no anonymous access. A leaked signed URL buys one bounded upload to one key for 5 minutes.
Objects auto-expire after 30 days.

## Live deployment (2026-07-20)

| | |
|---|---|
| Bucket | `punkmultiverse-diagnostics-577792960632-us-east-1-an` (us-east-1, private) |
| Lambda | `punkmv-log-signer` (Python, `lambda_handler`, env `BUCKET` = the bucket above) |
| Role | `punkmv-log-signer-role-f8ulatgh`, inline `punkmv-signer-put` = `s3:PutObject` on `<bucket>/PunkMultiverse/logs/*` |
| Endpoint | `https://57mjrwp6bts74pm7hsbv6rlgq40eekkq.lambda-url.us-east-1.on.aws/` |

Verified end to end: signer 200 → presigned PUT 200 → object downloaded and decompressed
to a readable log; public read of the same object returns 403.

## Reading the logs

    aws s3 ls s3://punkmultiverse-diagnostics-577792960632-us-east-1-an/PunkMultiverse/logs/
    aws s3 sync s3://punkmultiverse-diagnostics-577792960632-us-east-1-an/PunkMultiverse/logs/<runId>/ ./logs-<runId>/

## Gotchas (each cost us a debugging round)

* **Bucket names are globally unique.** The console suffixes them silently. The same name
  must appear in the Lambda's `BUCKET` env var *and* the role's inline policy — a mismatch
  gives 404 (env var wrong) or 403 (policy wrong) only at PUT time, never at sign time.
* **Missing `BUCKET` env var = 502** on cold start (`os.environ["BUCKET"]` raises).
* **IAM propagation** can take a few seconds; retry a 403 before concluding the policy is wrong.
* The 3-second default Lambda timeout is fine — presigning is local crypto, no network call.
