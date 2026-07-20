#!/usr/bin/env bash
#
# Move containerd's storage onto the HDD.
#
#   sudo bash move-containerd-to-hdd.sh
#
# Run this AFTER move-docker-to-hdd.sh. That script moved Docker's data root,
# which on Docker 29 holds container state and metadata but NOT image layers:
# with the containerd snapshotter, the layers live under containerd's own root.
# Moving Docker alone shifts well under a gigabyte and leaves the bulk behind --
# which is exactly what happened here.
#
# Same safety properties as the first script: stops everything cleanly, copies
# with -aHAX because overlayfs depends on hardlinks, ACLs and xattrs, adds a
# systemd guard so containerd cannot start before the disk is mounted, verifies,
# and never deletes the original.
#
set -euo pipefail

HDD=/mnt/retroboard-data
NEW_ROOT="$HDD/containerd"
OLD_ROOT=/var/lib/containerd
CONFIG=/etc/containerd/config.toml

if [[ $EUID -ne 0 ]]; then echo "Run with sudo."; exit 1; fi

echo "==> Preflight"
mountpoint -q "$HDD" || { echo "FAIL: $HDD is not mounted."; exit 1; }

current=$(containerd config dump 2>/dev/null | awk -F"'" '/^root =/ {print $2}')
if [[ "$current" == "$NEW_ROOT" ]]; then
    echo "containerd already uses $NEW_ROOT -- nothing to do."; exit 0
fi

need=$(du -sk "$OLD_ROOT" | cut -f1)
free=$(df -Pk "$HDD" | awk 'NR==2 {print $4}')
echo "    moving $((need/1024/1024)) GB, free on HDD $((free/1024/1024)) GB"
[[ $free -gt $((need + 2*1024*1024)) ]] || { echo "FAIL: not enough room."; exit 1; }

echo "    running containers: $(docker ps -q | wc -l)"
read -rp "Stop Docker and containerd, then migrate? [y/N] " ok
[[ "$ok" == "y" || "$ok" == "Y" ]] || { echo "Aborted."; exit 1; }

echo "==> Stopping Docker and containerd"
systemctl stop docker.socket docker.service
systemctl stop containerd
sleep 3
pgrep -x containerd >/dev/null && { echo "FAIL: containerd still running."; exit 1; }

echo "==> Copying $OLD_ROOT -> $NEW_ROOT (this is the slow part)"
mkdir -p "$NEW_ROOT"
rsync -aHAX --info=progress2 "$OLD_ROOT/" "$NEW_ROOT/"

echo "==> Verifying"
src=$(du -sk "$OLD_ROOT" | cut -f1)
dst=$(du -sk "$NEW_ROOT" | cut -f1)
echo "    source $((src/1024)) MB, copy $((dst/1024)) MB"
if [[ $dst -lt $((src * 95 / 100)) ]]; then
    echo "FAIL: copy looks short. Original untouched; nothing has been reconfigured."
    echo "Restart with: systemctl start containerd && systemctl start docker"
    exit 1
fi

echo "==> Pointing containerd at the HDD"
cp "$CONFIG" "$CONFIG.bak.$(date +%s)"
if grep -qE "^\s*root\s*=" "$CONFIG"; then
    sed -i -E "s|^\s*root\s*=.*|root = '$NEW_ROOT'|" "$CONFIG"
else
    # No explicit root (it was using the built-in default), so add one. It must
    # be top level, before any [section] header.
    sed -i "1i root = '$NEW_ROOT'" "$CONFIG"
fi
grep -E "^root" "$CONFIG"

echo "==> Making containerd wait for the HDD at boot"
mkdir -p /etc/systemd/system/containerd.service.d
cat > /etc/systemd/system/containerd.service.d/hdd-root.conf <<EOF
[Unit]
# containerd must not start before the disk holding its images is mounted.
RequiresMountsFor=$HDD
EOF
systemctl daemon-reload

echo "==> Starting containerd, then Docker"
systemctl start containerd
sleep 3
systemctl start docker
sleep 5

now=$(containerd config dump 2>/dev/null | awk -F"'" '/^root =/ {print $2}')
echo "    containerd root is now: $now"
[[ "$now" == "$NEW_ROOT" ]] || { echo "FAIL: containerd did not pick up the new root."; exit 1; }

echo "==> Containers"
docker ps --format '      {{.Names}}  {{.Status}}'

cat <<EOF

Root filesystem now:
$(df -h / | tail -1)
HDD now:
$(df -h $HDD | tail -1)

Both old copies are still in place and still using space:
    $OLD_ROOT      ($((src/1024)) MB)
    /var/lib/docker            (from the earlier migration)

Leave them until every service has been confirmed working -- especially that
Supabase's Postgres still has its data. Then reclaim with:

    sudo rm -rf $OLD_ROOT /var/lib/docker

EOF
