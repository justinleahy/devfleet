# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PiCommandCenter.ControlPlane/PiCommandCenter.ControlPlane.csproj \
      --configuration Release --no-self-contained --output /out/control-plane \
    && dotnet publish src/PiCommandCenter.Node/PiCommandCenter.Node.csproj \
      --configuration Release --no-self-contained --output /out/node

FROM node:26-bookworm-slim AS runtime
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && apt-get update \
    && apt-get install --yes --no-install-recommends bubblewrap ca-certificates curl git libicu72 \
    && chmod u+s /usr/bin/bwrap \
    && mkdir -p /run/user \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_EnableDiagnostics=0

FROM runtime AS control-plane
WORKDIR /app/control-plane
COPY --from=build /out/control-plane/ ./
USER node
ENTRYPOINT ["dotnet", "PiCommandCenter.ControlPlane.dll"]

FROM runtime AS worker-node
WORKDIR /app/node
COPY --from=build /out/node/ ./
COPY runtime/package.json runtime/package-lock.json /app/runtime/
COPY runtime/pi-worker/ /app/runtime/pi-worker/
RUN cd /app/runtime \
    && npm ci --omit=dev --ignore-scripts \
    && chown -R node:node /app
USER node
ENTRYPOINT ["dotnet", "PiCommandCenter.Node.dll"]
