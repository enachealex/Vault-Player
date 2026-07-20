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
WORKDIR /app
COPY --from=build /app .
ENV PORT=5555
EXPOSE 5555
ENTRYPOINT ["dotnet", "VideoPlayer.Rendezvous.dll"]
