#!/usr/bin/env bash
# Installs a hotlab-gateway .deb in a clean Debian system (run as root, e.g. in a
# debian:12 or debian:13 container) and checks it: the service account and file permissions,
# the systemd unit, that the binary runs with only the declared dependencies installed, then
# (after installing lintian) lintian, and remove/purge behaviour.
#
#   docker run --rm -v "$PWD:/work" -w /work debian:12 scripts/check-deb.sh artifacts/hotlab-gateway_*.deb
set -euo pipefail

deb=$(readlink -f "${1:?usage: $0 <package.deb>}")
export DEBIAN_FRONTEND=noninteractive

step() { echo "::group::$*"; }
endstep() { echo "::endgroup::"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

step "Install (pulls in only the declared dependencies)"
apt-get update -qq
apt-get install -y -qq --no-install-recommends "$deb"
endstep

step "Account and permissions"
getent passwd hotlab || fail "hotlab user missing"
getent group hotlab || fail "hotlab group missing"
[ "$(stat -c '%U:%G %a' /etc/hotlab)" = "root:hotlab 2750" ] || fail "/etc/hotlab: $(stat -c '%U:%G %a' /etc/hotlab)"
[ "$(stat -c '%U:%G %a' /etc/hotlab/users.json)" = "root:hotlab 640" ] ||
	fail "users.json: $(stat -c '%U:%G %a' /etc/hotlab/users.json)"
[ "$(readlink -f /usr/bin/hotlab-gateway)" = /usr/lib/hotlab-gateway/Hotlab.Gateway ] || fail "symlink"
dpkg-query -W -f='${Conffiles}\n' hotlab-gateway | grep -q '/etc/hotlab/appsettings.json' || fail "conffile"
endstep

step "systemd unit"
# --man=no: man(1) is not a declared dependency; lintian checks the man page later.
systemd-analyze verify --man=no /usr/lib/systemd/system/hotlab-gateway.service
systemd-analyze security --offline=yes /usr/lib/systemd/system/hotlab-gateway.service || true
endstep

step "Runs as hotlab with credentials from \$CREDENTIALS_DIRECTORY"
creds=$(mktemp -d)
head -c 32 /dev/urandom | base64 > "$creds/Hotlab__Tokens__SigningKey"
echo "not-a-real-secret" > "$creds/Hotlab__Proxmox__TokenSecret"
chown -R hotlab:hotlab "$creds"
as_hotlab() { runuser -u hotlab -- env CREDENTIALS_DIRECTORY="$creds" "$@"; }

rdp=$(as_hotlab hotlab-gateway mint-rdp alice --address gw.example.org)
grep -q '^loadbalanceinfo:s:' <<<"$rdp" || fail "mint-rdp output: $rdp"

# The listener: config from /etc/hotlab plus credentials; an unprivileged port because the
# capability to bind 3389 comes from the systemd unit.
as_hotlab Hotlab__Gateway__Listen=127.0.0.1:13389 HOTLAB_USERS_FILE=/etc/hotlab/users.json hotlab-gateway > "$creds/listener.log" 2>&1 &
pid=$!
for _ in $(seq 1 60); do
	if (exec 3<>/dev/tcp/127.0.0.1/13389) 2>/dev/null; then
		listening=1
		break
	fi
	kill -0 "$pid" 2>/dev/null || break
	sleep 0.5
done
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
cat "$creds/listener.log"
[ "${listening:-0}" = 1 ] || fail "listener did not start"
grep -q 'Loading user map from /etc/hotlab/users.json' "$creds/listener.log" || fail "users file not loaded"
rm -rf "$creds"
endstep

step "lintian"
# Installed only now, after the smoke tests, so that lintian's own dependencies can't mask a
# missing runtime dependency of the package.
apt-get install -y -qq --no-install-recommends lintian >/dev/null
lintian --version
# Warnings fail the check as well as errors; overrides are in packaging/lintian-overrides.
lintian --fail-on error,warning --display-info --info "$deb"
endstep

step "remove keeps /etc/hotlab, purge deletes it"
apt-get remove -y -qq hotlab-gateway
[ -f /etc/hotlab/appsettings.json ] && [ -f /etc/hotlab/users.json ] || fail "config removed on remove"
[ ! -e /usr/bin/hotlab-gateway ] || fail "binary left after remove"
apt-get purge -y -qq hotlab-gateway
[ ! -e /etc/hotlab ] || fail "/etc/hotlab left after purge"
! dpkg-statoverride --list /etc/hotlab >/dev/null || fail "stat override left after purge"
endstep

echo "OK: $deb"
