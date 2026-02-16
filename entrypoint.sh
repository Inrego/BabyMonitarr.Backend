#!/bin/sh
# Ensure data directory exists and is writable by the app user
mkdir -p /app/data
chown babymonitarr:babymonitarr /app/data

# Drop to non-root user and start the app
exec gosu babymonitarr dotnet BabyMonitarr.Backend.dll "$@"
