#!/usr/bin/env bash
set -euo pipefail
umask 077

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
action="${1:-status}"
mode="${2:-private}"
compose=(docker compose --project-directory "$root" --profile pipeline)
if [[ "$action" == public || "$mode" == public || "$action" == down ]]; then
    compose+=(--profile public)
fi

case "$action" in
    up|public|restart|rebuild)
        if [[ ! -f "$root/.env" ]]; then
            echo 'Configure .env from .env.example before starting.' >&2
            exit 1
        fi
        if [[ "$action" == public || "$mode" == public ]]; then
            command -v jq >/dev/null || { echo 'Install jq for public configuration validation.' >&2; exit 1; }
            "${compose[@]}" config --format json | jq -e '
                .services.caddy.environment as $proxy |
                .services.db.environment.POSTGRES_PASSWORD as $password |
                ($proxy.MEDIAHUB_DOMAIN != "localhost") and
                ($proxy.MEDIAHUB_DOMAIN != "mediahub.example.com") and
                ($proxy.MEDIAHUB_AUTH_PASSWORD_HASH | test("^\\$2[aby]\\$")) and
                ($password != "mediahub") and
                ($password != "replace_with_a_long_random_alphanumeric_password") and
                ($password | length >= 20)
            ' >/dev/null || { echo 'Public access requires a domain, bcrypt hash and strong database password.' >&2; exit 1; }
        fi
        ;;
esac

case "$action" in
    up|public|rebuild) "${compose[@]}" up -d --build --wait ;;
    restart) "${compose[@]}" up -d --force-recreate --wait ;;
    down) "${compose[@]}" down ;;
    status) "${compose[@]}" ps ;;
    logs) "${compose[@]}" logs --follow --tail 100 api worker web ;;
    config) "${compose[@]}" config --quiet ;;
    test)
        dotnet test "$root/backend/MediaHub.slnx" -c Release
        npm --prefix "$root/frontend" run build
        ;;
    backup)
        mkdir -p "$root/backups"
        destination="$root/backups/mediahub-$(date +%Y%m%d-%H%M%S).dump"
        "${compose[@]}" exec -T db pg_dump -U mediahub -d mediahub -Fc > "$destination"
        echo "Backup: $destination"
        ;;
    *) echo 'Usage: bash scripts/mediahub.sh up|public|down|restart|rebuild|status|logs|test|backup|config [public]' >&2; exit 1 ;;
esac