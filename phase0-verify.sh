#!/usr/bin/env bash
# Phase 0 acceptance verification - run against a locally running Car Dealer API.
# Usage:  bash phase0-verify.sh [base-url]        (default http://localhost:5080)
# Requires: bash + curl only.

BASE="${1:-http://localhost:5080}"
PW='Dev_Passw0rd!'
CURLA=(curl -s -m 20 --noproxy '*')
pass=0; fail=0; warn=0

c()  { printf '\033[%sm%s\033[0m' "$1" "$2"; }
ok() { pass=$((pass+1)); printf '  %s  %s\n' "$(c '0;32' 'PASS')" "$1"; }
no() { fail=$((fail+1)); printf '  %s  %s\n' "$(c '0;31' 'FAIL')" "$1"
       [ -n "$2" ] && printf '        got: %s\n' "$(echo "$2" | head -c 220)"; return 0; }
info(){ printf '        %s\n' "$1"; }
hdr(){ printf '\n%s\n' "$(c '1;36' "$1")"; }

req()  { "${CURLA[@]}" "$@"; }
code() { "${CURLA[@]}" -o /dev/null -w '%{http_code}' "$@"; }
js()   { echo "$1" | grep -o "\"$2\":\"[^\"]*\"" | head -1 | sed "s/.*\":\"//;s/\"$//"; }
ja()   { echo "$1" | grep -o "\"$2\":\[[^]]*\]" | head -1; }
nitems(){ n=$(echo "$1" | grep -o ',' | wc -l | tr -d ' '); echo $((n+1)); }

login() {
  if [ -n "$2" ]; then b="{\"email\":\"$1\",\"password\":\"$PW\",\"tenantSlug\":\"$2\"}"
  else b="{\"email\":\"$1\",\"password\":\"$PW\"}"; fi
  req -X POST "$BASE/api/v1/auth/login" -H 'Content-Type: application/json' -d "$b"
}

printf '%s\n' "$(c '1;37' "Phase 0 verification against $BASE")"

hdr 'A3, F5, F6 - running, health, liveness vs readiness'
for p in /health /health/live /health/ready; do
  r=$(code "$BASE$p"); [ "$r" = "200" ] && ok "GET $p -> 200" || no "GET $p -> $r (expected 200)"
done

hdr 'D2, D3 - login and tenant-scoped token'
L=$(login 'owner@nihon-motors.test'); TOK=$(js "$L" accessToken)
if [ -z "$TOK" ]; then no 'login failed - aborting' "$L"; exit 1; fi
ok 'login returns an access token'
[ -n "$(js "$L" refreshToken)" ] && ok 'login returns a refresh token' || no 'no refreshToken'
PAY=$(echo "$TOK" | cut -d. -f2); case $((${#PAY} % 4)) in 2) PAY="$PAY==";; 3) PAY="$PAY=";; esac
TID=$(echo "$PAY" | tr '_-' '/+' | base64 -d 2>/dev/null | grep -o '"tenant_id":"[^"]*"' | sed 's/.*":"//;s/"//')
[ -n "$TID" ] && ok "D3: token carries tenant_id=$TID" || no 'D3: no tenant_id claim in JWT'

hdr 'H1, H2 - cache abstraction  *** the key gap: Redis path never ran in CI ***'
CR=$(req -X POST "$BASE/api/v1/diagnostics/cache-roundtrip?key=signoff&value=v1" -H "Authorization: Bearer $TOK")
case "$(js "$CR" implementation)" in
  DistributedCacheService) ok 'implementation = DistributedCacheService  (Redis path LIVE)';;
  InMemoryCacheService)    no 'implementation = InMemoryCacheService - Redis did NOT connect' "$CR";;
  *)                       no 'unexpected cache-roundtrip response' "$CR";;
esac
echo "$CR" | grep -q '"matched":true' && ok 'cache round-trip value matched' || no 'round-trip mismatch' "$CR"

hdr 'C2 - client-supplied tenant id must be ignored'
B1=$(js "$(req "$BASE/api/v1/tenants/current" -H "Authorization: Bearer $TOK")" slug)
F1=$(js "$(req "$BASE/api/v1/tenants/current?tenantId=2&tenantSlug=karachi-auto" \
      -H "Authorization: Bearer $TOK" -H 'X-Tenant-Id: 2' -H 'X-Tenant-Slug: karachi-auto')" slug)
