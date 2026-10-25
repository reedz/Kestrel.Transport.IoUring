#!/bin/bash
# Post-test hook: stop the server this profile started (by its recorded PID only, never by name).

. "$(dirname "$0")/common.sh"

cd "$PTS_HOME" || exit 0
stop_server
[ -f "$PTS_SERVER_LOG" ] && cp -f "$PTS_SERVER_LOG" "${PTS_HOME}/server-last.log"
echo 0 > "${PTS_HOME}/post-test-exit-status"
sleep 2
exit 0
