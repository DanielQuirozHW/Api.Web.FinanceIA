#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# PreToolUse hook on Bash — blocks every EF Core schema-management command.
#
# FinancIA is Database-First against Azure SQL. Schema changes are numbered DDL
# scripts in Api.Web.FinanceIA/docs/database/, executed by the DBA by hand and
# BEFORE the matching deploy. EF must never generate or apply schema.
#
# Wire up in .claude/settings.json:
#   "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [
#     { "type": "command",
#       "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/block-ef-migrations.sh" } ] } ] }
#
# Contract: exit 0 and print permissionDecision:"deny" to deny; exit 0 silently
# to allow. Deliberately never exits non-zero — a crashing hook must not wedge
# every Bash call in the session.
#
# NOTE: `grep` is shimmed to ugrep in this environment, so no grep is used here.
# Matching is done with bash's own =~ (ERE), which needs no external binary.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

INPUT=$(cat)

# ── Extract .tool_input.command. jq if present, python3 otherwise. ────────────
extract_command() {
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$INPUT" | jq -r '.tool_input.command // ""' 2>/dev/null && return
  fi
  printf '%s' "$INPUT" | python3 -c \
    'import sys,json
try: print(json.load(sys.stdin).get("tool_input",{}).get("command",""))
except Exception: print("")' 2>/dev/null
}

CMD=$(extract_command) || CMD=""
[ -z "$CMD" ] && exit 0

REASON='BLOCKED: FinancIA is Database-First. EF schema tooling is forbidden.
Schema changes are numbered DDL scripts in Api.Web.FinanceIA/docs/database/, run
by the DBA by hand BEFORE the matching deploy (EF maps columns unconditionally,
so deploying first breaks production with "Invalid column name").
Write a new numbered .sql script there instead. See CLAUDE.md > Database Approach.'

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

# ── Normalise: newlines/tabs -> spaces, lowercase. ───────────────────────────
NORM=$(printf '%s' "$CMD" | tr '\n\t' '  ' | tr '[:upper:]' '[:lower:]')

# Split into command segments so we only match an ACTUAL invocation, never the
# same words quoted inside another command. This is what keeps
#   git commit -m "never run dotnet ef"
#   rg -n "dotnet ef migrations" docs/
# working, while still catching a cd-prefix, &&, ;, | or a subshell.
SEGMENTS=$(printf '%s' "$NORM" | tr ';|&()`' '\n\n\n\n\n\n\n')

# Anchored at the start of a segment, after optional leading whitespace and any
# number of leading VAR=value environment assignments.
ENVPREFIX='^[[:space:]]*([a-z_][a-z0-9_]*=[^[:space:]]*[[:space:]]+)*'
RE_DOTNET_EF="${ENVPREFIX}dotnet[[:space:]]+ef([[:space:]]|$)"
RE_DOTNET_EF_HYPHEN="${ENVPREFIX}dotnet-ef([[:space:]]|$)"
# Package Manager Console cmdlets (powershell / pwsh).
RE_PMC="${ENVPREFIX}(add|remove|script|drop)-migration([[:space:]]|$)"
RE_PMC_DB="${ENVPREFIX}(update|drop)-database([[:space:]]|$)"
# Installing the EF CLI is how this rule gets re-armed, so block that too. Here
# dotnet-ef is an ARGUMENT, not the invoked command, so the anchored rules above
# do not catch it — this needs its own pattern.
RE_TOOL_INSTALL="${ENVPREFIX}dotnet[[:space:]]+tool[[:space:]]+(install|update|run|restore)([[:space:]]|$)"

while IFS= read -r seg; do
  [ -z "${seg// /}" ] && continue
  if [[ "$seg" =~ $RE_DOTNET_EF ]]        ; then deny; fi
  if [[ "$seg" =~ $RE_DOTNET_EF_HYPHEN ]] ; then deny; fi
  if [[ "$seg" =~ $RE_PMC ]]              ; then deny; fi
  if [[ "$seg" =~ $RE_PMC_DB ]]           ; then deny; fi
  if [[ "$seg" =~ $RE_TOOL_INSTALL && "$seg" == *dotnet-ef* ]] ; then deny; fi
done <<< "$SEGMENTS"

exit 0
