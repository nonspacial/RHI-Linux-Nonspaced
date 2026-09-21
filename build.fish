#!/usr/bin/env fish
set -l root (cd (dirname (status --current-filename)); and pwd)
exec bash "$root/build.sh" $argv
