# Watch Party rendezvous — deploy to any small cloud box for room-code-only
# joins over the internet. Build from the repo's v2/ directory:
#   docker build -f src/Rendezvous/Dockerfile -t videoplayer-rendezvous .
#   docker run -d -p 5555:5555 --restart unless-stopped videoplayer-rendezvous
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Protocol/ Protocol/
COPY src/Rendezvous/ Rendezvous/
RUN dotnet publish Rendezvous/VideoPlayer.Rendezvous.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is only here for the container healthcheck — the runtime image ships
# with neither curl nor wget, so without it the check can never pass.
#
# ffmpeg brings ffprobe with it, and the catalogue scanner uses both: ffprobe
# for runtimes and codecs, ffmpeg for poster frames. Neither is required — a
# library without them still lists and streams, it just shows names and sizes
# and no artwork. It costs roughly 200 MB of image, which is the price of the
# library looking like a library.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ffmpeg \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV PORT=5555
EXPOSE 5555
ENTRYPOINT ["dotnet", "VideoPlayer.Rendezvous.dll"]
