#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# PreToolUse hook on Write|Edit|MultiEdit|NotebookEdit — blocks the creation or
# editing of EF Core migration artefacts by hand.
#
# Companion to block-ef-migrations.sh. That one stops `dotnet ef` from RUNNING;
# this one closes the other half of the hole: permissions allow Write(*), so a
# migration could simply be typed out.
#
# FinancIA has no Migrations/ folder and must never grow one. Schema changes are
# numbered DDL scripts in Api.Web.FinanceIA/docs/database/, run by the DBA.
#
# Wire up in .claude/settings.json:
#   { "matcher": "Write|Edit|MultiEdit|NotebookEdit", "hooks": [
#     { "type": "command",
#       "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/block-migration-writes.sh" } ] }
#
# Contract: exit 0 and print permissionDecision:"deny" to deny; exit 0 silently
# to allow. Never exits non-zero.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

INPUT=$(cat)

# Write/Edit/MultiEdit use tool_input.file_path; NotebookEdit uses notebook_path.
# Rather than hardcode those two names — the hooks reference does not actually
# document Write's tool_input shape — take every top-level key whose NAME is
# "path" or ends in "_path". A renamed or added path field is then still caught,
# while file CONTENT is never inspected. That distinction matters: content is
# how you would write documentation about this very rule, and scanning it would
# block any file that merely mentions Migrations/.
extract_paths() {
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$INPUT" \
      | jq -r '.tool_input | to_entries[] | select(.key=="path" or (.key|endswith("_path"))) | .value | strings' 2>/dev/null && return
  fi
  printf '%s' "$INPUT" | python3 -c \
    'import sys,json
try:
    for k,v in json.load(sys.stdin).get("tool_input",{}).items():
        if isinstance(v,str) and (k=="path" or k.endswith("_path")): print(v)
except Exception: pass' 2>/dev/null
}

TARGETS=$(extract_paths) || TARGETS=""
[ -z "$TARGETS" ] && exit 0

REASON='BLOCKED: FinancIA has no Migrations/ folder and must never grow one.
This repo is Database-First: schema changes are numbered DDL scripts in
Api.Web.FinanceIA/docs/database/, executed by the DBA by hand BEFORE the
matching deploy. See CLAUDE.md > Database Approach.'

deny() {
  if command -v jq >/dev/null 2>&1; then
    jq -n --arg r "$REASON" \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"deny",permissionDecisionReason:$r}}'
  else
    REASON="$REASON" python3 -c \
      'import json,os
print(json.dumps({"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":os.environ["REASON"]}}))'
  fi
  exit 0
}

while IFS= read -r TARGET; do
  [ -z "$TARGET" ] && continue
  LOWER=$(printf '%s' "$TARGET" | tr '[:upper:]' '[:lower:]')

  # Any path segment named Migrations/ (case-insensitive).
  case "/$LOWER" in
    */migrations/*) deny ;;
  esac

  # EF's generated companions. Verified: this tree contains zero *.Designer.cs,
  # so a blanket block costs nothing here. In a WinForms/.resx repo this would
  # need narrowing to the EF form ^[0-9]{14}_.*\.Designer\.cs$.
  BASE=$(basename "$LOWER")
  case "$BASE" in
    *modelsnapshot.cs|*.designer.cs) deny ;;
  esac
done <<< "$TARGETS"

exit 0
