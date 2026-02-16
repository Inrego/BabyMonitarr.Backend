# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore (layer caching)
COPY BabyMonitarr.Backend.csproj .
RUN dotnet restore

# Install libman and restore client-side libraries (Bootstrap, jQuery, SignalR JS are gitignored)
COPY libman.json .
RUN dotnet tool install -g Microsoft.Web.LibraryManager.Cli && \
    export PATH="$PATH:/root/.dotnet/tools" && \
    libman restore

# Copy everything else and publish
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Install FFmpeg 7.x from Jellyfin repository (supports amd64 + arm64)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl gnupg && \
    curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /usr/share/keyrings/jellyfin.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/jellyfin.gpg arch=$(dpkg --print-architecture)] https://repo.jellyfin.org/debian bookworm main" \
        > /etc/apt/sources.list.d/jellyfin.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends jellyfin-ffmpeg7 && \
    apt-get purge -y curl gnupg && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

ENV LD_LIBRARY_PATH=/usr/lib/jellyfin-ffmpeg/lib

# Create non-root user
RUN groupadd -r babymonitarr && useradd -r -g babymonitarr -m babymonitarr

WORKDIR /app

# Create data directory for SQLite volume mount
RUN mkdir -p /app/data && chown -R babymonitarr:babymonitarr /app/data

COPY --from=build /app/publish .
RUN chown -R babymonitarr:babymonitarr /app

USER babymonitarr

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__DefaultConnection="Data Source=/app/data/babymonitarr.db"

ENTRYPOINT ["dotnet", "BabyMonitarr.Backend.dll"]
