# One image definition for all three servers. Build with --build-arg PROJECT=WorldServer|AuthServer|StsServer
# NexusForever targets net5.0 (EF Core 5 + Pomelo alpha). It is EOL but pinned on purpose: retargeting is a code port, not a container change.
FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
ARG PROJECT
WORKDIR /src
COPY Source/ ./
RUN dotnet publish "NexusForever.${PROJECT}/NexusForever.${PROJECT}.csproj" -c Release -o /out \
 && cp "NexusForever.${PROJECT}/${PROJECT}.example.json" "/out/${PROJECT}.json"

# WorldServer hosts an embedded ASP.NET server, so use the aspnet runtime for all three.
FROM mcr.microsoft.com/dotnet/aspnet:5.0
ARG PROJECT
ENV APP_DLL="NexusForever.${PROJECT}.dll" \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=0
# /cache holds the parsed game-table cache (WorldServer writes it); /app must be writable by nf for config/logs.
RUN useradd --system --uid 10001 --no-create-home nf \
 && mkdir -p /app /cache && chown nf:nf /app /cache
WORKDIR /app
COPY --from=build --chown=nf:nf /out/ ./
USER nf
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