[ "$B1" = "nihon-motors" ] && ok "baseline tenant = $B1" || no "baseline = $B1 (expected nihon-motors)"
[ "$F1" = "nihon-motors" ] && ok 'forged tenant id in query + 2 headers IGNORED' \
                           || no "forged tenant id HONOURED -> $F1   *** CROSS-TENANT LEAK ***"

hdr 'C5, C6, E6 - permissions resolve per tenant, switch issues a new token'
M=$(login 'multi@example.test')
echo "$M" | grep -q '"requiresTenantSelection":true' && ok 'dual-membership user must select a tenant' \
                                                     || no 'expected requiresTenantSelection=true' "$M"
MN=$(login 'multi@example.test' 'nihon-motors'); MNT=$(js "$MN" accessToken)
PN=$(ja "$MN" permissions); CN=$(nitems "$PN"); info "nihon-motors ($CN): $PN"
SW=$(req -X POST "$BASE/api/v1/auth/switch-tenant" -H "Authorization: Bearer $MNT" \
     -H 'Content-Type: application/json' -d '{"tenantSlug":"karachi-auto"}')
SWT=$(js "$SW" accessToken); PK=$(ja "$SW" permissions); CK=$(nitems "$PK"); info "karachi-auto ($CK): $PK"
{ [ -n "$SWT" ] && [ "$SWT" != "$MNT" ]; } && ok 'C6: switch issued a NEW token' || no 'C6: no new token issued'
[ "$CN" -gt "$CK" ] && ok "E6: permissions differ per tenant ($CN vs $CK)" \
                    || no "E6: permissions did not differ ($CN vs $CK)"
OLD=$(js "$(req "$BASE/api/v1/tenants/current" -H "Authorization: Bearer $MNT")" slug)
[ "$OLD" = "nihon-motors" ] && ok 'C6: old token still scoped to old tenant' \
                            || no "C6: old token silently changed scope -> $OLD"

hdr 'C7 - tenant switch writes an audit row (read as the tenant switched INTO)'
KT=$(js "$(login 'owner@karachi-auto.test')" accessToken)
AUD=$(req "$BASE/api/v1/audit?take=200" -H "Authorization: Bearer $KT")
echo "$AUD" | grep -q 'auth.tenant.switched' && ok 'auth.tenant.switched found in karachi-auto audit log' \
                                             || no 'no auth.tenant.switched row' "$AUD"
NT=$(req "$BASE/api/v1/audit?take=200" -H "Authorization: Bearer $TOK")
echo "$NT" | grep -q 'auth.tenant.switched' \
  && no 'C3 CONCERN: nihon-motors can see another tenant audit row' \
  || ok 'C3: that row is NOT visible to nihon-motors (isolation holds)'

hdr 'C8 - per-tenant suspension does not lock the user out globally'
SU=$(login 'suspended@example.test')
HN=$(echo "$SU" | grep -c 'nihon-motors'); HK=$(echo "$SU" | grep -c 'karachi-auto')
{ [ "$HN" -ge 1 ] && [ "$HK" -eq 0 ]; } && ok 'suspended user sees nihon-motors only' \
                                        || no "expected nihon only (nihon=$HN karachi=$HK)" "$SU"

hdr 'D4, D5 - rotation and revocation chain  (separates real revocation from expiry)'
R1=$(js "$(login 'sales@nihon-motors.test')" refreshToken)
RR=$(req -X POST "$BASE/api/v1/auth/refresh" -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R1\"}")
R2=$(js "$RR" refreshToken)
{ [ -n "$R2" ] && [ "$R2" != "$R1" ]; } && ok 'D4: refresh rotated the token' || no 'D4: not rotated' "$RR"
r=$(code -X POST "$BASE/api/v1/auth/refresh" -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R1\"}")
[ "$r" = "401" ] && ok 'D4: reusing the old refresh token -> 401' || no "D4: reuse -> $r (expected 401)"
r=$(code -X POST "$BASE/api/v1/auth/refresh" -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R2\"}")
[ "$r" = "401" ] && ok 'D5: reuse killed the WHOLE chain -> 401' \
                 || no "D5: chain still alive -> $r   *** weak revocation ***"

