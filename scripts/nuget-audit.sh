#!/usr/bin/env bash
set -euo pipefail
mkdir -p TestResults/evidence
set +e
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all '-p:WarningsAsErrors=NU1900%3BNU1903%3BNU1904%3BNU1905' 2>&1 | tee TestResults/evidence/nuget-restore.log
restore_status=("${PIPESTATUS[@]}")
set -e
while IFS= read -r line; do
  if [[ "$line" =~ NU19[0-9]{2} ]]; then
    printf '%s\n' "${line//"${GITHUB_WORKSPACE:-$PWD}/"/}"
  fi
done < TestResults/evidence/nuget-restore.log > TestResults/evidence/nuget-audit.txt
{
  echo '### NuGet dependency audit'
  echo 'High/Critical（NU1903/NU1904）と監査データ取得失敗（NU1900/NU1905）で失敗します。'
  echo
  echo '```text'
  if [ -s TestResults/evidence/nuget-audit.txt ]; then
    cat TestResults/evidence/nuget-audit.txt
  elif [ "${restore_status[0]}" -eq 0 ]; then
    echo '該当なし'
  fi
  if [ "${restore_status[0]}" -ne 0 ]; then
    printf 'restore 失敗（exit %s）。詳細はこのステップのログを参照してください。\n' "${restore_status[0]}"
  fi
  echo '```'
} | tee TestResults/evidence/nuget-summary.md
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then cat TestResults/evidence/nuget-summary.md >> "$GITHUB_STEP_SUMMARY"; fi
if [ "${restore_status[1]}" -ne 0 ]; then exit "${restore_status[1]}"; fi
exit "${restore_status[0]}"
