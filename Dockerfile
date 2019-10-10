FROM mcr.microsoft.com/dotnet/core/sdk:2.2-alpine AS sdk
WORKDIR /app
ARG config=Release

COPY src ./

## Note ../out -> out in net core 3
RUN dotnet publish -c $config -o ../out

FROM mcr.microsoft.com/dotnet/core/aspnet:2.2-alpine
WORKDIR /app
COPY --from=sdk /app/out/* ./

# Docker Entry
ENTRYPOINT ["dotnet", "RadarrAPI.dll"]