hdr 'D6 - logout revokes the refresh token'
LO=$(login 'readonly@nihon-motors.test'); LOT=$(js "$LO" accessToken); LOR=$(js "$LO" refreshToken)
code -X POST "$BASE/api/v1/auth/logout" -H "Authorization: Bearer $LOT" \
     -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$LOR\"}" >/dev/null
r=$(code -X POST "$BASE/api/v1/auth/refresh" -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$LOR\"}")
[ "$r" = "401" ] && ok 'refresh after logout -> 401' || no "refresh after logout -> $r (expected 401)"

hdr 'E2, E3 - 403 (no permission) distinct from 401 (unauthenticated)'
RO=$(js "$(login 'readonly@nihon-motors.test')" accessToken)
r=$(code "$BASE/api/v1/roles"); [ "$r" = "401" ] && ok 'unauthenticated -> 401' || no "unauthenticated -> $r"
r=$(code -X POST "$BASE/api/v1/roles" -H "Authorization: Bearer $RO" \
    -H 'Content-Type: application/json' -d '{"name":"Nope","description":null,"permissionCodes":[]}')
[ "$r" = "403" ] && ok 'ReadOnly on a manage endpoint -> 403' || no "ReadOnly write -> $r (expected 403)"

hdr 'E4 - two tenants may each define a role with the same name'
mk(){ code -X POST "$BASE/api/v1/roles" -H "Authorization: Bearer $1" \
      -H 'Content-Type: application/json' -d '{"name":"Sales Manager","description":null,"permissionCodes":[]}'; }
r1=$(mk "$TOK"); r2=$(mk "$KT")
{ [ "$r1" = "201" ] || [ "$r1" = "409" ]; } && { [ "$r2" = "201" ] || [ "$r2" = "409" ]; } \
  && ok "same role name accepted in both tenants (nihon=$r1 karachi=$r2)" \
  || no "tenant-scoped role names rejected (nihon=$r1 karachi=$r2)"

hdr 'F2, F4, G4 - error contract and correlation id'
CID='signoff-correlation-check-1'
req -D - -o /dev/null "$BASE/api/v1/roles" -H "X-Correlation-Id: $CID" | grep -qi "$CID" \
  && ok 'G4: supplied X-Correlation-Id echoed back' || no 'G4: correlation id not echoed'
V=$(req -X POST "$BASE/api/v1/auth/login" -H 'Content-Type: application/json' -d '{"email":"not-an-email"}')
echo "$V" | grep -q '"status":400' && ok 'F2: validation -> 400 problem+json' || no 'F2: unexpected' "$V"
echo "$V" | grep -q 'correlationId' && ok 'F4: error body carries correlationId' || no 'F4: missing correlationId'

hdr 'F1, F7 - versioning and OpenAPI completeness'
SJ=$(req "$BASE/swagger/v1/swagger.json")
OPS=$(echo "$SJ" | grep -o '"/api/v1/[^"]*"' | sort -u | wc -l | tr -d ' ')
[ "$OPS" -ge 12 ] && ok "F1/F7: $OPS versioned /api/v1 paths documented" || no "only $OPS paths (expected ~12)"
echo "$SJ" | grep -q '"securitySchemes"' && ok 'F7: securitySchemes present' || no 'F7: no securitySchemes'
echo "$SJ" | grep -Eq '"bearerFormat"[[:space:]]*:[[:space:]]*"JWT"' && ok 'F7: Bearer/JWT scheme' \
                                                                    || no 'F7: bearerFormat is not JWT'
echo "$SJ" | grep -Eq '"security"[[:space:]]*:' && ok 'F7: auth requirement applied in the document' \
                                                || no 'F7: no security requirement found'

hdr 'Summary'
printf '  passed: %s   failed: %s\n\n' "$(c '0;32' $pass)" "$(c '0;31' $fail)"
cat <<'EOT'
  Still to check by hand (cannot be done over HTTP):
   * A2/A5  docker compose ps   -> sqlserver, redis, api, worker all healthy
   * G6     docker compose logs api | grep -iE 'Dev_Passw0rd|Password=|SigningKey'
            -> must print NOTHING  (hard fail if it does)
   * F3     restart with ASPNETCORE_ENVIRONMENT=Production, force an error,
            confirm no stack trace leaks in the response
   * I2     needs a low RateLimits__Auth__PermitLimit to trip quickly (dev = 200)
   * B2     NOT closable by anyone - only one migration exists. Mark N/A.
EOT
