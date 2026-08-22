FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/LiveStudio.Cloud/LiveStudio.Cloud/LiveStudio.Cloud.csproj
RUN dotnet publish src/LiveStudio.Cloud/LiveStudio.Cloud/LiveStudio.Cloud.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && apt-get clean \
    && find /var/lib/apt/lists -type f -delete \
    && mkdir -p /var/lib/livestudio/keys \
    && chown -R app:app /var/lib/livestudio
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "LiveStudio.Cloud.dll"]
