#!/usr/bin/env bash
# Builds the hotlab-gateway binary .deb from a self-contained single-file publish:
#
#   dotnet publish src/Hotlab.Gateway -c Release -r linux-x64 --self-contained \
#       -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o out/publish-amd64
#   scripts/build-deb.sh --version 1.2.3 --arch amd64 --publish-dir out/publish-amd64 --out-dir out
#
# Needs bash, GNU coreutils/findutils, gzip and dpkg-deb (Debian/Ubuntu). No debhelper.
# Maintainer scripts use deb-systemd-helper/deb-systemd-invoke from the essential
# init-system-helpers package.
set -euo pipefail

PACKAGE=hotlab-gateway
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PACKAGING="$REPO_ROOT/packaging"

usage() {
	echo "Usage: $0 --version <debian version> --arch <amd64|arm64> --publish-dir <dir> [--out-dir <dir>] [--maintainer 'Name <email>']" >&2
	exit 2
}

version='' arch='' publish_dir='' out_dir="$REPO_ROOT/artifacts" maintainer=''
while [ $# -gt 0 ]; do
	case "$1" in
		--version) version=${2:-}; shift 2 ;;
		--arch) arch=${2:-}; shift 2 ;;
		--publish-dir) publish_dir=${2:-}; shift 2 ;;
		--out-dir) out_dir=${2:-}; shift 2 ;;
		--maintainer) maintainer=${2:-}; shift 2 ;;
		*) usage ;;
	esac
done
[ -n "$version" ] && [ -n "$arch" ] && [ -n "$publish_dir" ] || usage

# Upstream version only (no Debian revision): digits first, then [A-Za-z0-9.+~].
if ! [[ "$version" =~ ^[0-9][A-Za-z0-9.+~]*$ ]]; then
	echo "error: '$version' is not a valid version for this package" >&2
	exit 2
fi
case "$arch" in amd64 | arm64) ;; *) echo "error: unsupported arch '$arch'" >&2; exit 2 ;; esac

binary="$publish_dir/Hotlab.Gateway"
[ -x "$binary" ] || { echo "error: $binary not found or not executable" >&2; exit 1; }
[ -f "$publish_dir/appsettings.json" ] || { echo "error: $publish_dir/appsettings.json not found" >&2; exit 1; }
# A single-file publish has exactly these files (plus .pdb symbols if DebugType was left on).
# Anything else (e.g. native libraries left beside the binary) would be missing from the package.
unexpected=$(find "$publish_dir" -mindepth 1 ! -name Hotlab.Gateway ! -name appsettings.json ! -name '*.pdb' -print)
if [ -n "$unexpected" ]; then
	echo "error: unexpected files in $publish_dir (not a single-file publish?):" >&2
	echo "$unexpected" >&2
	exit 1
fi

if [ -z "$maintainer" ]; then
	maintainer=$(git -C "$REPO_ROOT" log -1 --format='%an <%ae>')
fi
# Reproducible timestamps: the commit time unless the caller set SOURCE_DATE_EPOCH.
SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$REPO_ROOT" log -1 --format=%ct)}
export SOURCE_DATE_EPOCH
commit=$(git -C "$REPO_ROOT" rev-parse --short=12 HEAD)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
root="$work/root"
umask 022

install -d "$root/DEBIAN" \
	"$root/usr/bin" \
	"$root/usr/lib/$PACKAGE" \
	"$root/usr/lib/systemd/system" \
	"$root/usr/lib/sysusers.d" \
	"$root/usr/share/doc/$PACKAGE" \
	"$root/usr/share/lintian/overrides" \
	"$root/usr/share/man/man1" \
	"$root/etc/hotlab"

install -m 0755 "$binary" "$root/usr/lib/$PACKAGE/Hotlab.Gateway"
install -m 0644 "$publish_dir/appsettings.json" "$root/usr/lib/$PACKAGE/appsettings.json"
ln -s "../lib/$PACKAGE/Hotlab.Gateway" "$root/usr/bin/$PACKAGE"

