FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ClassLibrary1/ClassLibrary1.csproj ClassLibrary1/
RUN dotnet restore ClassLibrary1/ClassLibrary1.csproj

COPY ClassLibrary1/ ClassLibrary1/
RUN dotnet publish ClassLibrary1/ClassLibrary1.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
RUN mkdir /app/data && chown "$APP_UID:$APP_UID" /app/data

USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "StopGraffitiKurganBot.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "StopGraffitiKurganBot.dll"]
