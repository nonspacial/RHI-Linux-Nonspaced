#!/bin/sh
# /usr/bin/rhi-linux from the .deb and .rpm packages: runs the self-contained build in /opt/rhi-linux.
# No PATH or LD_LIBRARY_PATH changes (the same rule as the AppImage's AppRun): the .NET host finds
# its native libraries next to the executable, and anything exported here would be inherited by
# every game, Wine and Steam process RHI starts.
exec /opt/rhi-linux/RHI "$@"