install -m 0644 "$PACKAGING/appsettings.json" "$root/etc/hotlab/appsettings.json"
install -m 0644 "$PACKAGING/$PACKAGE.service" "$root/usr/lib/systemd/system/$PACKAGE.service"
install -m 0644 "$PACKAGING/$PACKAGE.sysusers" "$root/usr/lib/sysusers.d/$PACKAGE.conf"
install -m 0644 "$PACKAGING/lintian-overrides" "$root/usr/share/lintian/overrides/$PACKAGE"
install -m 0644 "$PACKAGING/copyright" "$root/usr/share/doc/$PACKAGE/copyright"
sed -e "s/@DATE@/$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%d)/" -e "s/@VERSION@/$version/" \
	"$PACKAGING/$PACKAGE.1" | gzip -9n > "$root/usr/share/man/man1/$PACKAGE.1.gz"
gzip -9n -c "$REPO_ROOT/README.md" > "$root/usr/share/doc/$PACKAGE/README.md.gz"

changelog_date=$(date -u -R -d "@$SOURCE_DATE_EPOCH")
cat > "$work/changelog" <<EOF
$PACKAGE ($version) unstable; urgency=medium

  * Build of commit $commit.

 -- $maintainer  $changelog_date
EOF
gzip -9n -c "$work/changelog" > "$root/usr/share/doc/$PACKAGE/changelog.gz"
chmod 0644 "$root/usr/share/man/man1/$PACKAGE.1.gz" "$root/usr/share/doc/$PACKAGE/"*.gz

for script in postinst prerm postrm; do
	install -m 0755 "$PACKAGING/maintscripts/$script" "$root/DEBIAN/$script"
done
echo "/etc/hotlab/appsettings.json" > "$root/DEBIAN/conffiles"
chmod 0644 "$root/DEBIAN/conffiles"

# md5sums lists every file except conffiles (dpkg tracks those itself), like dh_md5sums.
(cd "$root" && find . -path ./DEBIAN -prune -o -path ./etc -prune -o -type f -printf '%P\0' |
	LC_ALL=C sort -z | xargs -0 md5sum) > "$root/DEBIAN/md5sums"
chmod 0644 "$root/DEBIAN/md5sums"

installed_size=$(du -sk --exclude=DEBIAN "$root" | cut -f1)
# Runtime libraries of a self-contained .NET 10 app on Debian 12/13 (learn.microsoft.com,
# "Install .NET on Debian", Dependencies), less: libicu (InvariantGlobalization=true),
# libgssapi-krb5-2 (only for Kerberos/Negotiate authentication, which hotlab doesn't use) and
# zlib1g (.NET 8 only). systemd-sysusers creates the service account in postinst.
cat > "$root/DEBIAN/control" <<EOF
Package: $PACKAGE
Version: $version
Architecture: $arch
Maintainer: $maintainer
Installed-Size: $installed_size
Depends: libc6, libgcc-s1, libstdc++6, libssl3t64 | libssl3, ca-certificates, tzdata, systemd | systemd-standalone-sysusers | systemd-sysusers
Section: net
Priority: optional
Homepage: https://github.com/jas88/hotlab
Description: RDP gateway that starts Proxmox VE lab VMs on demand
 Hotlab relays RDP connections on port 3389 to per-user Proxmox VE virtual
 machines, starting each VM when its user connects. Connections are routed by
 a signed token in the .rdp file or by user name.
 .
 The package also provides the deploy and teardown commands, which clone a
 source VM once per user and create guest accounts and .rdp files.
 .
 The executable is a self-contained .NET build: no .NET runtime is needed.
EOF
chmod 0644 "$root/DEBIAN/control"

find "$root" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +

mkdir -p "$out_dir"
deb="$out_dir/${PACKAGE}_${version}_${arch}.deb"
dpkg-deb --root-owner-group -Zxz --build "$root" "$deb"
echo "$deb"
