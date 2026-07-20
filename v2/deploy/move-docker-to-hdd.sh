#!/usr/bin/env bash
#
# Move Docker's storage off the small eMMC root filesystem onto the 1.8 TB HDD.
#
#   sudo bash move-docker-to-hdd.sh
#
# THIS IS ONLY HALF THE JOB on Docker 23+ with the containerd snapshotter.
# Docker's data root holds container state and metadata; the image layers live
# under containerd's own root instead. Check which applies to you:
#
#     docker info --format '{{.Driver}}'
#
# "overlayfs" means the containerd snapshotter, and you also need
# move-containerd-to-hdd.sh -- that is where the bulk of the space actually is.
# "overlay2" is the older layout, where this script alone is enough.
#
# What it does, and why each part matters:
#
#   * Stops Docker, so nothing writes while the data is copied. Every container
#     goes down for the duration -- including Postgres. Expect a few minutes.
#   * Copies /var/lib/docker with -aHAX. The hardlinks (-H), ACLs (-A) and
#     extended attributes (-X) are not optional: overlay2 relies on all three,
#     and a plain cp will produce an installation that looks fine until an
#     image layer misbehaves.
#   * Points the daemon at the new location.
#   * Tells systemd that Docker requires the HDD mount. Without this, a reboot
#     that starts Docker before the disk mounts leaves it with an empty data
#     root -- every container and volume appears to have vanished, and Docker
#     helpfully creates a fresh empty directory on the eMMC.
#   * Leaves the original data in place. Nothing is deleted. Verify first, then
#     remove it yourself with the command printed at the end.
#
set -euo pipefail

HDD=/mnt/retroboard-data
NEW_ROOT="$HDD/docker"
OLD_ROOT=/var/lib/docker

if [[ $EUID -ne 0 ]]; then echo "Run with sudo."; exit 1; fi

echo "==> Preflight"
mountpoint -q "$HDD" || { echo "FAIL: $HDD is not mounted."; exit 1; }

current_root=$(docker info --format '{{.DockerRootDir}}' 2>/dev/null || echo "$OLD_ROOT")
if [[ "$current_root" == "$NEW_ROOT" ]]; then
    echo "Docker is already using $NEW_ROOT -- nothing to do."; exit 0
fi

need=$(du -sk "$OLD_ROOT" | cut -f1)
free=$(df -Pk "$HDD" | awk 'NR==2 {print $4}')
echo "    need $((need/1024)) MB, free on HDD $((free/1024)) MB"
[[ $free -gt $((need + 2*1024*1024)) ]] || { echo "FAIL: not enough room on the HDD."; exit 1; }

echo "    running containers: $(docker ps -q | wc -l)"
docker ps --format '      {{.Names}}' || true

read -rp "Stop all of these and migrate? [y/N] " ok
[[ "$ok" == "y" || "$ok" == "Y" ]] || { echo "Aborted."; exit 1; }

echo "==> Stopping Docker"
systemctl stop docker.socket docker.service
sleep 3
pgrep -x dockerd >/dev/null && { echo "FAIL: dockerd still running."; exit 1; }

echo "==> Copying $OLD_ROOT -> $NEW_ROOT (this is the slow part)"
mkdir -p "$NEW_ROOT"
rsync -aHAX --info=progress2 "$OLD_ROOT/" "$NEW_ROOT/"

echo "==> Verifying the copy"
src=$(du -sk "$OLD_ROOT" | cut -f1)
dst=$(du -sk "$NEW_ROOT" | cut -f1)
echo "    source $((src/1024)) MB, copy $((dst/1024)) MB"
# Allow small drift: du accounting differs across filesystems.
if [[ $dst -lt $((src * 95 / 100)) ]]; then
    echo "FAIL: copy looks short. Original untouched; investigate before retrying."
    exit 1
fi

echo "==> Pointing the daemon at the HDD"
mkdir -p /etc/docker
if [[ -f /etc/docker/daemon.json ]]; then
    cp /etc/docker/daemon.json "/etc/docker/daemon.json.bak.$(date +%s)"
    tmp=$(mktemp)
    # Preserve any existing settings rather than overwriting the file.
    if command -v jq >/dev/null; then
        jq --arg r "$NEW_ROOT" '. + {"data-root": $r}' /etc/docker/daemon.json > "$tmp"
        mv "$tmp" /etc/docker/daemon.json
    else
        echo "jq not installed -- edit /etc/docker/daemon.json by hand and add:"
        echo "    \"data-root\": \"$NEW_ROOT\""
        exit 1
    fi
else
    printf '{\n  "data-root": "%s"\n}\n' "$NEW_ROOT" > /etc/docker/daemon.json
fi

echo "==> Making Docker wait for the HDD at boot"
mkdir -p /etc/systemd/system/docker.service.d
cat > /etc/systemd/system/docker.service.d/hdd-data-root.conf <<EOF
[Unit]
# Docker must not start before the disk holding its data is mounted.
RequiresMountsFor=$HDD
EOF
systemctl daemon-reload

echo "==> Starting Docker"
systemctl start docker
sleep 5

root_now=$(docker info --format '{{.DockerRootDir}}')
echo "    data root is now: $root_now"
[[ "$root_now" == "$NEW_ROOT" ]] || { echo "FAIL: daemon did not pick up the new root."; exit 1; }

echo "==> Containers"
docker ps --format '      {{.Names}}  {{.Status}}'

cat <<EOF

Done. Root filesystem before/after:
$(df -h / | tail -1)

The old data is still at $OLD_ROOT, using $((src/1024)) MB.
Leave it until you have confirmed every service works -- especially that
Supabase's Postgres has its data. Then reclaim the space with:

    sudo rm -rf $OLD_ROOT

EOF
