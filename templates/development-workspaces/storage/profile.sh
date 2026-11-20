# Minicloud storage tiers: login shells of the coding user (UID 2000) use the same scratch-disk
# cache and temporary directories as minicloud-t3.service. Other users, including root, are unchanged.
if [ "$(id -u 2>/dev/null)" = 2000 ] && [ -r /opt/minicloud-workspace/scratch.env ]; then
    while IFS='=' read -r minicloud_key minicloud_value; do
        case "$minicloud_key" in
            ''|'#'*) ;;
            *) export "$minicloud_key=$minicloud_value" ;;
        esac
    done < /opt/minicloud-workspace/scratch.env
    unset minicloud_key minicloud_value
fi
