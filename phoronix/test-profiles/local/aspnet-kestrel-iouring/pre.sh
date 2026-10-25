#!/bin/sh
# Thin hook: the logic lives in the source bundle that install.sh unpacks to ~/pts-source (HOME is the PTS
# install directory), so it is versioned together with the server it drives.
exec bash "$HOME/pts-source/phoronix/runtime/pre.sh" "$@"
