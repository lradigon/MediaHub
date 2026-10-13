#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
address="${1:?Usage: bash scripts/configure-lan.sh SERVER_LAN_IP}"
if [[ ! "$address" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'Une adresse IPv4 locale est requise.\n' >&2
    exit 1
fi
docker network inspect mediahub_default >/dev/null
docker pull caddy:2-alpine
read -r -p 'Identifiant web : ' username
if [[ ! "$username" =~ ^[a-zA-Z0-9_-]+$ ]]; then
    printf 'Identifiant invalide.\n' >&2
    exit 1
fi
read -r -s -p 'Mot de passe web (12 caracteres minimum) : ' password
printf '\n'
read -r -s -p 'Confirmer le mot de passe : ' confirmation
printf '\n'
if [[ ${#password} -lt 12 || "$password" != "$confirmation" ]]; then
    unset password confirmation
    printf 'Mot de passe trop court ou confirmation differente.\n' >&2
    exit 1
fi
hash=$(printf '%s\n' "$password" | docker run --rm -i caddy:2-alpine caddy hash-password)
unset password confirmation
if [[ ! "$hash" =~ ^\$2[aby]\$ ]]; then
    printf 'Generation du hash impossible.\n' >&2
    exit 1
fi
printf "MEDIAHUB_LAN_ADDRESS=%s\nMEDIAHUB_AUTH_USER=%s\nMEDIAHUB_AUTH_PASSWORD_HASH='%s'\n" \
    "$address" "$username" "$hash" > deploy/.env.lan
unset hash
chmod 600 deploy/.env.lan
docker compose --project-name mediahub-lan --env-file deploy/.env.lan -f deploy/compose.lan.yaml config --quiet
docker compose --project-name mediahub-lan --env-file deploy/.env.lan -f deploy/compose.lan.yaml up -d
printf 'Acces permanent : https://%s:8443\n' "$address"
printf 'Le certificat HTTPS local doit etre approuve sur chaque appareil.\n'